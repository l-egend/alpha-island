using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace alpha_island.Services
{
    /// <summary>系统音量变更事件数据。</summary>
    public sealed record VolumeInfo(int Percent, bool IsMuted);

    /// <summary>音频输出设备信息。</summary>
    public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault)
    {
        public System.Windows.Visibility CheckVisibility => IsDefault ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    /// <summary>带采样时间的不可变快照；后台阻塞时不能把旧电平当成连续有声或无声。</summary>
    internal sealed record AudioPeakSnapshot(IReadOnlyDictionary<string, float> Peaks, long SampledAtMs)
    {
        public float Read(string processName, long nowMs)
        {
            if (nowMs < SampledAtMs || nowMs - SampledAtMs > 500 || string.IsNullOrWhiteSpace(processName)) return -1f;
            string key = processName.Trim();
            int slash = key.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0) key = key[(slash + 1)..];
            if (!key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) key += ".exe";
            return Peaks.TryGetValue(key, out float peak) && float.IsFinite(peak) && peak is >= 0 and <= 1 ? peak : -1f;
        }
    }

    /// <summary>音频应用会话信息。</summary>
    public sealed class AudioAppSessionViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private string _name = "";
        private int _volumePercent = 100;
        private bool _isMuted;
        public int ProcessId { get; set; }
        public string Name { get => _name; set { if (_name == value) return; _name = value; Changed(nameof(Name)); } }
        public int VolumePercent
        {
            get => _volumePercent;
            set { value = Math.Clamp(value, 0, 100); if (_volumePercent == value) return;
                _volumePercent = value; Changed(nameof(VolumePercent)); Changed(nameof(PercentText)); }
        }
        public string PercentText => $"{VolumePercent}%";
        public bool IsMuted
        {
            get => _isMuted;
            set { if (_isMuted == value) return; _isMuted = value;
                Changed(nameof(IsMuted)); Changed(nameof(MuteText)); Changed(nameof(MuteColorBrush)); }
        }
        private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
        public string MuteText => IsMuted ? "已静音" : "静音";
        public System.Windows.Media.Brush MuteColorBrush => IsMuted
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x55, 0x55))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xE5, 0xFF));
    }

    /// <summary>
    /// Windows 系统主音量监听服务。
    ///
    /// 核心职责：
    ///  1. 基于 NAudio CoreAudioApi 挂钩当前默认音频输出端点的 <see cref="AudioEndpointVolume"/>；
    ///  2. 监听 <c>OnVolumeNotification</c>，在系统音量或静音状态发生变化时即刻派发事件；
    ///  3. 监听 <see cref="IMMNotificationClient.OnDefaultDeviceChanged"/>，默认音频设备热插拔切换时自动重新绑定；
    ///  4. 支持枚举与快捷切换音频输出设备、枚举应用会话与快速静音。
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public sealed class VolumeService : IDisposable, IMMNotificationClient
    {
        private readonly Dispatcher _dispatcher;
        private readonly object _lock = new();

        private MMDeviceEnumerator? _enumerator;
        private MMDevice? _currentDevice;
        private AudioSessionManager? _audioSessionManager;
        private volatile AudioEndpointVolume? _volume;
        private volatile bool _disposed;
        private bool _hasInitialValue;
        private volatile int _lastPercent = -1;
        private volatile bool _lastMuted;

        /// <summary>当前默认端点的 ID / 友好名（绑定瞬间缓存下来）。</summary>
        /// <remarks>
        /// UI 线程只读这两个字符串，永远不碰 <c>MMDevice</c> 对象：设备对象的重绑 / 释放都发生在
        /// 后台刷新线程上，UI 若同时去读 <c>FriendlyName</c>（COM 调用）就会撞上"对象正在被
        /// Dispose"的窗口 —— 轻则异常，重则整条 UI 线程卡在跨单元调用里（用户报的"点一下就卡死"）。
        /// </remarks>
        private volatile string? _currentDeviceId;
        private volatile string? _currentDeviceName;

        /// <summary>
        /// 待由后台线程落地的写入请求。UI 线程只写这几个变量（Interlocked / volatile，无锁无 COM），
        /// 真正的 COM 写入全部在后台刷新线程上执行 —— 这样调音量时 UI 永远不会被枚举 / 重绑阻塞，
        /// 数值先用乐观值立刻显示（用户报的"音量延迟"就是 UI 在等锁 + 等 COM 往返）。
        /// </summary>
        private int _pendingVolumePercent = -1;
        private int _pendingMuteToggle;
        private int _pendingUrgent;

        /// <summary>最近一次"本进程自己写下去"的目标值与时刻，用于丢弃设置之后迟到的旧通知。</summary>
        private int _localWritePercent = -1;
        private volatile bool _localWriteMuted;
        private long _localWriteTick;

        /// <summary>
        /// 设备 / 应用会话列表的缓存。列表内容只由后台刷新线程写入，UI 线程只读这份缓存 ——
        /// <para>
        /// 绝不能在 UI(STA) 线程上枚举 COM：<c>MMDeviceEnumerator</c> 是在后台 MTA 线程上创建的，
        /// 从 STA 调 <c>EnumerateAudioEndPoints</c> 时返回的对象是跨单元代理，
        /// QueryInterface(<c>IMMDevice</c>) 会直接失败（InvalidCastException … 0x80004002 E_NOINTERFACE），
        /// 表现为"输出设备 / 音量合成器两个面板展开后永远是空的，点也点不动"。
        /// </para>
        /// </summary>
        private readonly object _cacheLock = new object();
        private List<AudioDeviceInfo> _deviceCache = new List<AudioDeviceInfo>();
        private List<AudioAppSessionViewModel> _sessionCache = new List<AudioAppSessionViewModel>();

        /// <summary>
        /// 各进程音频会话的峰值（进程名→峰值，0~1）。由后台刷新线程在枚举会话时顺手采样，
        /// UI 线程只读这份快照（跨单元调用会直接失败，见 <see cref="_cacheLock"/> 的说明）。
        /// 用途：判断"这个播放器自己有没有在出声" —— 这是把媒体状态与真实出声关联起来最可靠的
        /// 一条证据，因为它不受其它应用出声、系统音量、系统静音的影响（端点的环回电平三者都会被带偏）。
        /// </summary>
        private volatile AudioPeakSnapshot _sessionPeaks = new(new Dictionary<string, float>(), 0);
        // 仅后台刷新线程访问；进程查询仍每 2.5 秒一次，缓存的电平表每 100ms 采样。
        private List<(string Name, AudioMeterInformation Meter)> _sessionMeters = new();
        private int _devicesDirty = 1;

        private AutoResetEvent? _refreshSignal;
        private Thread? _refreshThread;
        private AutoResetEvent? _commandSignal;
        private Thread? _commandThread;
        private volatile bool _commandReady;
        private volatile string? _pendingDefaultDeviceId;
        private readonly AppAudioCommandQueue _appCommands = new();

        /// <summary>设备列表或应用会话列表发生变化时触发（已在 UI 线程上）。</summary>
        public event EventHandler? ListsChanged;

        /// <summary>音量或静音状态变更时触发。</summary>
        public event EventHandler<VolumeInfo>? VolumeChanged;

        /// <summary>默认音频输出设备变更时触发。</summary>
        public event EventHandler? DeviceChanged;

        /// <summary>当前主音量百分比 (0~100)。</summary>
        public int CurrentPercent => _lastPercent >= 0 ? _lastPercent : 0;
        internal bool IsAvailable => !_disposed && _volume != null && _commandReady;

        /// <summary>当前是否处于静音状态。</summary>
        public bool IsMuted => _lastMuted;

        /// <summary>当前默认音频设备名称。</summary>
        public string CurrentDeviceName => _currentDeviceName ?? "系统扬声器";

        public VolumeService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        /// <summary>调节系统音量增减（如 delta = +2 或 -2）。</summary>
        internal bool TryHandleVolumeKey(int virtualKey)
        {
            if (!IsAvailable || _lastPercent < 0 || virtualKey is not (0xAD or 0xAE or 0xAF)) return false;
            if (virtualKey == 0xAD) ToggleMute();
            else AdjustVolume(virtualKey == 0xAF ? 2 : -2);
            return true;
        }
        public void AdjustVolume(int delta)
        {
            SetVolume(Math.Clamp(CurrentPercent + delta, 0, 100));
        }

        /// <summary>
        /// 直接设置系统主音量百分比 (0~100)。
        /// <para>
        /// 这里**不**做任何 COM 调用：写入请求投给后台刷新线程（那是唯一持有端点的线程），
        /// 显示值立刻按目标值更新。以前在 UI 线程 <c>lock</c> 里直写 COM，一旦后台正在枚举 /
        /// 切换设备，UI 就得排在后面等 —— 表现为"调音量有延迟"，极端时整岛无响应。
        /// </para>
        /// </summary>
        public void SetVolume(int percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            _lastPercent = percent;
            // 拖到 0 不解静音（与系统一致），其余情况调音量即视为取消静音。
            bool optimisticMuted = percent > 0 ? false : _lastMuted;
            _lastMuted = optimisticMuted;
            MarkLocalWrite(percent, optimisticMuted);
            Interlocked.Exchange(ref _pendingVolumePercent, percent);
            Interlocked.Exchange(ref _pendingUrgent, 1);
            RequestRefresh();
        }

        /// <summary>切换系统静音状态（同样投给后台线程执行）。</summary>
        public void ToggleMute()
        {
            _lastMuted = !_lastMuted;
            MarkLocalWrite(_lastPercent, _lastMuted);
            Interlocked.Exchange(ref _pendingMuteToggle, 1);
            Interlocked.Exchange(ref _pendingUrgent, 1);
            RequestRefresh();
        }

        /// <summary>标记"刚刚由本进程写入"的音量值，供 <see cref="HandleVolumeNotification"/> 过滤迟到通知。</summary>
        private void MarkLocalWrite(int percent, bool muted)
        {
            _localWritePercent = percent;
            _localWriteMuted = muted;
            Volatile.Write(ref _localWriteTick, Environment.TickCount64);
        }

        /// <summary>启动音量监听并获取初始状态。</summary>
        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VolumeService));

            lock (_lock)
            {
                try
                {
                    _enumerator = new MMDeviceEnumerator();
                    _enumerator.RegisterEndpointNotificationCallback(this);
                    BindCurrentEndpoint();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[VolumeService] 启动失败: " + ex.Message);
                }
            }

            // 绑定完成后拉起一条常驻刷新线程。它和 Start() 一样是 MTA，
            // 所有设备 / 会话枚举都在这条线程上做，UI 只读缓存（理由见 _cacheLock 的注释）。
            if (_disposed) return;
            _refreshSignal = new AutoResetEvent(false);
            _refreshThread = new Thread(RefreshLoop)
            {
                IsBackground = true,
                Name = "VolumeListRefresh"
            };
            _refreshThread.Start();
            _commandSignal = new AutoResetEvent(false);
            _commandThread = new Thread(VolumeCommandLoop) { IsBackground = true, Name = "VolumeCommands" };
            _commandThread.Start();
            _refreshSignal.Set();
        }

        /// <summary>请求后台线程立刻刷新设备 / 会话缓存（UI 线程调用，自身不做任何 COM 操作）。</summary>
        public void RequestRefresh()
        {
            try { _refreshSignal?.Set(); _commandSignal?.Set(); } catch (ObjectDisposedException) { }
        }

        /// <summary>后台刷新循环：落地挂起写入 → 跟随系统默认端点 → 枚举 → 更新缓存 → 有变化就通知 UI。</summary>
        private void RefreshLoop()
        {
            long nextEnumerationMs = 0;
            long nextDevicesMs = 0;
            bool refreshLists = true;
            while (!_disposed)
            {
                // 1) 先把挂起的写入落到端点上。写音量 / 切静音是"抢占式"的：
                //    落地后这一拍直接跳过枚举，让用户按键的响应只花"唤醒线程"的时间。
                bool urgent = Volatile.Read(ref _pendingUrgent) != 0 || Environment.TickCount64 - Volatile.Read(ref _localWriteTick) < 300;

                bool switched = false;
                List<AudioDeviceInfo>? devices = null;
                List<AudioAppSessionViewModel>? sessions = null;

                // 设备切换 / 应用静音不能因为"刚调过音量"被饿死，所以排在 urgent 判断之前。
                string? pending = _pendingDefaultDeviceId;
                if (pending != null)
                {
                    _pendingDefaultDeviceId = null;
                    try { ApplyDefaultDeviceCore(pending); switched = true; }
                    catch (Exception ex) { Debug.WriteLine("[VolumeService] 切换默认设备异常: " + ex.Message); }
                }

                if (ApplyPendingAppWritesCore()) refreshLists = true;

                if (!urgent && (refreshLists || switched || Environment.TickCount64 >= nextEnumerationMs))
                {
                    // 2) 主动跟随系统默认端点：不依赖 OnDefaultDeviceChanged（驱动/系统偶发不派发），
                    //    每拍读一次默认端点 ID，变了就重绑 —— 这就是"系统里换输出设备后岛内不跟着变"的解药。
                    if (SyncDefaultEndpoint()) switched = true;

                    long now = Environment.TickCount64;
                    bool devicesDirty = Interlocked.Exchange(ref _devicesDirty, 0) != 0;
                    if (switched || devicesDirty || now >= nextDevicesMs)
                    {
                        devices = EnumerateDevicesCore();
                        nextDevicesMs = now + 15000;
                    }
                    else { lock (_cacheLock) devices = _deviceCache; }
                    sessions = EnumerateSessionsCore();
                    nextEnumerationMs = Environment.TickCount64 + 2500;
                    refreshLists = false;
                }

                SampleSessionPeaks();

                if (switched)
                {
                    // 不等系统通知回调（有可能被驱动吞掉），主动通知一次让卡片立刻换名字。
                    _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
                    {
                        if (!_disposed) DeviceChanged?.Invoke(this, EventArgs.Empty);
                    });
                }

                if (devices != null && sessions != null)
                {
                    bool changed;
                    lock (_cacheLock)
                    {
                        changed = !SameDevices(_deviceCache, devices) || !SameSessions(_sessionCache, sessions);
                        _deviceCache = devices;
                        _sessionCache = sessions;
                    }

                    if (changed)
                    {
                        _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                        {
                            if (!_disposed) ListsChanged?.Invoke(this, EventArgs.Empty);
                        });
                    }
                }

                try { if (_refreshSignal?.WaitOne(urgent ? 30 : 100) == true) refreshLists = true; }
                catch (ObjectDisposedException) { break; }
            }

            CleanupCom();
        }

        /// <summary>
        /// 把挂起的音量 / 静音写入落到当前端点上。只在后台刷新线程调用。
        /// 返回 true 表示这一拍做过写入（调用方应跳过枚举，保证按键响应不被枚举拖慢）。
        /// </summary>
        // Volume commands own a separate MTA endpoint. Device/session enumeration
        // cannot delay a key that has already been accepted by the keyboard hook.
        private void VolumeCommandLoop()
        {
            MMDeviceEnumerator? commands = null;
            MMDevice? device = null;
            AudioEndpointVolume? endpoint = null;
            string? bound = null;
            try
            {
                commands = new MMDeviceEnumerator();
                while (!_disposed)
                {
                    try
                    {
                        string? id = _currentDeviceId;
                        if (id != bound || endpoint == null)
                        {
                            _commandReady = false;
                            endpoint?.Dispose(); endpoint = null; device?.Dispose(); device = null; bound = null;
                            if (!string.IsNullOrWhiteSpace(id))
                            {
                                device = commands.GetDevice(id); endpoint = device.AudioEndpointVolume; bound = id;
                                _commandReady = endpoint != null;
                            }
                        }
                        ApplyPendingWrites(endpoint);
                    }
                    catch (Exception ex)
                    {
                        _commandReady = false;
                        Debug.WriteLine("[VolumeService] 音量命令暂时失败: " + ex.GetType().Name);
                        bound = null;
                    }
                    _commandSignal?.WaitOne(100);
                }
            }
            catch (Exception ex) { alpha_island.Core.IslandLog.Warn("Volume", "音量命令线程不可用: " + ex.GetType().Name); }
            finally
            {
                _commandReady = false;
                try { endpoint?.Dispose(); } catch { }
                try { device?.Dispose(); } catch { }
                try { commands?.Dispose(); } catch { }
                _commandSignal?.Dispose();
            }
        }
        private bool ApplyPendingWrites(AudioEndpointVolume? ep)
        {
            bool urgent = Interlocked.Exchange(ref _pendingUrgent, 0) == 1;
            int percent = Interlocked.Exchange(ref _pendingVolumePercent, -1);
            int muteToggle = Interlocked.Exchange(ref _pendingMuteToggle, 0);

            if (percent < 0 && muteToggle == 0) return false;

            if (ep == null)
            {
                // 端点还没绑上（启动那一瞬间）：把请求放回去，下一拍再落地，别丢用户的按键。
                if (percent >= 0) Interlocked.Exchange(ref _pendingVolumePercent, percent);
                if (muteToggle == 1) Interlocked.Exchange(ref _pendingMuteToggle, 1);
                return urgent;
            }

            try
            {
                if (percent >= 0)
                {
                    ep.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f;
                    if (ep.Mute && percent > 0)
                    {
                        ep.Mute = false;
                    }
                    // SetVolume already published the latest target. A completed older
                    // write must never overwrite a newer key/slider request in the cache.
                }

                if (muteToggle == 1)
                {
                    bool next = _localWriteMuted; // Coalesced repeats apply the latest requested state.
                    ep.Mute = next;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[VolumeService] 写入音量失败: " + ex.Message);
            }

            return urgent;
        }

        /// <summary>
        /// 读系统当前默认输出端点，与已绑定的比对；不一致就重绑。
        /// 只在后台刷新线程调用。返回是否发生了重绑。
        /// </summary>
        private bool SyncDefaultEndpoint()
        {
            if (_enumerator == null || _disposed) return false;

            string? sysId = null;
            try
            {
                using var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                sysId = dev?.ID;
            }
            catch
            {
                try
                {
                    using var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    sysId = dev?.ID;
                }
                catch { }
            }

            if (string.IsNullOrEmpty(sysId)) return false;
            if (_volume != null && string.Equals(sysId, _currentDeviceId, StringComparison.OrdinalIgnoreCase)) return false;

            Debug.WriteLine("[VolumeService] 系统默认输出端点变化，重新绑定: " + sysId);
            BindCurrentEndpoint();
            return true;
        }

        private static bool SameDevices(List<AudioDeviceInfo> a, List<AudioDeviceInfo> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i].Id, b[i].Id, StringComparison.Ordinal) ||
                    !string.Equals(a[i].Name, b[i].Name, StringComparison.Ordinal) ||
                    a[i].IsDefault != b[i].IsDefault)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool SameSessions(List<AudioAppSessionViewModel> a, List<AudioAppSessionViewModel> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].ProcessId != b[i].ProcessId ||
                    !string.Equals(a[i].Name, b[i].Name, StringComparison.Ordinal) ||
                    a[i].IsMuted != b[i].IsMuted || a[i].VolumePercent != b[i].VolumePercent)
                {
                    return false;
                }
            }
            return true;
        }

        private void BindCurrentEndpoint()
        {
            try
            {
                UnbindCurrentEndpoint();

                if (_enumerator == null) return;
                try
                {
                    _currentDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                }
                catch
                {
                    try
                    {
                        _currentDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    }
                    catch { }
                }

                if (_currentDevice == null) return;

                _volume = _currentDevice.AudioEndpointVolume;
                if (_volume == null) return;

                _currentDeviceId = _currentDevice.ID;
                _currentDeviceName = _currentDevice.FriendlyName;

                float master = _volume.MasterVolumeLevelScalar;
                int percent = (int)Math.Round(master * 100f);
                bool muted = _volume.Mute;

                _lastPercent = Math.Clamp(percent, 0, 100);
                _lastMuted = muted;
                _hasInitialValue = true;

                _volume.OnVolumeNotification += HandleVolumeNotification;
                _audioSessionManager = _currentDevice.AudioSessionManager;
                if (_audioSessionManager != null) _audioSessionManager.OnSessionCreated += OnAudioSessionCreated;
                Debug.WriteLine($"[VolumeService] 已绑定音频终结点: {_currentDeviceName}, 音量: {_lastPercent}%, 静音: {_lastMuted}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[VolumeService] 绑定终结点异常: " + ex.Message);
            }
        }

        private void UnbindCurrentEndpoint()
        {
            _sessionMeters.Clear();
            _sessionPeaks = new(new Dictionary<string, float>(), Environment.TickCount64);
            if (_audioSessionManager != null)
            {
                _audioSessionManager.OnSessionCreated -= OnAudioSessionCreated;
                _audioSessionManager = null;
            }
            if (_volume != null)
            {
                try { _volume.OnVolumeNotification -= HandleVolumeNotification; } catch { }
                _volume = null;
            }
            _currentDevice?.Dispose();
            _currentDevice = null;
        }

        private void HandleVolumeNotification(AudioVolumeNotificationData data)
        {
            if (_disposed || !_hasInitialValue) return;

            int percent = Math.Clamp((int)Math.Round(data.MasterVolume * 100f), 0, 100);
            bool muted = data.Muted;

            // 自己刚写下去的值会经音频驱动绕一圈后"迟到"回来。若直接采信，
            // 它会把滑块/按键刚设好的新值覆盖回去，界面表现为百分比来回跳、拖不动。
            // 600ms 窗口内只忽略"与自己写入值不同"的通知：用户在此期间手动改系统音量也照常生效，
            // 因为那时 _localWritePercent 已经过期或被后续写入刷新。
            long localTick = Volatile.Read(ref _localWriteTick);
            if (localTick != 0 && Environment.TickCount64 - localTick < 600
                && (percent != _localWritePercent || muted != _localWriteMuted))
            {
                return;
            }

            // 仅在真实发生数值或静音状态变动时触发
            if (percent == _lastPercent && muted == _lastMuted) return;

            _lastPercent = percent;
            _lastMuted = muted;

            var info = new VolumeInfo(percent, muted);
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (!_disposed)
                {
                    VolumeChanged?.Invoke(this, info);
                }
            });
        }

        private void OnAudioSessionCreated(object sender, IAudioSessionControl newSession) => RequestRefresh();

        /// <summary>快捷切换 Windows 默认音频输出设备（实际调用在后台刷新线程上执行）。</summary>
        public bool SetDefaultDevice(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;

            // IPolicyConfig 与 _enumerator 的调用一律丢给后台线程：那里才是创建 COM 对象的单元。
            _pendingDefaultDeviceId = deviceId;

            // 名字先从缓存里乐观地改掉：SetDefaultEndpoint 是跨进程 RPC，真绑完可能要大几百毫秒，
            // 卡片上那行设备名不该跟着一起等（真名会在后台 BindCurrentEndpoint 后再校一遍）。
            lock (_cacheLock)
            {
                foreach (var d in _deviceCache)
                {
                    if (string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        _currentDeviceName = d.Name;
                        break;
                    }
                }
            }

            _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (!_disposed) DeviceChanged?.Invoke(this, EventArgs.Empty);
            });

            RequestRefresh();
            return true;
        }

        /// <summary>必须在后台刷新线程上调用（绝不可以在 UI 线程或 RPC 回调里调）。</summary>
        private void ApplyDefaultDeviceCore(string deviceId)
        {
            var policyConfig = (IPolicyConfig)new CPolicyConfigClient();
            // 0 = Console, 1 = Multimedia, 2 = Communications
            policyConfig.SetDefaultEndpoint(deviceId, 0);
            policyConfig.SetDefaultEndpoint(deviceId, 1);
            policyConfig.SetDefaultEndpoint(deviceId, 2);
            BindCurrentEndpoint();
        }

        /// <summary>当前激活的音频输出端点（读缓存，UI 线程安全）。</summary>
        public List<AudioDeviceInfo> GetActiveOutputDevices()
        {
            lock (_cacheLock) return new List<AudioDeviceInfo>(_deviceCache);
        }

        /// <summary>枚举当前系统所有处于激活状态的音频输出端点。必须在后台刷新线程上调用。</summary>
        private List<AudioDeviceInfo> EnumerateDevicesCore()
        {
            var list = new List<AudioDeviceInfo>();
            if (_enumerator == null) return list;
            try
            {
                string currentId = _currentDeviceId ?? "";
                var endpoints = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                foreach (var ep in endpoints)
                {
                    // 有更急的活（音量/静音写入、切默认设备）就立刻收手：枚举不是关键路径，
                    // 但会话/设备的 COM 遍历可能耗掉几百毫秒，会把用户按键的响应拖成"有延迟"。
                    if (Volatile.Read(ref _pendingUrgent) != 0 || _pendingDefaultDeviceId != null) break;
                    try
                    {
                        bool isDef = !string.IsNullOrEmpty(currentId) && string.Equals(ep.ID, currentId, StringComparison.OrdinalIgnoreCase);
                        list.Add(new AudioDeviceInfo(ep.ID, ep.FriendlyName, isDef));
                    }
                    catch { }
                    finally
                    {
                        // 每 2.5s 枚举一次，不释放就是稳定的 COM 引用泄漏。
                        try { ep.Dispose(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[VolumeService] 枚举音频设备异常: " + ex.Message);
            }
            return list;
        }

        /// <summary>当前默认音频输出设备上的各应用音频会话（读缓存，UI 线程安全）。</summary>
        public List<AudioAppSessionViewModel> GetAudioAppSessions()
        {
            lock (_cacheLock) return new List<AudioAppSessionViewModel>(_sessionCache);
        }

        /// <summary>枚举各应用音频会话。必须在后台刷新线程上调用。</summary>
        private List<AudioAppSessionViewModel> EnumerateSessionsCore()
        {
            var list = new List<AudioAppSessionViewModel>();
            var meters = new List<(string Name, AudioMeterInformation Meter)>();
            if (_currentDevice == null) { _sessionMeters = meters; return list; }
            try
            {
                    var sm = _currentDevice.AudioSessionManager;
                    if (sm != null)
                    {
                        // NAudio 的 Sessions 是缓存集合；只重复读取它不会发现新开的播放会话。
                        sm.RefreshSessions();
                        var sessions = sm.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            // 同上：每查一个会话就可能付出一次进程查询的代价，急活优先。
                            if (Volatile.Read(ref _pendingUrgent) != 0 || _appCommands.HasPending)
                            {
                                // Never publish a partial list: it would destroy live sliders and
                                // replace the player's peak evidence with an incomplete sample.
                                lock (_cacheLock) return new(_sessionCache);
                            }
                            var s = sessions[i];
                            if (s == null) continue;
                            uint pid = s.GetProcessID;
                            if (pid == 0) continue; // 忽略系统基础音效进程

                            string procName = "应用程序";
                            string procKey = string.Empty;
                            try
                            {
                                using var p = Process.GetProcessById((int)pid);
                                procName = !string.IsNullOrWhiteSpace(p.MainWindowTitle) ? p.MainWindowTitle : p.ProcessName;
                                if (procName.Length > 20) procName = procName.Substring(0, 20) + "...";
                                procKey = p.ProcessName + ".exe";
                            }
                            catch { }

                            // 保留电平表供快速采样，避免每拍重新查询进程和枚举全部设备。
                            try
                            {
                                if (s.AudioMeterInformation is { } meter && procKey.Length > 0)
                                    meters.Add((procKey, meter));
                            }
                            catch { }

                            var vol = s.SimpleAudioVolume;
                            if (vol != null)
                            {
                                list.Add(new AudioAppSessionViewModel
                                {
                                    ProcessId = (int)pid,
                                    Name = procName,
                                    IsMuted = vol.Mute,
                                    VolumePercent = (int)Math.Round(vol.Volume * 100)
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[VolumeService] 枚举音频会话异常: " + ex.Message);
                }
            _sessionMeters = meters;
            return list.GroupBy(item => item.ProcessId).Select(group => new AudioAppSessionViewModel
            {
                ProcessId = group.Key, Name = group.First().Name,
                VolumePercent = group.Max(item => item.VolumePercent), IsMuted = group.All(item => item.IsMuted)
            }).ToList();
        }

        private void SampleSessionPeaks()
        {
            var peaks = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, meter) in _sessionMeters)
            {
                try
                {
                    float peak = meter.MasterPeakValue;
                    if (!float.IsFinite(peak) || peak is < 0 or > 1) continue;
                    if (!peaks.TryGetValue(name, out float old) || peak > old) peaks[name] = peak;
                }
                catch { } // 设备断开或会话结束时丢弃证据，不延续旧样本。
            }
            _sessionPeaks = new(peaks, Environment.TickCount64);
        }

        /// <summary>
        /// 取指定进程（如 <c>cloudmusic.exe</c>）当前音频会话的峰值；拿不到返回 -1，等于 0 表示
        /// "这个进程自己正在输出静音"。UI 线程只读后台线程拍好的快照，本身不做任何 COM 操作。
        /// </summary>
        public float GetPlayerSessionPeak(string processName)
        {
            return _sessionPeaks.Read(processName, Environment.TickCount64);
        }

        /// <summary>快捷切换指定进程的音频静音状态（实际调用在后台刷新线程上执行）。</summary>
        public void ToggleAppMute(int processId)
        {
            if (processId <= 0) return;
            lock (_cacheLock)
            {
                var app = _sessionCache.FirstOrDefault(item => item.ProcessId == processId);
                if (app == null) return;
                bool muted = !app.IsMuted;
                _appCommands.SetMute(processId, muted);
                UpdateAppCache(processId, null, muted);
            }
            RequestRefresh();
        }

        public void SetAppVolume(int processId, int percent)
        {
            if (processId <= 0) return;
            percent = Math.Clamp(percent, 0, 100);
            lock (_cacheLock)
            {
                _appCommands.SetVolume(processId, percent);
                UpdateAppCache(processId, percent, percent > 0 ? false : null);
            }
            RequestRefresh();
        }

        // Replace cache values rather than notifying UI bindings from a backend thread.
        private void UpdateAppCache(int pid, int? percent, bool? muted)
        {
            _sessionCache = _sessionCache.Select(item => item.ProcessId != pid ? item : new AudioAppSessionViewModel
            {
                ProcessId = pid, Name = item.Name,
                VolumePercent = percent ?? item.VolumePercent, IsMuted = muted ?? item.IsMuted
            }).ToList();
        }

        /// <summary>必须在后台刷新线程上调用。</summary>
        private bool ApplyPendingAppWritesCore()
        {
            if (!_appCommands.HasPending || _currentDevice == null) return false;
            try
            {
                var sm = _currentDevice.AudioSessionManager;
                if (sm == null) return false;
                var commands = _appCommands.Drain();
                var sessions = sm.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    if (s != null && commands.TryGetValue((int)s.GetProcessID, out var command))
                    {
                        try
                        {
                            var vol = s.SimpleAudioVolume;
                            if (vol == null) continue;
                            if (command.Percent is int percent) vol.Volume = percent / 100f;
                            if (command.Muted is bool muted) vol.Mute = muted;
                        }
                        catch (Exception ex) { Debug.WriteLine("[VolumeService] 应用音量写入异常: " + ex.Message); }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[VolumeService] 应用音量写入异常: " + ex.Message);
                return false;
            }
        }

        /// <summary>打开 Windows 11 设置中的音量合成器页面。</summary>
        public static void OpenWindowsVolumeMixer()
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:apps-volume") { UseShellExecute = true });
            }
            catch
            {
                try
                {
                    Process.Start(new ProcessStartInfo("sndvol.exe") { UseShellExecute = true });
                }
                catch { }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 唤醒刷新线程：COM 的释放必须在创建它的那条线程上做
            // （从别的单元去放开一个 MTA 对象，轻则异常、重则把调用线程一起拖死）。
            RequestRefresh();

            // 刷新线程没在跑（Start 失败 / 还没启动）才由调用方线程收尾 —— 此时不存在并发。
            if (_refreshThread == null || !_refreshThread.IsAlive)
            {
                CleanupCom();
            }
        }

        /// <summary>释放 COM 对象。只在后台刷新线程退出时（或确认无并发时）调用。</summary>
        private void CleanupCom()
        {
            try
            {
                if (_enumerator != null)
                {
                    _enumerator.UnregisterEndpointNotificationCallback(this);
                    _enumerator.Dispose();
                    _enumerator = null;
                }
            }
            catch { }

            try { UnbindCurrentEndpoint(); } catch { }
        }

        // ===================== IMMNotificationClient =====================

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (_disposed) return;
            if (flow == DataFlow.Render && (role == Role.Console || role == Role.Multimedia))
            {
                // 只做"喊一声"：这个回调跑在 RPC 线程上，绝不能在这里碰 COM
                // （以前它直接 lock + BindCurrentEndpoint，正好和刷新线程的 SetDefaultEndpoint
                //   撞成互相等待 —— 那一下整岛就无响应了）。重绑交给刷新线程。
                Debug.WriteLine("[VolumeService] 收到默认输出设备切换通知，排队重绑...");
                RequestRefresh();
            }
        }

        private void InvalidateDevices()
        {
            if (_disposed) return;
            Interlocked.Exchange(ref _devicesDirty, 1);
            RequestRefresh();
        }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => InvalidateDevices();
        public void OnDeviceAdded(string pwstrDeviceId) => InvalidateDevices();
        public void OnDeviceRemoved(string deviceId) => InvalidateDevices();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) => InvalidateDevices();
    }

    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig]
        int SetDefaultEndpoint(
            [In][MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId,
            [In][MarshalAs(UnmanagedType.U4)] uint role);
        [PreserveSig] int SetEndpointVisibility();
    }

    [ComImport]
    [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    internal class CPolicyConfigClient
    {
    }
}
