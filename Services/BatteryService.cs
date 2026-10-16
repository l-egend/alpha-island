using alpha_island.Core;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Threading;
using Windows.Devices.Power;

namespace alpha_island.Services
{
    /// <summary>电池电源事件类型。</summary>
    public enum BatteryPowerEvent
    {
        None,
        PluggedIn,        // 接入电源
        Unplugged,        // 拔出电源（切换为电池）
        FullyCharged,     // 电池已充满 (100%)
        LowBattery,       // 低电量警告 (<= 20%)
        CriticalBattery   // 严重低电量警告 (<= 10%)
    }

    /// <summary>已确认的充电器类型；无法读取厂商信息时使用 Unknown。</summary>
    public enum BatteryChargeType
    {
        Unknown,          // 未知/通用充电
        StandardAC,       // 电源适配器
        PD,               // USB-C PD（不表示已知充电瓦数）
        ConservationMode  // 电池保养模式 (通常限制在 80% 左右)
    }

    /// <summary>电池状态快照数据。</summary>
    public sealed record BatteryStatusInfo(
        int Percent,
        bool IsCharging,
        BatteryChargeType ChargeType,
        BatteryPowerEvent EventType,
        int? ChargeRateMilliwatts,
        int? DischargeRateMilliwatts = null,   // 放电功率（电池供电时，毫瓦；A 组③ 派生）
        int? RemainingLifeSeconds = null,
        bool HasBattery = true,
        bool? IsOnAcPower = null,
        bool IsPowerEstimated = false,
        DateTime SampledUtc = default,
        bool IsEnergySaverEnabled = false,
        SmartChargingState SmartCharging = SmartChargingState.Unknown
    );

    /// <summary>
    /// 笔记本电脑电池与充电状态监听服务。
    ///
    /// 核心职责：
    ///  1. 基于 Win32 GetSystemPowerStatus 与 WinRT Windows.Devices.Power.Battery 双轨监控电池；
    ///  2. 监听 Microsoft.Win32.SystemEvents.PowerModeChanged 与 WinRT ReportUpdated，瞬时感知插拔电源；
    ///  3. 后台读取 Windows 的智能充电指示（不按电量或充电速率猜测）；
    ///  4. 充满与低电量阶梯阈值预警；
    ///  5. 所有状态通知与事件统一封送到 UI Dispatcher。
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public sealed class BatteryService : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;          // 0 = Offline, 1 = Online, 255 = Unknown
            public byte BatteryFlag;           // 1 = High, 2 = Low, 4 = Critical, 8 = Charging, 128 = No battery
            public byte BatteryLifePercent;    // 0..100, 255 = Unknown
            public byte SystemStatusFlag;      // 0 = 节电关闭，1 = 节电开启（Windows 10+）
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS sps);

        private readonly Dispatcher _dispatcher;
        private readonly object _lock = new();
        private readonly object _reportGate = new();
        private Task<BatteryHardwareReport>? _reportTask;
        private readonly Func<BatteryHardwareReport> _readReport;
        private readonly Func<NativeBatterySnapshot?> _readNative;
        private readonly SmartChargingMonitor _smartCharging = new();
        private readonly ChargerTypeMonitor _chargerType = new();
        private BatteryHardwareReport? _cachedReport;
        private DateTime _cachedReportUtc, _reportStartedUtc, _nextReportUtc;
        private bool? _reportPowerSource;
        private int _powerGeneration, _reportGeneration, _cachedReportGeneration;
        private BatteryStatusInfo? _current;
        public BatteryStatusInfo? Current => System.Threading.Volatile.Read(ref _current);

        private IslandTimer? _pollTimer;
        private bool _disposed;
        private bool _isStarted;
        private bool _initialReportReceived;

        // 上一次记录的状态
        private bool _lastIsCharging;
        private bool _hasBaseline;
        private int _lastPercent = -1;
        private bool _warned20;
        private bool _warned10;
        private bool _notifiedFull;
        private DateTime _lastPlugEventTime = DateTime.MinValue;

        /// <summary>电池状态、充放电事件发生时触发。</summary>
        public event EventHandler<BatteryStatusInfo>? BatteryStatusChanged;

        public BatteryService(Dispatcher dispatcher) : this(dispatcher, ReadHardwareReport) { }
        internal BatteryService(Dispatcher dispatcher, Func<BatteryHardwareReport> readReport,
            Func<NativeBatterySnapshot?>? readNative = null)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _readReport = readReport;
            _readNative = readNative ?? ReadNativeSnapshot;
            _sampler = new BatterySampleCoordinator(() =>
            {
                if (_disposed) return;
                var status = QueryCurrentStatus();
                lock (_lock) { if (!_disposed) ProcessStatusUpdate(status); }
            });
        }
        private static NativeBatterySnapshot? ReadNativeSnapshot()
            => GetSystemPowerStatus(out var s) ? new(s.ACLineStatus, s.BatteryFlag, s.BatteryLifePercent, s.SystemStatusFlag, s.BatteryLifeTime) : null;
        private static BatteryHardwareReport ReadHardwareReport()
        {
            var r = Battery.AggregateBattery.GetReport();
            return new(r.Status, r.ChargeRateInMilliwatts, r.RemainingCapacityInMilliwattHours, r.FullChargeCapacityInMilliwattHours);
        }

        private readonly BatterySampleCoordinator _sampler;

        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BatteryService));
            if (_isStarted) return;
            _isStarted = true;

            try
            {
                // 后台异步读取初始状态
                TriggerStatusCheck(0);

                // 订阅系统级电源模式切换事件（插拔电源瞬时通知）
                Microsoft.Win32.SystemEvents.PowerModeChanged += OnSystemPowerModeChanged;

                try { Windows.System.Power.PowerManager.EnergySaverStatusChanged += OnEnergySaverStatusChanged; }
                catch (Exception ex) { Debug.WriteLine("[BatteryService] 节电状态事件不可用，使用原有采样兜底: " + ex.Message); }

                // 订阅 WinRT 电池报告更新
                try
                {
                    Battery.AggregateBattery.ReportUpdated += OnWinRtReportUpdated;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[BatteryService] 订阅 WinRT ReportUpdated 失败: " + ex.Message);
                }

                // 辅助轮询定时器（每 1 秒检查一次，兜底防止系统广播丢失）
                _pollTimer = new IslandTimer(_dispatcher, "battery-fallback")
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _pollTimer.Tick += (s, e) => TriggerStatusCheck(0);
                _pollTimer.Start();

                Debug.WriteLine("[BatteryService] 启动成功，已启用后台异步硬件采样");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BatteryService] 启动异常: " + ex.Message);
            }
        }

        /// <summary>由宿主窗口 Win32 WM_POWERBROADCAST 驱动，实现 0ms 瞬时响应与防抖二次采样。</summary>
        public void NotifyPowerBroadcast()
        {
            if (_disposed) return;
            _smartCharging.Invalidate();
            _chargerType.Invalidate();
            TriggerStatusCheck(0);
            TriggerStatusCheck(150);
        }

        private void OnSystemPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (_disposed) return;
            _smartCharging.Invalidate();
            _chargerType.Invalidate();
            TriggerStatusCheck(0);
            TriggerStatusCheck(180);
        }

        private void OnWinRtReportUpdated(Battery sender, object args)
        {
            if (_disposed) return;
            if (!_initialReportReceived)
            {
                _initialReportReceived = true;
                TriggerStatusCheck(0);
            }
            else
            {
                TriggerStatusCheck(0);
                TriggerStatusCheck(180);
            }
        }

        private void OnEnergySaverStatusChanged(object? sender, object args)
        {
            if (!_disposed) TriggerStatusCheck(0);
        }

        /// <summary>后台触发异步状态采样，避免 ACPI/WMI 硬件查询阻塞 UI 线程。</summary>
        private void TriggerStatusCheck(int delayMs = 0)
        {
            if (!_disposed) _sampler.Request(delayMs);
        }
        private void ProcessStatusUpdate(BatteryStatusInfo current)
        {
            if (_disposed) return;
            var previous = Current;
            bool displayChanged = previous == null || current.HasBattery != previous.HasBattery
                || current.Percent != previous.Percent || current.IsCharging != previous.IsCharging
                || current.IsOnAcPower != previous.IsOnAcPower || current.IsEnergySaverEnabled != previous.IsEnergySaverEnabled
                || current.SmartCharging != previous.SmartCharging || current.ChargeType != previous.ChargeType;
            System.Threading.Volatile.Write(ref _current, current);
            bool onAc = current.IsOnAcPower ?? current.IsCharging;

            // 首次采样本底
            if (!_hasBaseline || !current.HasBattery || current.Percent < 0 || current.IsOnAcPower == null)
            {
                _hasBaseline = current.HasBattery && current.Percent >= 0 && current.IsOnAcPower != null;
                _lastIsCharging = onAc;
                _lastPercent = current.Percent;
                if (displayChanged) Publish(current);
                return;
            }

            var eventType = BatteryPowerEvent.None;

            // 1. 充电状态改变判定（插上电源 vs 拔出电源）
            if (onAc != _lastIsCharging)
            {
                // 防抖：必须间隔 > 200ms 才提交并触发，防止触点抖动导致丢事件
                if ((DateTime.UtcNow - _lastPlugEventTime).TotalMilliseconds > 200)
                {
                    _lastPlugEventTime = DateTime.UtcNow;
                    _lastIsCharging = onAc;
                    eventType = onAc ? BatteryPowerEvent.PluggedIn : BatteryPowerEvent.Unplugged;

                    // 重新插入时重置充满通知标记
                    if (onAc)
                    {
                        _notifiedFull = false;
                    }
                    else
                    {
                        // 拔出时重置低电量标记，若再次耗尽时可触发
                        if (current.Percent > 20) _warned20 = false;
                        if (current.Percent > 10) _warned10 = false;
                    }
                }
            }

            // 2. 充满判定
            if (onAc && current.Percent >= 100)
            {
                if (!_notifiedFull)
                {
                    _notifiedFull = true;
                    eventType = BatteryPowerEvent.FullyCharged;
                }
            }

            // 3. 低电量判定（仅在未充电/放电阶段警告）
            if (!onAc)
            {
                if (current.Percent <= 10 && !_warned10)
                {
                    _warned10 = true;
                    eventType = BatteryPowerEvent.CriticalBattery;
                }
                else if (current.Percent <= 20 && !_warned20)
                {
                    _warned20 = true;
                    eventType = BatteryPowerEvent.LowBattery;
                }
            }

            _lastPercent = current.Percent;

            if (eventType != BatteryPowerEvent.None)
            {
                var eventInfo = current with { EventType = eventType };
                Publish(eventInfo);
            }
            else if (displayChanged) Publish(current);
        }

        /// <summary>查询当前电池即时快照。</summary>
        public BatteryStatusInfo QueryCurrentStatus(BatteryPowerEvent eventType = BatteryPowerEvent.None)
        {
            int percent = -1;
            bool hasBattery = false;
            bool nativeAvailable = false;
            bool isCharging = false;
            bool energySaver = false;
            bool? onAc = null;
            int? rate = null;
            int? remainingMwh = null;
            int remainingSeconds = -1;
            try
            {
                if (_readNative() is { } status)
                {
                    nativeAvailable = true;
                    energySaver = status.SystemStatusFlag == 1;
                    onAc = status.ACLineStatus <= 1 ? status.ACLineStatus == 1 : null;
                    hasBattery = status.BatteryFlag != 255 && (status.BatteryFlag & 128) == 0;
                    isCharging = hasBattery && (status.BatteryFlag & 8) != 0;
                    if (hasBattery && status.BatteryLifePercent <= 100) percent = status.BatteryLifePercent;
                    remainingSeconds = status.BatteryLifeTime;
                }
                // Detailed ACPI data never delays the immediate Windows power snapshot.
                BatteryHardwareReport? report = null;
                lock (_reportGate)
                {
                    var now = DateTime.UtcNow;
                    if (_reportPowerSource != onAc)
                    {
                        _reportPowerSource = onAc; _powerGeneration++;
                        _cachedReport = null; _nextReportUtc = DateTime.MinValue;
                    }
                    if (_reportTask is { IsCompleted: true })
                    {
                        if (_reportTask.IsCompletedSuccessfully && _reportGeneration == _powerGeneration
                            && now - _reportStartedUtc <= TimeSpan.FromSeconds(10))
                        {
                            _cachedReport = _reportTask.Result; _cachedReportUtc = now;
                            _cachedReportGeneration = _reportGeneration;
                        }
                        else if (_reportTask.IsFaulted) _ = _reportTask.Exception;
                        _reportTask = null;
                    }
                    if (_reportTask == null && now >= _nextReportUtc)
                    {
                        _reportGeneration = _powerGeneration; _reportStartedUtc = now;
                        _nextReportUtc = now.AddSeconds(1);
                        _reportTask = Task.Run(_readReport);
                    }
                    if (_cachedReportGeneration == _powerGeneration && now - _cachedReportUtc <= TimeSpan.FromSeconds(10))
                        report = _cachedReport;
                }
                if (report != null)
                {
                    // Win32 flags describe the present power source; an older report must not reverse them.
                    if (!nativeAvailable)
                    {
                        hasBattery = report.Status != Windows.System.Power.BatteryStatus.NotPresent;
                        isCharging = hasBattery && report.Status == Windows.System.Power.BatteryStatus.Charging;
                    }
                    rate = report.ChargeRateInMilliwatts;
                    remainingMwh = report.RemainingCapacityInMilliwattHours;
                    if (percent < 0 && hasBattery && remainingMwh.HasValue && report.FullChargeCapacityInMilliwattHours is > 0)
                        percent = Math.Clamp((int)Math.Round(100.0 * remainingMwh.Value /
                            report.FullChargeCapacityInMilliwattHours.Value), 0, 100);
                }
            }
            catch (Exception ex) { Debug.WriteLine("[BatteryService] 采样降级: " + ex.Message); }

            int? discharge = null;
            int? lifeSeconds = null;
            bool estimated = false;
            if (hasBattery && onAc == false)
            {
                if (rate is < 0 && rate != int.MinValue) discharge = -rate.Value;
                else if (remainingMwh is > 0 && remainingSeconds > 0)
                {
                    double watts = remainingMwh.Value * 3600.0 / remainingSeconds;
                    if (watts <= int.MaxValue) discharge = (int)Math.Round(watts);
                    estimated = true;
                }
                if (remainingSeconds > 0) lifeSeconds = remainingSeconds;
                else if (remainingMwh is > 0 && discharge is > 0)
                {
                    double seconds = remainingMwh.Value * 3600.0 / discharge.Value;
                    if (seconds < 360000) lifeSeconds = (int)Math.Round(seconds);
                }
            }
            if (!hasBattery) percent = -1;
            // 电池的充电速率无法证明适配器使用 PD，也无法证明厂商开启了保养模式。
            return new BatteryStatusInfo(percent, isCharging,
                _chargerType.ReadCached(onAc),
                eventType, rate, discharge, lifeSeconds, hasBattery, onAc, estimated, DateTime.UtcNow, energySaver,
                hasBattery ? _smartCharging.ReadCached(onAc) : SmartChargingState.Unknown);
        }
        public static string FormatChargeType(BatteryChargeType type) => type switch
        {
            BatteryChargeType.StandardAC => "普通 AC 适配器",
            BatteryChargeType.PD => "USB-C PD",
            _ => "充电器类型未知"
        };
        /// <summary>把毫瓦格式化成瓦（如 18.4 W）。</summary>
        public static string FormatPower(int? milliwatts)
            => milliwatts.HasValue && milliwatts.Value > 0 ? $"{milliwatts.Value / 1000.0:0.#} W" : "—";

        /// <summary>把剩余秒数格式化成「2 小时 15 分」/「45 分钟」。</summary>
        public static string FormatRemaining(int? seconds)
        {
            if (!seconds.HasValue || seconds.Value <= 0) return "—";
            var span = TimeSpan.FromSeconds(seconds.Value);
            if (span.TotalHours >= 1) return $"{(int)span.TotalHours} 小时 {span.Minutes} 分";
            return $"{Math.Max(1, span.Minutes)} 分钟";
        }

        private void Publish(BatteryStatusInfo info)
        {
            if (_disposed) return;
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (!_disposed)
                {
                    BatteryStatusChanged?.Invoke(this, info);
                }
            });
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _sampler.Dispose();

                try
                {
                    Microsoft.Win32.SystemEvents.PowerModeChanged -= OnSystemPowerModeChanged;
                }
                catch { }

                try
                {
                    Battery.AggregateBattery.ReportUpdated -= OnWinRtReportUpdated;
                }
                catch { }

                try { Windows.System.Power.PowerManager.EnergySaverStatusChanged -= OnEnergySaverStatusChanged; }
                catch { }

                _pollTimer?.Dispose();
                _pollTimer = null;
            }
        }
    }
}
