using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using alpha_island.Core;

namespace alpha_island.Services
{
    /// <summary>一次网速采样的结果（**字节**/秒，不是 bit）。</summary>
    public readonly record struct NetworkSpeedSample(double DownBytesPerSecond, double UpBytesPerSecond)
    {
        /// <summary>下行 Mb/s（阈值设置用的就是它 —— 用户填的是 Mbps，不是 MB/s）。</summary>
        public double DownMbps => DownBytesPerSecond * 8 / 1_000_000d;

        /// <summary>
        /// 是否值得显示。半 KB/s 以下当成"没流量"：
        /// 待机时网卡本身也在收发心跳包，照实显示会让细条上的数字永远挂在 `↓0B`。
        /// </summary>
        public bool HasTraffic => DownBytesPerSecond >= 512 || UpBytesPerSecond >= 512;
    }

    /// <summary>
    /// 网速采样。**本类不含定时器** —— 节拍由 <see cref="IslandTicker"/> 的
    /// <c>netspeed</c> 订阅提供（1 秒一拍），这里只做「选网卡 → 累计字节差分 → 算速率」。
    ///
    /// 为什么不用 <c>NetworkInterface.GetIPv4Statistics()</c> 直接读单块网卡：
    /// 一台机器同时挂着有线 / Wi-Fi / 虚拟网卡时，真实流量只走其中一块，
    /// 而"当前默认路由走哪块"用 <c>NetworkInterface</c> 拿不到；把 Up 状态的物理网卡**全部累加**
    /// 既不用判断默认路由，又天然覆盖"插网线自动切有线"的场景。
    ///
    /// 排除规则（网络接口过滤）：Loopback / Tunnel 一律不算，名字或描述里带
    /// clash / tap / wintun / vpn / hyper-v / vmware / tailscale / zerotier 的虚拟网卡也不算 ——
    /// 它们要么不承载真实流量，要么会把同一份流量再统计一遍（Clash 的 TUN 网卡尤其明显）。
    /// </summary>
    public sealed class NetworkSpeedService : IDisposable
    {
        private static readonly string[] ExcludedHints =
        {
            "loopback", "tunnel", "clash", "tap-", "wintun", "vpn", "hyper-v", "vmware",
            "virtualbox", "vbox", "tailscale", "zerotier", "teredo", "isatap", "6to4",
            "bluetooth", "虚拟", "隧道",
        };

        /// <summary>每隔这么多拍重新枚举一次网卡（网线插拔 / Wi-Fi 开关）。</summary>
        private const int AdapterRefreshTicks = 60;

        private readonly List<NetworkInterface> _adapters = new();
        private readonly List<string> _adapterNames = new();

        private long _lastBytesDown;
        private long _lastBytesUp;
        private long _lastTimestamp;
        private bool _primed;
        private int _ticksSinceRefresh;
        private bool _disposed;
        private int _adaptersDirty;

        public NetworkSpeedService()
        {
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        }
        private void OnNetworkChanged(object? sender, EventArgs e) => System.Threading.Interlocked.Exchange(ref _adaptersDirty, 1);
        private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => OnNetworkChanged(sender, e);

        /// <summary>最近一拍的速率。第一拍（建立基线）恒为 0。</summary>
        public NetworkSpeedSample Current { get; private set; }
        /// <summary>本服务启动后采得的流量，不含建立基线之前的网卡历史。</summary>
        public long SessionDownloadedBytes { get; private set; }
        public long SessionUploadedBytes { get; private set; }
        internal long ObservedDownloadedBytes { get; private set; }
        internal long ObservedUploadedBytes { get; private set; }
        public double SessionPeakDownload { get; private set; }
        public double SessionPeakUpload { get; private set; }

        /// <summary>正在统计的网卡名（诊断页 / 日志用）。</summary>
        public IReadOnlyList<string> AdapterNames => _adapterNames;

        /// <summary>非空 = 选网卡或读计数器失败过，内容可直接显示在诊断页。</summary>
        public string DegradedReason { get; private set; } = string.Empty;
        internal void ResetSession()
        {
            _primed = false; Current = default;
            SessionDownloadedBytes = SessionUploadedBytes = 0; SessionPeakDownload = SessionPeakUpload = 0;
        }

        /// <summary>采一拍。由 ticker 每秒调一次；手动再调一次不会污染速率（见下面的时间间隔保护）。</summary>
        public void Sample()
        {
            if (_disposed) return;

            if (System.Threading.Interlocked.Exchange(ref _adaptersDirty, 0) != 0 ||
                _adapters.Count == 0 || ++_ticksSinceRefresh >= AdapterRefreshTicks)
            {
                RefreshAdapters();
                _ticksSinceRefresh = 0;
            }

            long down = 0;
            long up = 0;
            bool countersComplete = true;
            foreach (NetworkInterface adapter in _adapters)
            {
                try
                {
                    IPv4InterfaceStatistics stats = adapter.GetIPv4Statistics();
                    down += stats.BytesReceived;
                    up += stats.BytesSent;
                }
                catch
                {
                    countersComplete = false;
                    // 网卡恰好在这一拍被拔掉/禁用会抛：跳过它，下一拍重建列表
                    _ticksSinceRefresh = AdapterRefreshTicks;
                }
            }

            if (!countersComplete)
            {
                _primed = false;
                Current = default;
                return; // 部分计数不可用时重新建基线，避免把网卡历史混入本次累计。
            }

            long now = Stopwatch.GetTimestamp();
            if (!_primed)
            {
                // 建立基线：累计字节数是"开机以来的总量"，直接差分等于把整段历史当成 1 秒的流量
                _lastBytesDown = down;
                _lastBytesUp = up;
                _lastTimestamp = now;
                _primed = true;
                Current = default;
                return;
            }

            double seconds = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
            if (seconds < 0.05)
            {
                // 同一瞬间被调了两次（改设置后 TriggerNow 会和定时拍撞上），这一次不算数
                return;
            }

            long deltaDown = down - _lastBytesDown;
            long deltaUp = up - _lastBytesUp;
            _lastBytesDown = down;
            _lastBytesUp = up;
            _lastTimestamp = now;

            ApplyByteDeltas(deltaDown, deltaUp, seconds);
        }

        /// <summary>累计只使用实际字节差分；网卡重建的基线与负差分不计入流量。</summary>
        internal void ApplyByteDeltas(long deltaDown, long deltaUp, double seconds)
        {
            if (!double.IsFinite(seconds) || seconds < 0.05) return;
            deltaDown = Math.Max(0, deltaDown);
            deltaUp = Math.Max(0, deltaUp);
            Current = new NetworkSpeedSample(deltaDown / seconds, deltaUp / seconds);
            ObservedDownloadedBytes = deltaDown > long.MaxValue - ObservedDownloadedBytes ? long.MaxValue : ObservedDownloadedBytes + deltaDown;
            ObservedUploadedBytes = deltaUp > long.MaxValue - ObservedUploadedBytes ? long.MaxValue : ObservedUploadedBytes + deltaUp;
            SessionDownloadedBytes += deltaDown;
            SessionUploadedBytes += deltaUp;
            SessionPeakDownload = Math.Max(SessionPeakDownload, Current.DownBytesPerSecond);
            SessionPeakUpload = Math.Max(SessionPeakUpload, Current.UpBytesPerSecond);
        }

        /// <summary>重新枚举网卡。网卡集合真的变了才重置基线（否则每 60 拍会出现一次 0）。</summary>
        public void RefreshAdapters()
        {
            List<NetworkInterface> found = new();
            List<string> names = new();

            try
            {
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                    if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    if (IsExcluded(adapter.Name) || IsExcluded(adapter.Description)) continue;

                    found.Add(adapter);
                    names.Add($"{adapter.Name}（{adapter.NetworkInterfaceType}）");
                }

                DegradedReason = string.Empty;
            }
            catch (Exception ex)
            {
                DegradedReason = ex.GetType().Name + ": " + ex.Message;
                IslandLog.Error("NetworkSpeed", ex);
            }

            if (SameAdapters(found))
            {
                return; // 集合没变，保持基线，别制造人工的 0
            }

            _adapters.Clear();
            _adapters.AddRange(found);
            _adapterNames.Clear();
            _adapterNames.AddRange(names);
            _primed = false;

            IslandLog.Debug("NetworkSpeed",
                _adapters.Count == 0
                    ? "当前没有可用于统计的网卡（降级：细条不显示网速）"
                    : "统计网卡：" + string.Join("、", _adapterNames));
        }

        private bool SameAdapters(List<NetworkInterface> candidate)
        {
            if (candidate.Count != _adapters.Count) return false;
            for (int i = 0; i < candidate.Count; i++)
            {
                if (!string.Equals(candidate[i].Id, _adapters[i].Id, StringComparison.Ordinal)) return false;
            }

            return true;
        }

        private static bool IsExcluded(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            var lower = text.ToLowerInvariant();
            foreach (string hint in ExcludedHints)
            {
                if (lower.Contains(hint, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>细条上用的紧凑写法：`1.2M` / `860K` / `12B`（不带单位后缀，箭头在外面）。</summary>
        public static string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond >= 1_048_576) return (bytesPerSecond / 1_048_576).ToString("0.#", CultureInfo.InvariantCulture) + "M";
            if (bytesPerSecond >= 1024) return (bytesPerSecond / 1024).ToString("0.#", CultureInfo.InvariantCulture) + "K";
            return ((long)Math.Max(0, bytesPerSecond)).ToString(CultureInfo.InvariantCulture) + "B";
        }

        /// <summary>提示胶囊里用的完整写法：`1.2 MB/s`。</summary>
        public static string FormatSpeedFull(double bytesPerSecond)
        {
            if (bytesPerSecond >= 1_048_576) return (bytesPerSecond / 1_048_576).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s";
            if (bytesPerSecond >= 1024) return (bytesPerSecond / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " KB/s";
            return ((long)Math.Max(0, bytesPerSecond)).ToString(CultureInfo.InvariantCulture) + " B/s";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
            _adapters.Clear();
            _adapterNames.Clear();
            Current = default;
        }
    }
}
