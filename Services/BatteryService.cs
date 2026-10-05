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

    /// <summary>充电类型推断。</summary>
    public enum BatteryChargeType
    {
        Unknown,          // 未知/通用充电
        StandardAC,       // 电源适配器
        PD,               // PD 快充 (USB Type-C)
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
        DateTime SampledUtc = default
    );

    /// <summary>
    /// 笔记本电脑电池与充电状态监听服务。
    ///
    /// 核心职责：
    ///  1. 基于 Win32 GetSystemPowerStatus 与 WinRT Windows.Devices.Power.Battery 双轨监控电池；
    ///  2. 监听 Microsoft.Win32.SystemEvents.PowerModeChanged 与 WinRT ReportUpdated，瞬时感知插拔电源；
    ///  3. 智能推断充电类型（PD 快充 / 电源适配器 / 80% 电池保养模式）；
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
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS sps);

        private readonly Dispatcher _dispatcher;
        private readonly object _lock = new();
        private readonly object _reportGate = new();
        private Task<BatteryReport>? _reportTask;
        private BatteryStatusInfo? _current;
        public BatteryStatusInfo? Current => System.Threading.Volatile.Read(ref _current);

        private DispatcherTimer? _pollTimer;
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

        public BatteryService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        private int _querying = 0;
        private volatile bool _pendingQuery = false;

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
                _pollTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
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
            TriggerStatusCheck(0);
            TriggerStatusCheck(150);
        }

        private void OnSystemPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (_disposed) return;
            TriggerStatusCheck(30);
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
                TriggerStatusCheck(30);
                TriggerStatusCheck(180);
            }
        }

        /// <summary>后台触发异步状态采样，避免 ACPI/WMI 硬件查询阻塞 UI 线程。</summary>
        private void TriggerStatusCheck(int delayMs = 0)
        {
            if (_disposed) return;
            Task.Run(async () =>
            {
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }
                if (_disposed) return;
                if (System.Threading.Interlocked.CompareExchange(ref _querying, 1, 0) != 0)
                {
                    _pendingQuery = true;
                    return; // 已经有查询在进行中，标记 pending 等当前完成后补查
                }
                try
                {
                    do
                    {
                        _pendingQuery = false;
                        var current = QueryCurrentStatus(BatteryPowerEvent.None);
                        lock (_lock)
                        {
                            if (!_disposed)
                            {
                                ProcessStatusUpdate(current);
                            }
                        }
                    } while (_pendingQuery && !_disposed);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[BatteryService] 异步采样异常: " + ex.Message);
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _querying, 0);
                }
            });
        }

        private void ProcessStatusUpdate(BatteryStatusInfo current)
        {
            if (_disposed) return;
            System.Threading.Volatile.Write(ref _current, current);
            bool onAc = current.IsOnAcPower ?? current.IsCharging;

            // 首次采样本底
            if (!_hasBaseline || !current.HasBattery || current.Percent < 0 || current.IsOnAcPower == null)
            {
                _hasBaseline = current.HasBattery && current.Percent >= 0 && current.IsOnAcPower != null;
                _lastIsCharging = onAc;
                _lastPercent = current.Percent;
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
        }

        /// <summary>查询当前电池即时快照。</summary>
        public BatteryStatusInfo QueryCurrentStatus(BatteryPowerEvent eventType = BatteryPowerEvent.None)
        {
            int percent = -1;
            bool hasBattery = false;
            bool isCharging = false;
            bool? onAc = null;
            int? rate = null;
            int? remainingMwh = null;
            int remainingSeconds = -1;
            try
            {
                if (GetSystemPowerStatus(out var status))
                {
                    onAc = status.ACLineStatus <= 1 ? status.ACLineStatus == 1 : null;
                    hasBattery = status.BatteryFlag != 255 && (status.BatteryFlag & 128) == 0;
                    isCharging = hasBattery && (status.BatteryFlag & 8) != 0;
                    if (hasBattery && status.BatteryLifePercent <= 100) percent = status.BatteryLifePercent;
                    remainingSeconds = status.BatteryLifeTime;
                }
                // 上一次硬件查询若挂起，复用它；最多保留一个未完成查询。
                Task<BatteryReport> task;
                lock (_reportGate)
                {
                    if (_reportTask == null || _reportTask.IsCompleted)
                        _reportTask = Task.Run(() => Battery.AggregateBattery.GetReport());
                    task = _reportTask;
                }
                if (task.Wait(200))
                {
                    var report = task.Result;
                    hasBattery = report.Status != Windows.System.Power.BatteryStatus.NotPresent;
                    isCharging = hasBattery && report.Status == Windows.System.Power.BatteryStatus.Charging;
                    rate = report.ChargeRateInMilliwatts;
                    remainingMwh = report.RemainingCapacityInMilliwattHours;
                    if (hasBattery && remainingMwh.HasValue && report.FullChargeCapacityInMilliwattHours is > 0)
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
                onAc == true ? BatteryChargeType.StandardAC : BatteryChargeType.Unknown,
                eventType, rate, discharge, lifeSeconds, hasBattery, onAc, estimated, DateTime.UtcNow);
        }
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

                _pollTimer?.Stop();
                _pollTimer = null;
            }
        }
    }
}
