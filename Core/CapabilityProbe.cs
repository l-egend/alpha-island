using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using alpha_island.Interop;

namespace alpha_island.Core
{
    /// <summary>
    /// 岛体依赖的「可选项」。任何新功能都必须先问一句 <see cref="CapabilityProbe.Check"/>：
    /// 拿不到就降级或整块隐藏，绝不能弹异常、也不能让界面停在半成品状态。
    /// </summary>
    public enum IslandCapability
    {
        None = 0,
        /// <summary>DWM 背景模糊（系统「透明效果」开关）。</summary>
        TransparentEffects = 1,
        /// <summary>Windows.Devices.Radios —— 飞行模式状态与无线电开关。</summary>
        RadiosInfo = 2,
        /// <summary>CapabilityAccessManager  ConsentStore —— 麦克风/摄像头占用（只读注册表）。</summary>
        PrivacyConsentStore = 3,
        /// <summary>对自身 EmptyWorkingSet（免提权）。</summary>
        MemoryTrimSelf = 4,
        /// <summary>整理其它进程的内存（需要管理员，默认不做）。</summary>
        MemoryTrimSystem = 5,
        /// <summary>NetworkInterface 速率统计（已排除 Loopback/Tunnel）。</summary>
        NetworkAdapterStats = 6,
        /// <summary>真出网（天气/AI 等网络功能的前提）。</summary>
        OutboundNetwork = 7,
        /// <summary>DPAPI（保存 API Key 之类敏感配置）。</summary>
        Dpapi = 8,
    }

    /// <summary>
    /// 能力状态。<see cref="Degraded"/> 与 <see cref="Available"/> 都算「能用」，
    /// 区别只是数据来源是否完整（例如透明效果关掉后仍退回纯渐变玻璃）。
    /// </summary>
    public enum CapabilityStatus
    {
        /// <summary>尚未探测（异步探测还没回来）。</summary>
        Unknown = 0,
        /// <summary>可用，走完整实现。</summary>
        Available = 1,
        /// <summary>能用但不完整，需要走兜底路径。</summary>
        Degraded = 2,
        /// <summary>被权限 / 系统设置 / 用户授权拒绝。</summary>
        Denied = 3,
        /// <summary>本机或本系统版本没有这个 API。</summary>
        Unsupported = 4,
    }

    /// <summary>一次能力探测的结论。<paramref name="Reason"/> 是给人看的，会直接显示在「关于」页。</summary>
    public readonly record struct CapabilityResult(IslandCapability Capability, CapabilityStatus Status, string Reason)
    {
        public bool IsUsable => Status == CapabilityStatus.Available || Status == CapabilityStatus.Degraded;

        public string StatusText => Status switch
        {
            CapabilityStatus.Available => "可用",
            CapabilityStatus.Degraded => "降级",
            CapabilityStatus.Denied => "被拒",
            CapabilityStatus.Unsupported => "不支持",
            _ => "未探测",
        };

        public override string ToString() => Capability + " = " + StatusText + "（" + Reason + "）";
    }

    /// <summary>
    /// 能力探测。结果带缓存：同步能力第一次 <see cref="Check"/> 时探一次，之后直接命中；
    /// 异步能力（无线电、出网）由 <see cref="WarmUpAsync"/> 在启动后台探一次。
    /// 探测本身绝不抛异常 —— 探不出东西就是一个 <see cref="CapabilityStatus"/>。
    /// </summary>
    public static class CapabilityProbe
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<IslandCapability, CapabilityResult> Cache = new();
        private static readonly HashSet<IslandCapability> InFlight = new();
        private static readonly HttpClient Http = CreateHttp();

        private static Task? _warmUp;

        /// <summary>某能力结论发生变化（含首次探测完成）时触发，参数是能力名。</summary>
        public static event Action<IslandCapability>? Changed;

        private static readonly IslandCapability[] All =
        {
            IslandCapability.TransparentEffects,
            IslandCapability.RadiosInfo,
            IslandCapability.PrivacyConsentStore,
            IslandCapability.MemoryTrimSelf,
            IslandCapability.MemoryTrimSystem,
            IslandCapability.NetworkAdapterStats,
            IslandCapability.OutboundNetwork,
            IslandCapability.Dpapi,
        };

        private static readonly IslandCapability[] AsyncCapabilities =
        {
            IslandCapability.RadiosInfo,
            IslandCapability.OutboundNetwork,
        };

        private static HttpClient CreateHttp()
        {
            var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate };
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            return http;
        }

        /// <summary>探测（或读取缓存）。<paramref name="forceRefresh"/> 为 true 时重新探一次。</summary>
        public static CapabilityResult Check(IslandCapability capability, bool forceRefresh = false)
        {
            if (capability == IslandCapability.None)
                return new CapabilityResult(capability, CapabilityStatus.Unsupported, "空能力");

            lock (Gate)
            {
                if (!forceRefresh && Cache.TryGetValue(capability, out var cached))
                    return cached;
            }

            // 异步能力不可能在这里同步等出来：踢一次后台探测（同一能力同一时刻只跑一个），
            // 本次先给上一次/Unknown 的结论。
            if (IsAsync(capability))
            {
                StartProbe(capability);

                lock (Gate)
                {
                    if (Cache.TryGetValue(capability, out var pending)) return pending;
                }

                return new CapabilityResult(capability, CapabilityStatus.Unknown, "探测中…");
            }

            CapabilityResult result = Probe(capability);
            Store(result);
            return result;
        }

        public static CapabilityStatus StatusOf(IslandCapability capability) => Check(capability).Status;

        /// <summary>是否「能用」（Available 或 Degraded）。</summary>
        public static bool CanUse(IslandCapability capability) => Check(capability).IsUsable;

        /// <summary>按枚举顺序给全部能力当前结论。不触发新的异步探测。</summary>
        public static IReadOnlyList<CapabilityResult> CheckAll()
            => All.Select(c => Check(c)).ToArray();

        /// <summary>
        /// 启动后调一次：把异步能力（无线电、出网）先探出来，顺便预热同步能力。
        /// 幂等，重复调用只跑一次。
        /// </summary>
        public static Task WarmUpAsync()
        {
            lock (Gate)
            {
                _warmUp ??= WarmUpCoreAsync();
                return _warmUp;
            }
        }

        private static async Task WarmUpCoreAsync()
        {
            try
            {
                await Task.WhenAll(AsyncCapabilities.Select(ProbeAsync)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                IslandLog.Debug("CapabilityProbe", "预热失败: " + ex.Message);
            }
        }

        /// <summary>给「关于」页和诊断导出用的多行报告。</summary>
        public static string Report()
        {
            var sb = new System.Text.StringBuilder();
            foreach (CapabilityResult r in CheckAll())
                sb.Append(r.Capability).Append(" = ").Append(r.StatusText)
                  .Append("  [").Append(r.Status.ToString()).Append("]  ").Append(r.Reason).AppendLine();
            return sb.ToString().TrimEnd();
        }

        /// <summary>同一能力同一时刻只允许一个后台探测在跑，避免被轮询调用点打成网络风暴。</summary>
        private static void StartProbe(IslandCapability capability)
        {
            lock (Gate)
            {
                if (!InFlight.Add(capability)) return;
            }

            _ = ProbeAsync(capability);
        }

        private static bool IsAsync(IslandCapability capability) => Array.IndexOf(AsyncCapabilities, capability) >= 0;

        private static void Store(CapabilityResult result)
        {
            bool changed;
            lock (Gate)
            {
                changed = !Cache.TryGetValue(result.Capability, out CapabilityResult old)
                          || old.Status != result.Status
                          || old.Reason != result.Reason;
                Cache[result.Capability] = result;
            }

            if (changed)
            {
                IslandLog.Debug("CapabilityProbe", result.ToString());
                try { Changed?.Invoke(result.Capability); }
                catch (Exception ex) { IslandLog.Error("CapabilityProbe.Changed", ex); }
            }
        }

        private static async Task ProbeAsync(IslandCapability capability)
        {
            try
            {
                CapabilityResult result = capability switch
                {
                    IslandCapability.RadiosInfo => await ProbeRadiosAsync().ConfigureAwait(false),
                    IslandCapability.OutboundNetwork => await ProbeOutboundAsync().ConfigureAwait(false),
                    _ => Probe(capability),
                };
                Store(result);
            }
            catch (Exception ex)
            {
                Store(new CapabilityResult(capability, CapabilityStatus.Unsupported, "探测异常：" + ex.GetType().Name));
            }
            finally
            {
                lock (Gate) InFlight.Remove(capability);
            }
        }

        private static CapabilityResult Probe(IslandCapability capability) => capability switch
        {
            IslandCapability.TransparentEffects => ProbeTransparentEffects(),
            IslandCapability.PrivacyConsentStore => ProbeConsentStore(),
            IslandCapability.MemoryTrimSelf => ProbeTrimSelf(),
            IslandCapability.MemoryTrimSystem => ProbeTrimSystem(),
            IslandCapability.NetworkAdapterStats => ProbeAdapters(),
            IslandCapability.Dpapi => ProbeDpapi(),
            _ => new CapabilityResult(capability, CapabilityStatus.Unsupported, "未知能力"),
        };

        private static CapabilityResult ProbeTransparentEffects()
        {
            bool enabled = BackdropWindow.SystemTransparencyEnabled();
            return enabled
                ? new CapabilityResult(IslandCapability.TransparentEffects, CapabilityStatus.Available, "系统「透明效果」开启，可用 DWM 背景模糊")
                : new CapabilityResult(IslandCapability.TransparentEffects, CapabilityStatus.Degraded,
                    "系统「透明效果」已关闭（设置 → 个性化 → 颜色），退回纯渐变玻璃；改回后需重启程序");
        }

        private static async Task<CapabilityResult> ProbeRadiosAsync()
        {
            try
            {
                var task = Windows.Devices.Radios.Radio.GetRadiosAsync().AsTask();
                Task done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(4))).ConfigureAwait(false);
                if (!ReferenceEquals(done, task))
                {
                    // 超时后别把异常丢成未观测异常。
                    _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    return new CapabilityResult(IslandCapability.RadiosInfo, CapabilityStatus.Denied, "Radio.GetRadiosAsync 超过 4 秒未返回");
                }

                var radios = await task.ConfigureAwait(false);
                int count = radios?.Count ?? 0;
                return new CapabilityResult(IslandCapability.RadiosInfo, CapabilityStatus.Available,
                    "拿到 " + count + " 个无线电设备（Wi-Fi / 蓝牙 / 移动宽带）");
            }
            catch (UnauthorizedAccessException)
            {
                return new CapabilityResult(IslandCapability.RadiosInfo, CapabilityStatus.Denied,
                    "系统拒绝访问无线电信息（需要 radios 受限能力，非商店应用通常拿不到）");
            }
            catch (Exception ex)
            {
                return new CapabilityResult(IslandCapability.RadiosInfo, CapabilityStatus.Unsupported,
                    "本机不支持 Windows.Devices.Radios：" + ex.GetType().Name);
            }
        }

        private static CapabilityResult ProbeConsentStore()
        {
            try
            {
                using RegistryKey? root = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore");
                if (root == null)
                    return new CapabilityResult(IslandCapability.PrivacyConsentStore, CapabilityStatus.Unsupported,
                        "注册表里没有 ConsentStore 根（系统版本过旧）");

                int apps = 0;
                foreach (string cap in new[] { "microphone", "camera" })
                {
                    foreach (string scope in new[] { "NonPackaged", "Packaged" })
                    {
                        using RegistryKey? sub = root.OpenSubKey(cap + "\\" + scope);
                        apps += sub?.SubKeyCount ?? 0;
                    }
                }

                return new CapabilityResult(IslandCapability.PrivacyConsentStore, CapabilityStatus.Available,
                    "ConsentStore 可读（mic/camera 共收录 " + apps + " 个应用；只读，免提权）");
            }
            catch (Exception ex)
            {
                return new CapabilityResult(IslandCapability.PrivacyConsentStore, CapabilityStatus.Denied,
                    "读 ConsentStore 失败：" + ex.GetType().Name);
            }
        }

        private static CapabilityResult ProbeTrimSelf()
        {
            try
            {
                IntPtr self = NativeMethods.GetCurrentProcess();
                if (NativeMethods.EmptyWorkingSet(self))
                    return new CapabilityResult(IslandCapability.MemoryTrimSelf, CapabilityStatus.Available,
                        "可对自身调用 EmptyWorkingSet（免提权）");

                return new CapabilityResult(IslandCapability.MemoryTrimSelf, CapabilityStatus.Degraded,
                    "EmptyWorkingSet(自身) 失败 win32=" + Marshal.GetLastWin32Error());
            }
            catch (Exception ex)
            {
                return new CapabilityResult(IslandCapability.MemoryTrimSelf, CapabilityStatus.Unsupported,
                    "psapi 不可用：" + ex.GetType().Name);
            }
        }

        private static CapabilityResult ProbeTrimSystem()
        {
            if (IsElevated())
                return new CapabilityResult(IslandCapability.MemoryTrimSystem, CapabilityStatus.Degraded,
                    "当前已提权，但仍需启用 SeProfileSingleProcessPrivilege 才能整理其它进程");

            return new CapabilityResult(IslandCapability.MemoryTrimSystem, CapabilityStatus.Denied,
                "需要管理员权限（本程序不提权，默认不做全系统内存整理）");
        }

        private static CapabilityResult ProbeAdapters()
        {
            try
            {
                List<NetworkInterface> usable = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up
                                && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                                && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .ToList();

                if (usable.Count == 0)
                    return new CapabilityResult(IslandCapability.NetworkAdapterStats, CapabilityStatus.Degraded,
                        "没有 Up 且非 Loopback/Tunnel 的网卡");

                return new CapabilityResult(IslandCapability.NetworkAdapterStats, CapabilityStatus.Available,
                    "可用网卡 " + usable.Count + " 个：" + string.Join(", ", usable.Select(n => n.Name)));
            }
            catch (Exception ex)
            {
                return new CapabilityResult(IslandCapability.NetworkAdapterStats, CapabilityStatus.Unsupported,
                    "枚举网卡失败：" + ex.GetType().Name);
            }
        }

        private static async Task<CapabilityResult> ProbeOutboundAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                using var req = new HttpRequestMessage(HttpMethod.Head, "https://music.163.com/");
                using HttpResponseMessage resp = await Http
                    .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);

                return new CapabilityResult(IslandCapability.OutboundNetwork, CapabilityStatus.Available,
                    "出网可达（music.163.com → HTTP " + (int)resp.StatusCode + "）");
            }
            catch (OperationCanceledException)
            {
                return new CapabilityResult(IslandCapability.OutboundNetwork, CapabilityStatus.Degraded,
                    "出网探测超时（6 秒），网络功能会走缓存");
            }
            catch (Exception ex)
            {
                return new CapabilityResult(IslandCapability.OutboundNetwork, CapabilityStatus.Denied,
                    "出网不可达：" + ex.GetType().Name + "（代理/VPN/离线时属正常）");
            }
        }

        private static CapabilityResult ProbeDpapi()
        {
            byte[] secret = { 0x41, 0x6C, 0x70, 0x68, 0x61 };
            IntPtr input = Marshal.AllocHGlobal(secret.Length);
            try
            {
                Marshal.Copy(secret, 0, input, secret.Length);
                var blob = new NativeMethods.DATA_BLOB { cbData = secret.Length, pbData = input };

                if (!NativeMethods.CryptProtectData(ref blob, "alpha-island-probe", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out NativeMethods.DATA_BLOB encrypted))
                    return new CapabilityResult(IslandCapability.Dpapi, CapabilityStatus.Denied,
                        "CryptProtectData 失败 win32=" + Marshal.GetLastWin32Error());

                try
                {
                    if (!NativeMethods.CryptUnprotectData(ref encrypted, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out NativeMethods.DATA_BLOB plain))
                        return new CapabilityResult(IslandCapability.Dpapi, CapabilityStatus.Denied,
                            "CryptUnprotectData 失败 win32=" + Marshal.GetLastWin32Error());

                    try
                    {
                        bool same = plain.cbData == secret.Length;
                        if (same)
                        {
                            var roundTrip = new byte[plain.cbData];
                            Marshal.Copy(plain.pbData, roundTrip, 0, plain.cbData);
                            same = roundTrip.SequenceEqual(secret);
                        }

                        return same
                            ? new CapabilityResult(IslandCapability.Dpapi, CapabilityStatus.Available, "CryptProtectData / CryptUnprotectData 往返一致")
                            : new CapabilityResult(IslandCapability.Dpapi, CapabilityStatus.Degraded, "DPAPI 往返结果与原文不一致");
                    }
                    finally
                    {
                        if (plain.pbData != IntPtr.Zero) NativeMethods.LocalFree(plain.pbData);
                    }
                }
                finally
                {
                    if (encrypted.pbData != IntPtr.Zero) NativeMethods.LocalFree(encrypted.pbData);
                }
            }
            catch (Exception ex)
            {
                return new CapabilityResult(IslandCapability.Dpapi, CapabilityStatus.Unsupported,
                    "crypt32 不可用：" + ex.GetType().Name);
            }
            finally
            {
                Marshal.FreeHGlobal(input);
            }
        }

        private static bool IsElevated()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }
}
