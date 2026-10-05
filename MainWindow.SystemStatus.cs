using System;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using System.Windows;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;

// A 组「系统状态」类功能在 UI 侧的装配：低频采样节拍（IslandTicker）、细条常驻指示、
// 阈值提示（通知图层）。采样本体在 Services\NetworkSpeedService.cs 里，这里只做接线与显示。
namespace alpha_island
{
    public partial class MainWindow
    {
        // ================= 低频采样调度（阶段 3.6 的 IslandTicker 的第一个消费者） =================

        /// <summary>网速 / 内存 / 功率 / 飞行模式 / 隐私灯共用的采样定时器（一个 timer + 命名订阅）。</summary>
        private IslandTicker _ticker = null!;

        // ================= 网速（A 组①） =================

        private NetworkSpeedService? _netSpeed;

        /// <summary>细条上最后一次写下去的两段文本，避免每秒无谓地重设 XAML 属性引起重排。</summary>
        private string _barSpeedDown = string.Empty;
        private string _barSpeedUp = string.Empty;

        /// <summary>细条指示层现在该不该画（开关打开 && 最近有流量）。</summary>
        private bool _barSpeedVisible;

        /// <summary>上一行 `[bar-privacy …]` 诊断内容：内容没变就不重复写日志（形变期本方法每帧都被调）。</summary>
        private string _barPrivacyDiagLast = string.Empty;

        /// <summary>最近一次"有流量"的时刻：短暂空档（缓冲、换包）不立刻收起数字，避免每秒闪一下。</summary>
        private DateTime _barSpeedLastTrafficUtc = DateTime.MinValue;

        /// <summary>连续多少拍超过阈值（要连续 3 拍才算"真在高速下载"）。</summary>
        private int _netSpeedFastTicks;

        /// <summary>提示冷却到期时刻。一场下载只该弹一次，否则每秒都会弹。</summary>
        private DateTime _netSpeedNextAlertUtc = DateTime.MinValue;

        private static readonly TimeSpan NetSpeedAlertCooldown = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan BarSpeedHold = TimeSpan.FromSeconds(2.5);

        /// <summary>网速提示胶囊停留秒数（固定值：这一条不打算做成设置项，避免设置页继续膨胀）。</summary>
        private const double NetSpeedAlertSeconds = 4;

        /// <summary>
        /// 网速阈值告警的时间窗。网速图层平时优先级 64（让位于下载提示 66），告警期间靠
        /// 图层注册表里的 PriorityOverride 升到 70 —— 否则下载提示每 2 秒续期一次，
        /// 告警的 Show 永远输在仲裁上：提示不出现、60 秒冷却却已经开始计时 = 提示丢失。
        /// </summary>
        private DateTime _netSpeedAlertUntilUtc = DateTime.MinValue;

        private void StartNetworkSpeedService()
        {
            if (_netSpeed != null) return;

            var service = new NetworkSpeedService();
            service.RefreshAdapters();
            _netSpeed = service;

            // 立即跑第一拍：只为建立字节基线（那一拍速率恒为 0），真正有数字要等第 2 拍
            _ticker.Register("netspeed", TimeSpan.FromSeconds(1), SampleNetworkSpeed, runImmediately: true);

            IslandLog.Debug("NetworkSpeed",
                service.AdapterNames.Count == 0
                    ? "网速采样已启动（当前无可用网卡，细条不显示）"
                    : $"网速采样已启动，统计网卡 {service.AdapterNames.Count} 块：{string.Join("、", service.AdapterNames)}");
        }

        private void StopNetworkSpeedService()
        {
            if (_ticker != null) _ticker.Unregister("netspeed");

            _netSpeed?.Dispose();
            _netSpeed = null;
            _externalNetwork.Reset();
            _netSpeedDownHistory.Clear();
            UpdateNetSpeedPrimary(default);
            UpdateNetSpeedSatelliteVisual(default);

            _netSpeedFastTicks = 0;
            _netSpeedNextAlertUtc = DateTime.MinValue;
            _barSpeedVisible = false;
            _barSpeedLastTrafficUtc = DateTime.MinValue;

            if (_layers != null && _layers.IsRegistered("netspeed")) _layers.Hide("netspeed");

            BarSpeedDownText.Text = string.Empty;
            BarSpeedUpText.Text = string.Empty;
            _barSpeedDown = string.Empty;
            _barSpeedUp = string.Empty;
            SyncBarIndicators();
        }

        private void SampleNetworkSpeed()
        {
            NetworkSpeedService? service = _netSpeed;
            if (service == null) return;

            NetworkSpeedSample sample = ReadNetworkSample(service);

            // 调试钩子：ALPHA_ISLAND_FAKE_NETSPEED=<KB/s> 时用合成采样替代真实采样，
            // 这样没有真实下载也能验证「下载期间网速常显」的胶囊与网速卡片曲线。
            if (!_netSpeedFakeParsed)
            {
                _netSpeedFakeParsed = true;
                string? fake = Environment.GetEnvironmentVariable("ALPHA_ISLAND_FAKE_NETSPEED");
                if (!string.IsNullOrWhiteSpace(fake) &&
                    double.TryParse(fake, out double fakeKbps) && fakeKbps > 0)
                {
                    _netSpeedFakeKbps = fakeKbps;
                    IslandLog.Debug("NetSpeed", $"调试钩子：网速强制为 {fakeKbps:0} KB/s（合成采样）");
                }
            }

            if (_netSpeedFakeKbps.HasValue)
            {
                double seconds = DateTime.UtcNow.TimeOfDay.TotalSeconds;
                double wave = 0.6 + 0.4 * Math.Sin(seconds * 1.1);   // 起伏让曲线看得出是活的
                double bytes = _netSpeedFakeKbps.Value * 1024;
                sample = new NetworkSpeedSample(bytes * wave, bytes * 0.06);
            }

            UpdateBarSpeed(sample);
            MaybeAlertHighSpeed(sample);
            UpdateNetSpeedSatelliteVisual(sample);

            // 卡片页 / 常显胶囊：每拍都推进（曲线不能因为没开卡片页就断点）。
            UpdateNetSpeedSample(sample);
        }

        /// <summary>
        /// 细条上的常驻网速：只写"有流量"的那一侧，两侧都没流量就整层让开（保持细条干净）。
        /// 数字比阈值提示优先 —— 用户开这个开关要的就是"随时看得到现在多少速度"。
        /// </summary>
        private void UpdateBarSpeed(NetworkSpeedSample sample)
        {
            bool enabled = _options != null && SettingsRegistry.GetBool(_options, "NetSpeedEnabled", false);

            if (enabled && sample.HasTraffic) _barSpeedLastTrafficUtc = DateTime.UtcNow;
            bool hold = enabled && DateTime.UtcNow - _barSpeedLastTrafficUtc < BarSpeedHold;

            // 半 KB/s 以下显示成整数 B 没有意义，直接当作没流量
            string down = hold && sample.DownBytesPerSecond >= 512
                ? "↓" + NetworkSpeedService.FormatSpeed(sample.DownBytesPerSecond)
                : string.Empty;
            string up = hold && sample.UpBytesPerSecond >= 512
                ? "↑" + NetworkSpeedService.FormatSpeed(sample.UpBytesPerSecond)
                : string.Empty;

            if (!string.Equals(down, _barSpeedDown, StringComparison.Ordinal))
            {
                _barSpeedDown = down;
                BarSpeedDownText.Text = down;
            }

            if (!string.Equals(up, _barSpeedUp, StringComparison.Ordinal))
            {
                _barSpeedUp = up;
                BarSpeedUpText.Text = up;
            }

            _barSpeedVisible = down.Length > 0 || up.Length > 0;
            SyncBarIndicators();
        }

        /// <summary>
        /// 细条指示层的内容开关。**当前为 false**：用户明确要求「最小化的岛不要显示东西」
        /// （细条上那行 `12K 12K 52%` 观感差）。整层因此永久熄灭，只留 <c>IslandBody</c>
        /// 那一条玻璃细线本身；占用灯已改到屏幕右上角的独立小窗里（见
        /// <see cref="Interop.PrivacyIndicatorWindow"/>），细条上不再需要小圆点。
        /// 想恢复只改这一处：下面那些标志与诊断都原样保留，不参与命中测试的约定也没变。
        /// </summary>
        private const bool BarIndicatorsEnabled = false;

        /// <summary>
        /// 细条指示层的显隐。**只在 Bar 态画**（Pill / Card 态本来就有自己的内容层，
        /// 数字挤进去只会打架），且不参与命中测试（细条整条都是拖拽/点击热区）。
        /// 由 <c>SyncNotificationLayers()</c> 在每次状态切换时、以及 <c>ApplyReveal()</c>
        /// 每帧形变时一起调用。
        /// </summary>
        internal void SyncBarIndicators()
        {
            if (BarSpeedLayer == null) return;

            // 细条只有 10px 高：收回过程里提前点亮，会在还在缩小的卡片中间浮出一行数字。
            // 等主弹簧基本收满（_reveal ≥ 0.9）再亮；反方向（展开）立刻熄灭。
            // 网速与内存共用这一层：任一侧有内容就点亮（没流量时内存百分比单独占位）。
            bool show = BarIndicatorsEnabled
                        && (_barSpeedVisible || _barMemVisible || _barAirVisible || _barMicVisible || _barCamVisible || _barAgentVisible)
                        && _state == IslandState.Bar && _reveal >= 0.9;
            double target = show ? 1.0 : 0.0;
            if (Math.Abs(BarSpeedLayer.Opacity - target) > 0.001) BarSpeedLayer.Opacity = target;

            // 飞行模式图标只在自己那一侧有内容时出现（细条上没有别的无线图标会打架）
            if (BarAirText != null)
            {
                BarAirText.Visibility = _barAirVisible ? Visibility.Visible : Visibility.Collapsed;
            }

            // 麦克风 / 摄像头小圆点（A 组⑤）
            if (BarPrivacyDots != null)
            {
                BarPrivacyDots.Visibility = (_barMicVisible || _barCamVisible) ? Visibility.Visible : Visibility.Collapsed;
            }
            if (BarMicDot != null)
            {
                BarMicDot.Visibility = _barMicVisible ? Visibility.Visible : Visibility.Collapsed;
            }
            if (BarCamDot != null)
            {
                BarCamDot.Visibility = _barCamVisible ? Visibility.Visible : Visibility.Collapsed;
            }

            // agent 状态色点（B 组③）：颜色由 UpdateAgentStatusUi 每次刷新时写入，这里只管亮灭。
            if (BarAgentDot != null)
            {
                BarAgentDot.Visibility = _barAgentVisible ? Visibility.Visible : Visibility.Collapsed;
            }

            // 占用灯（A 组⑤）的诊断：只有真的在占用时才打，用来断言「细条上那两枚点亮没亮」，
            // 因为岛窗是分层窗口 + DWM 背板，本机 `CopyFromScreen` 抓不到它的内容（见 README §15）。
            // 形变期间本方法每帧都被调，所以同一内容**只在变化时**打一行，否则日志会被刷爆
            // （实测 20s 能刷 350+ 行，512KB 轮转很快就顶掉别的现场）。
            if (_barMicVisible || _barCamVisible)
            {
                string privacyDiag = $"细条占用点：点亮={show}（形态={_state}，形变={_reveal:0.00}）"
                    + $" 麦克风={_barMicVisible} 摄像头={_barCamVisible} 层透明度={BarSpeedLayer.Opacity:0.00}"
                    + $" [bar-privacy show={show} state={_state} reveal={_reveal:0.00} mic={_barMicVisible} cam={_barCamVisible} opacity={BarSpeedLayer.Opacity:0.00}]";
                if (privacyDiag != _barPrivacyDiagLast)
                {
                    _barPrivacyDiagLast = privacyDiag;
                    IslandLog.Debug("Bar", privacyDiag);
                }
            }
            else
            {
                _barPrivacyDiagLast = "";
            }
        }

        /// <summary>
        /// 下行或上行速率持续超过阈值（MB/s）时弹一次一级胶囊。
        /// 连击口径：**连着 3 拍都在阈值之上**才算（一次网页加载/秒传的尖峰不算），
        /// 弹过进 60 秒冷却 —— 否则一整场下载会每秒弹一次。
        /// </summary>
        private void MaybeAlertHighSpeed(NetworkSpeedSample sample)
        {
            if (_options == null) return;

            bool notifyEnabled = SettingsRegistry.GetBool(_options, "NetSpeedNotifyEnabled", true);
            double thresholdMBps = SettingsRegistry.GetDouble(_options, "NetSpeedThresholdMbps", 10.0);
            if (!notifyEnabled || thresholdMBps <= 0)
            {
                _netSpeedFastTicks = 0;
                return;
            }

            double downMBps = sample.DownBytesPerSecond / (1024.0 * 1024.0);
            double upMBps = sample.UpBytesPerSecond / (1024.0 * 1024.0);

            if (downMBps < thresholdMBps && upMBps < thresholdMBps)
            {
                _netSpeedFastTicks = 0;
                return;
            }

            _netSpeedFastTicks++;
            if (_netSpeedFastTicks < 3) return;
            if (DateTime.UtcNow < _netSpeedNextAlertUtc) return;

            _netSpeedFastTicks = 0;
            _netSpeedNextAlertUtc = DateTime.UtcNow + NetSpeedAlertCooldown;
            _netSpeedAlertUntilUtc = DateTime.UtcNow + TimeSpan.FromSeconds(NetSpeedAlertSeconds);

            bool isDown = downMBps >= upMBps;
            UpdateNetSpeedPrimary(sample);
            NetSpeedNotifyLayer.ToolTip = $"{(isDown ? "高速下载提醒" : "高速上传提醒")} · 阈值 {thresholdMBps:0.#} MB/s";

            // 只在细条态主动弹胶囊：用户正开着的胶囊/卡片不该被一个下载提示顶掉
            // （形态判断已收口到 ShowPillNotification）。
            ShowPillNotification(IslandNotification.Seconds("netspeed", NetSpeedAlertSeconds));

            // 同 ShowSystemNotify：转成胶囊态后要马上结算，否则音乐胶囊还压在同一个格子上。
            SyncNotificationLayers();

            IslandLog.Debug("NetworkSpeed",
                $"{(isDown ? "下行" : "上行")} {(isDown ? downMBps : upMBps):0.0} MB/s 超过阈值 {thresholdMBps:0.#} MB/s，弹出提示（state={_state} action={GetCurrentActionType()}）");
        }

        /// <summary>设置页改了网速相关的开关/阈值后立即生效（不用等下一次 ApplyOptions）。</summary>
        private void OnNetSpeedOptionsChanged()
        {
            _netSpeedFastTicks = 0;

            if (_netSpeed == null)
            {
                // 开关是刚打开的：Sync 会拉起服务，但那一拍才建立基线，这里先按"无流量"把细条让开
                _barSpeedVisible = false;
                SyncBarIndicators();
                return;
            }

            _ticker?.TriggerNow("netspeed");
        }

        // ================= 内存占用 + 一键整理（A 组②） =================

        private MemoryService? _memory;

        /// <summary>细条上最后一次写下去的内存文本（避免每拍都重设 XAML 属性）。</summary>
        private string _barMemText = string.Empty;

        /// <summary>细条内存数字现在该不该画（开关打开 && 采样有效）。</summary>
        private bool _barMemVisible;

        /// <summary>连续多少拍超过告警阈值（要连续 3 拍才算"真的高"，编译/解压的瞬时峰值不算）。</summary>
        private int _memHighTicks;

        /// <summary>内存告警冷却到期时刻（5 分钟内只提示一次）。</summary>
        private DateTime _memNextAlertUtc = DateTime.MinValue;

        private static readonly TimeSpan MemoryAlertCooldown = TimeSpan.FromMinutes(5);

        /// <summary>系统状态提示的默认停留秒数（内存/功率/飞行模式/隐私灯共用）。</summary>
        private const double SystemNotifySeconds = 4;

        private void StartMemoryService()
        {
            if (_memory != null) return;

            var service = new MemoryService();
            service.Sample();
            _memory = service;

            // 内存不像网速那样每秒都在变，2 秒一拍足够（也顺带把任务栏/细条的重排次数压下来）
            _ticker.Register("memory", TimeSpan.FromSeconds(2), SampleMemory, runImmediately: true);

            IslandLog.Debug("Memory",
                service.Current.Valid
                    ? $"内存采样已启动：物理内存 {MemoryService.FormatSize((long)service.Current.TotalBytes)}，当前占用 {service.Current.UsedPercent:0}%"
                    : "内存采样已启动（GlobalMemoryStatusEx 不可用，细条不显示占用率）");
        }

        private void StopMemoryService()
        {
            if (_ticker != null) _ticker.Unregister("memory");

            _memory?.Dispose();
            _memory = null;

            _memHighTicks = 0;
            _memNextAlertUtc = DateTime.MinValue;
            _barMemVisible = false;
            _barMemText = string.Empty;
            BarMemText.Text = string.Empty;
            RefreshCombinedResources();
            SyncBarIndicators();
        }

        private void SampleMemory()
        {
            MemoryService? service = _memory;
            if (service == null) return;

            service.Sample();
            MemorySample sample = service.Current;

            UpdateBarMemory(sample);
            RefreshCombinedResources();
            MaybeAlertHighMemory(sample);

            // 系统状态卡片页正显示时跟着刷新（页不显示就不做无谓的写文本 + 日志）。
            if (IsSystemCardVisible) SyncSystemCard();
        }

        /// <summary>
        /// 细条上的常驻内存占用率。内存百分比是"一直想看到"的量，
        /// 所以没有网速那套"有流量才显示 + 保持 2.5 秒"的收放逻辑：开关打开就常亮。
        /// </summary>
        private void UpdateBarMemory(MemorySample sample)
        {
            bool enabled = _options != null && SettingsRegistry.GetBool(_options, "MemoryMonitorEnabled", false);
            string text = enabled && sample.Valid ? $"{sample.UsedPercent:0}%" : string.Empty;

            if (!string.Equals(text, _barMemText, StringComparison.Ordinal))
            {
                _barMemText = text;
                BarMemText.Text = text;
            }

            _barMemVisible = text.Length > 0;
            SyncBarIndicators();
        }

        /// <summary>
        /// 占用率持续超过阈值时提示一次。和网速提示同一套口径：
        /// 连着 3 拍才算（2 秒一拍 → 至少高 6 秒），弹过进 5 分钟冷却。
        /// <b>只提示，绝不自动整理</b>：悄悄动用户的进程工作集是意外行为，整理必须由用户点。
        /// </summary>
        private void MaybeAlertHighMemory(MemorySample sample)
        {
            if (_options == null) return;

            bool notifyEnabled = SettingsRegistry.GetBool(_options, "MemoryMonitorEnabled", false);
            double threshold = SettingsRegistry.GetInt(_options, "MemoryNotifyPercent", 90);
            if (!notifyEnabled || !sample.Valid || threshold <= 0)
            {
                _memHighTicks = 0;
                return;
            }

            if (!sample.Exceeds(threshold))
            {
                _memHighTicks = 0;
                return;
            }

            _memHighTicks++;
            if (_memHighTicks < 3) return;
            if (DateTime.UtcNow < _memNextAlertUtc) return;

            _memHighTicks = 0;
            _memNextAlertUtc = DateTime.UtcNow + MemoryAlertCooldown;

            ShowSystemNotify("\uE950", "内存占用偏高",
                $"已用 {MemoryService.FormatSize(sample.UsedBytes)} / {MemoryService.FormatSize((long)sample.TotalBytes)}｜阈值 {threshold:0}%",
                $"{sample.UsedPercent:0}%");

            IslandLog.Debug("Memory",
                $"占用率 {sample.UsedPercent:0}% 超过阈值 {threshold:0}%，弹出提示（state={_state} action={GetCurrentActionType()}）");
        }

        /// <summary>
        /// 弹出"系统状态"通用提示（A 组②③④⑤共用一层）。
        /// 内容全部在弹出前写入，所以新增一类提示不需要再加 XAML 图层。
        /// </summary>
        internal void ShowSystemNotify(string glyph, string title, string detail, string value, double seconds = SystemNotifySeconds)
        {
            if (SystemNotifyLayer == null) return;
            if (!_exiting) _desktopInbox.Add(new("system-" + DateTimeOffset.UtcNow.UtcTicks, "", "Windows", title, detail + " · " + value, DateTimeOffset.Now, DesktopEventKind.System), false);
            if (_sessionLocked || SettingsRegistry.GetBool(_options, "NotificationQuietMode", false)) return;

            SysNotifyGlyph.Text = glyph;
            var accent = glyph == "\uE950" ? Color.FromRgb(0xFB, 0xBF, 0x24) : glyph == "\uE945" ? Color.FromRgb(0xFB, 0x92, 0x3C) : Color.FromRgb(0x22, 0xD3, 0xEE);
            SysNotifyGlyph.Foreground = new SolidColorBrush(accent);
            SysNotifyTitle.Text = title;
            SysNotifyDetail.Text = detail;
            SysNotifyValue.Text = value;

            // 和网速提示一样：细条态主动弹胶囊，不抢占用户正开着的胶囊/卡片
            // （形态判断已收口到 ShowPillNotification）。
            ShowPillNotification(IslandNotification.Seconds("system", seconds));

            // 弹出后立刻结算图层显隐：细条态刚转成胶囊态，这一拍才轮到本层（PillOnly）上场，
            // 同时把音乐胶囊压下去 —— 少了这一句两层就会在同一次动画里叠着画。
            SyncNotificationLayers();
        }

        /// <summary>
        /// 一键内存整理（托盘菜单「🧹 整理内存」）。只回收<b>本进程</b>工作集：
        /// EmptyWorkingSet 对自身免提权；全系统整理（MemoryTrimSystem）需要管理员权限，本批不提供。
        /// </summary>
        private bool _memoryTrimBusy;
        internal async void TrimMemoryNow()
        {
            if (_options == null || _memoryTrimBusy) return;

            if (!SettingsRegistry.GetBool(_options, "MemoryTrimEnabled", false))
            {
                // 入口已经变灰了，正常点不到；这里是防"改完设置没生效"或托盘回调乱入
                IslandLog.Debug("Memory", "一键内存整理被设置项关闭（MemoryTrimEnabled=false），忽略本次请求");
                return;
            }

            _memoryTrimBusy = true;
            try
            {
                var result = await Task.Run(() =>
                {
                    using var service = new MemoryService();
                    service.Sample();
                    long before = service.Current.OwnWorkingSetBytes;
                    long released = service.TrimOwnWorkingSet();
                    service.Sample();
                    return (before, released, after: service.Current.OwnWorkingSetBytes);
                });
                if (_exiting) return;
                var (before, released, after) = result;
                if (_memory != null) { _memory.Sample(); UpdateBarMemory(_memory.Current); }

                string detail;
                string value;
                if (released > 0)
                {
                    value = "-" + MemoryService.FormatSize(released);
                    detail = $"本进程工作集 {MemoryService.FormatSize(before)} → {MemoryService.FormatSize(after)}｜仅优化本程序";
                }
                else
                {
                    value = "—";
                    detail = "本进程工作集已经很低，没有可回收的页｜仅优化本程序";
                }

                ShowSystemNotify("\uE950", released > 0 ? "已整理内存" : "内存已整理", detail, value);

                IslandLog.Debug("Memory",
                    $"一键整理完成：释放 {MemoryService.FormatSize(released)}（仅本进程；系统级整理需管理员权限，本批不提供）");
            }
            catch (Exception ex)
            {
                IslandLog.Warn("Memory", "整理失败: " + ex.Message);
            }
            finally
            {
                _memoryTrimBusy = false;
            }
        }

        /// <summary>设置页改了内存相关开关后立即生效。</summary>
        private void OnMemoryOptionsChanged()
        {
            if (_options == null) return;

            _memHighTicks = 0;

            // 「整理内存」菜单项跟着开关走（菜单项恒在，只是变灰）
            _tray?.SetMemoryTrimEnabled(SettingsRegistry.GetBool(_options, "MemoryTrimEnabled", false));

            if (_memory == null)
            {
                _barMemVisible = false;
                SyncBarIndicators();
                return;
            }

            _ticker?.TriggerNow("memory");
        }

        // ─────────────────────────── A 组③：功率检测 ───────────────────────────
        // 复用 BatteryService（不新建服务）：电池供电时取放电功率与剩余时长，
        // 功率持续高于阈值就弹一条 system 提示；插着电源不提示。
        private int _powerHighTicks;
        private DateTime _powerNextAlertUtc = DateTime.MinValue;
        private double? _powerFakeWatts;          // 调试钩子：伪造放电功率
        private static readonly TimeSpan PowerAlertCooldown = TimeSpan.FromMinutes(10);
        private const double PowerSampleSeconds = 5;
        private const int PowerAlertTicks = 3;

        private void StartPowerSampler()
        {
            if (_ticker == null) return;
            _ticker.Register("power", TimeSpan.FromSeconds(PowerSampleSeconds), SamplePower, runImmediately: true);
        }

        private void StopPowerSampler()
        {
            _powerHighTicks = 0;
            _ticker?.Unregister("power");
        }

        private void SamplePower()
        {
            // 调试注入（ALPHA_ISLAND_POWER_FAKE=<瓦数>）：插电时也能验证提示链路
            if (_powerFakeWatts.HasValue)
            {
                var fakeInfo = new BatteryStatusInfo(50, false, BatteryChargeType.Unknown,
                    BatteryPowerEvent.None, null, (int)Math.Round(_powerFakeWatts.Value * 1000), 9000);
                RecordPowerReading(fakeInfo);   // 系统状态卡片页的功耗格
                MaybeAlertHighPower(fakeInfo);
                return;
            }

            if (_battery == null) return;

            var info = _battery.Current;
            if (info == null || DateTime.UtcNow - info.SampledUtc > TimeSpan.FromSeconds(15))
            {
                _powerSampled = false;
                if (IsSystemCardVisible) SyncSystemCard();
                return;
            }

            RecordPowerReading(info);   // 系统状态卡片页的功耗格
            MaybeAlertHighPower(info);
        }

        private void MaybeAlertHighPower(BatteryStatusInfo info)
        {
            if (_options == null) return;

            bool enabled = SettingsRegistry.GetBool(_options, "PowerNotifyEnabled", false);
            double thresholdWatts = SettingsRegistry.GetDouble(_options, "PowerNotifyWatts", 25);

            // 插电 / 掉电 / 关开关：退出告警计数（冷却时间戳不动，避免关一次开关就重置冷却）
            if (!enabled || !info.HasBattery || info.IsOnAcPower == true || !info.DischargeRateMilliwatts.HasValue)
            {
                _powerHighTicks = 0;
                return;
            }

            double watts = info.DischargeRateMilliwatts.Value / 1000.0;
            if (watts < thresholdWatts)
            {
                _powerHighTicks = 0;
                return;
            }

            _powerHighTicks++;
            if (_powerHighTicks < PowerAlertTicks) return;

            var now = DateTime.UtcNow;
            if (now < _powerNextAlertUtc) return;
            _powerNextAlertUtc = now + PowerAlertCooldown;
            _powerHighTicks = 0;

            string detail = $"当前放电 {BatteryService.FormatPower(info.DischargeRateMilliwatts)}" +
                            $"｜预计剩余 {BatteryService.FormatRemaining(info.RemainingLifeSeconds)}" +
                            $"｜阈值 {thresholdWatts:0.#} W";
            IslandLog.Debug("Power", $"放电 {watts:0.#} W 超过阈值 {thresholdWatts:0.#} W，弹出提示");
            ShowSystemNotify("\uE945", "功耗偏高", detail, BatteryService.FormatPower(info.DischargeRateMilliwatts));
        }

        /// <summary>设置页改了功率开关/阈值后立即生效。</summary>
        private void OnPowerOptionsChanged()
        {
            _powerHighTicks = 0;
            _ticker?.TriggerNow("power");
        }

        // ─────────────────────────── A 组④：飞行模式（只读） ───────────────────────────
        // 只读不切换：Radio.GetRadiosAsync() 看所有无线电是不是都关了；
        // NetworkStatusChanged 事件负责"立刻重查"（拨开关/插网线），ticker 兜底每 10s 复查。
        private bool _airplaneOn;
        private bool _airplaneKnown;
        private bool _barAirVisible;
        private bool _radioBusy;
        private bool? _airplaneFake;              // 调试钩子：伪造飞行模式状态
        private const double RadioSampleSeconds = 10;

        private void StartTelemetryService()
        {
            if (_ticker != null)
            {
                _ticker.Register("radio", TimeSpan.FromSeconds(RadioSampleSeconds),
                    () => _ = SampleRadioAsync(), runImmediately: true);
            }

            try
            {
                Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
            }
            catch (Exception ex)
            {
                IslandLog.Warn("Radio", "订阅 NetworkStatusChanged 失败: " + ex.Message);
            }
        }

        private void StopTelemetryService()
        {
            try
            {
                Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
            }
            catch { /* 退出阶段忽略 */ }

            _ticker?.Unregister("radio");
            _airplaneOn = false;
            _airplaneKnown = false;
            _barAirVisible = false;
            SyncBarIndicators();
        }

        private void OnNetworkStatusChanged(object sender)
        {
            // 这个事件可能来自非 UI 线程
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => _ = SampleRadioAsync()));
        }

        private async Task SampleRadioAsync()
        {
            if (_radioBusy) return;
            _radioBusy = true;

            try
            {
                bool on;
                if (_airplaneFake.HasValue)
                {
                    on = _airplaneFake.Value;
                }
                else
                {
                    var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync().AsTask();
                    // 每台无线电的开关不能证明系统飞行模式的开关状态。
                    _airplaneKnown = false;
                    _barAirVisible = false;
                    _wirelessSummary = radios.Count == 0 ? "未检测到无线设备" : string.Join(" / ", radios.Select(r =>
                        r.Kind + " " + (r.State == Windows.Devices.Radios.RadioState.On ? "开" :
                            r.State == Windows.Devices.Radios.RadioState.Off ? "关" : "未知")));
                    SyncBarIndicators();
                    if (IsSystemCardVisible) SyncSystemCard();
                    return;
                }

                ApplyAirplaneState(on);
            }
            catch (Exception ex)
            {
                IslandLog.Warn("Radio", "读取飞行模式失败: " + ex.Message);
                _airplaneKnown = false;
                _barAirVisible = false;
                _wirelessSummary = "无线设备状态读取失败";
                SyncBarIndicators();
                if (IsSystemCardVisible) SyncSystemCard();
            }
            finally
            {
                _radioBusy = false;
            }
        }

        private void ApplyAirplaneState(bool on)
        {
            bool changed = _airplaneKnown && on != _airplaneOn;
            _airplaneOn = on;

            bool barEnabled = _options == null || SettingsRegistry.GetBool(_options, "AirplaneBarEnabled", true);
            _barAirVisible = on && barEnabled;
            SyncBarIndicators();
            IslandLog.Debug("Radio", $"飞行模式 = {(on ? "开" : "关")}，细条图标 = {(_barAirVisible ? "显示" : "隐藏")}" +
                                     $"，首次已知 = {(!changed && !_airplaneKnown ? "是" : "否")}，变化 = {(changed ? "是" : "否")}");

            if (changed && _options != null && SettingsRegistry.GetBool(_options, "AirplaneNotifyEnabled", false))
            {
                IslandLog.Debug("Radio", on ? "飞行模式已开启，弹出提示" : "飞行模式已关闭，弹出提示");
                ShowSystemNotify("\uE709",
                    on ? "已进入飞行模式" : "已退出飞行模式",
                    on ? "无线网络已关闭｜网速检测将没有数据" : "无线网络已恢复",
                    on ? "开" : "关");
            }

            _airplaneKnown = true;

            // 系统状态卡片页的飞行模式格（必须在 _airplaneKnown = true 之后刷，否则还会写"未知"）。
            if (IsSystemCardVisible) SyncSystemCard();
        }

        /// <summary>设置页改了飞行模式相关开关后立即生效。</summary>
        private void OnTelemetryOptionsChanged()
        {
            bool barEnabled = _options == null || SettingsRegistry.GetBool(_options, "AirplaneBarEnabled", true);
            _barAirVisible = _airplaneOn && barEnabled;
            SyncBarIndicators();
            _ticker?.TriggerNow("radio");
        }

        // ─────────────────────────── A 组⑤：麦克风 / 摄像头占用灯 ───────────────────────────
        // 只读 ConsentStore 注册表（免提权免授权）。有应用开用就亮细条小圆点 + 弹一条提示，
        // 「从无人使用 → 有人使用」的那一拍才提示（不是每次采样都提示）。
        private PrivacyIndicatorService? _privacy;
        private PrivacyIndicatorWindow? _privacyIndicator;
        private bool _barMicVisible;
        private bool _barCamVisible;
        private bool _privacyMicActive;
        private bool _privacyCamActive;
        private DateTime _privacyMicNextNotifyUtc = DateTime.MinValue;
        private DateTime _privacyCamNextNotifyUtc = DateTime.MinValue;
        /// <summary>调试钩子的零点：<c>_OFF</c>/<c>_ON</c> 的循环以它为基准算相位。</summary>
        private DateTime _privacyFakeStartUtc;
        /// <summary>调试钩子的两个时长：<c>_OFF</c>=假占用持续多久，<c>_ON</c>=结束后隔多久再开（&gt;0 即循环演示）。</summary>
        private double _privacyFakeOffSeconds;
        private double _privacyFakeResumeSeconds = -1;   // -1 = 还没读过环境变量
        private bool? _privacyFakeLastOn;                // 只为在开关边界打一行日志（验收脚本要断言）
        private static readonly TimeSpan PrivacyNotifyCooldown = TimeSpan.FromSeconds(30);
        private const double PrivacySampleSeconds = 3;

        private void StartPrivacyService()
        {
            _privacy ??= new PrivacyIndicatorService();
            if (_ticker != null)
            {
                _ticker.Register("privacy", TimeSpan.FromSeconds(PrivacySampleSeconds), SamplePrivacy, runImmediately: true);
            }
        }

        private void StopPrivacyService()
        {
            _ticker?.Unregister("privacy");
            _privacy = null;
            _barMicVisible = false;
            _barCamVisible = false;
            _privacyMicActive = false;
            _privacyCamActive = false;
            SyncBarIndicators();
            // 服务停掉（关了开关 / 正在退出）时角落的灯也必须跟着灭，不能留在屏幕上。
            _privacyIndicator?.SetActive(false, false);
        }

        private void SamplePrivacy()
        {
            if (_privacy == null) return;

            string? micApp = null;
            string? camApp = null;
            try
            {
                foreach (var usage in _privacy.Sample())
                {
                    if (usage.Capability == PrivacyCapability.Microphone) micApp ??= usage.AppName;
                    else camApp ??= usage.AppName;
                }
            }
            catch (Exception ex)
            {
                IslandLog.Warn("Privacy", "采样失败: " + ex.Message);
                return;
            }

            bool micNow = !string.IsNullOrEmpty(micApp);
            bool camNow = !string.IsNullOrEmpty(camApp);

            // 调试钩子：本机前台常被全屏游戏占住，真开麦克风/摄像头不方便，用环境变量注入一个假占用
            // （ALPHA_ISLAND_FAKE_PRIVACY=mic / cam / both），走的是同一条生产路径。
            string? fake = Environment.GetEnvironmentVariable("ALPHA_ISLAND_FAKE_PRIVACY");
            if (!string.IsNullOrEmpty(fake))
            {
                // 两个时长只在第一拍读一次：
                //   ALPHA_ISLAND_FAKE_PRIVACY_OFF=6 → 假占用 6 秒后自动结束（验收「占用结束 → 灯真的消失」）
                //   ALPHA_ISLAND_FAKE_PRIVACY_ON=4  → 结束后再等 4 秒重新开始（**循环演示**，反复看那两条出退场动画）
                if (_privacyFakeResumeSeconds < 0)
                {
                    _privacyFakeStartUtc = DateTime.UtcNow;
                    _privacyFakeOffSeconds = ParseSeconds("ALPHA_ISLAND_FAKE_PRIVACY_OFF", 0);
                    _privacyFakeResumeSeconds = ParseSeconds("ALPHA_ISLAND_FAKE_PRIVACY_ON", 0);
                }

                // 用「起点 + 取模」算当前相位。写成「到期就把结束时刻往后推 OFF+ON」的版本会让假占用永远不到期
                // （只出现一次出场动画、看不到退场），所以这里必须按起点取模。
                double elapsed = (DateTime.UtcNow - _privacyFakeStartUtc).TotalSeconds;
                bool fakeOn = _privacyFakeOffSeconds <= 0                             // 只给了 FAKE_PRIVACY：一直亮
                    || (_privacyFakeResumeSeconds > 0                                  // OFF 亮 + ON 灭，循环
                        ? elapsed % (_privacyFakeOffSeconds + _privacyFakeResumeSeconds) < _privacyFakeOffSeconds
                        : elapsed < _privacyFakeOffSeconds);                           // 只结束一次，之后不再亮

                if (_privacyFakeLastOn != fakeOn)
                {
                    _privacyFakeLastOn = fakeOn;
                    IslandLog.Debug("Privacy", $"调试钩子：假占用 {(fakeOn ? "开始" : "结束")}（第 {elapsed:0.0}s）"
                        + $" [privacy-fake on={fakeOn} elapsed={elapsed:0.0}]");
                }

                if (fakeOn)
                {
                    // 取值：mic / cam / mic+cam / both / all（同时含 mic 与 cam 的字符串都行）
                    bool all = fake.Contains("both", StringComparison.OrdinalIgnoreCase) ||
                               fake.Contains("all", StringComparison.OrdinalIgnoreCase);
                    if (all || fake.Contains("mic", StringComparison.OrdinalIgnoreCase)) { micApp ??= "调试占用（mic）"; micNow = true; }
                    if (all || fake.Contains("cam", StringComparison.OrdinalIgnoreCase)) { camApp ??= "调试占用（cam）"; camNow = true; }
                }
            }

            var now = DateTime.UtcNow;

            // 「开始使用」的那一拍提示一次；30s 冷却防止应用反复开关刷屏
            if (micNow && !_privacyMicActive && now >= _privacyMicNextNotifyUtc)
            {
                _privacyMicNextNotifyUtc = now + PrivacyNotifyCooldown;
                IslandLog.Debug("Privacy", $"麦克风开始被使用（{micApp}），弹出提示");
                ShowSystemNotify("\uE720", "麦克风使用中", $"{micApp} 正在使用麦克风", string.Empty);
            }

            if (camNow && !_privacyCamActive && now >= _privacyCamNextNotifyUtc)
            {
                _privacyCamNextNotifyUtc = now + PrivacyNotifyCooldown;
                IslandLog.Debug("Privacy", $"摄像头开始被使用（{camApp}），弹出提示");
                ShowSystemNotify("\uE722", "摄像头使用中", $"{camApp} 正在使用摄像头", string.Empty);
            }

            _privacyMicActive = micNow;
            _privacyCamActive = camNow;
            _barMicVisible = micNow;
            _barCamVisible = camNow;
            SyncBarIndicators();
            SyncPrivacyIndicator(micApp, camApp);

            // 系统状态卡片页的隐私格要用到"是哪个应用"，先记名字再刷。
            RecordPrivacyApps(micApp, camApp);
            if (IsSystemCardVisible) SyncSystemCard();

            IslandLog.Debug("Privacy", $"使用中：麦克风 = {micApp ?? "—"}，摄像头 = {camApp ?? "—"}，" +
                                       $"角落灯 = 麦克风:{(micNow ? "亮" : "灭")} 摄像头:{(camNow ? "亮" : "灭")}" +
                                       $"（细条内容按用户要求整体关闭，见 BarIndicatorsEnabled）");
        }

        /// <summary>
        /// 屏幕右上角的占用灯（A 组⑤ 角落版）：和细条上那两枚点是同一份状态，但独立成窗、常驻屏幕右上角，
        /// 岛收成细条（甚至岛被隐藏）时它照样在。窗口按需创建 —— 从没用过麦克风/摄像头的人不会多一个窗。
        /// </summary>
        private void SyncPrivacyIndicator(string? micApp, string? camApp)
        {
            if (_exiting)
            {
                return;
            }

            bool mic = _barMicVisible;
            bool cam = _barCamVisible;
            if (!mic && !cam && _privacyIndicator == null)
            {
                return;
            }

            try
            {
                _privacyIndicator ??= new PrivacyIndicatorWindow();
                _privacyIndicator.SetActive(mic, cam, micApp, camApp);
            }
            catch (Exception ex)
            {
                IslandLog.Warn("Privacy", "角落指示灯更新失败: " + ex.Message);
            }
        }

        /// <summary>设置页改了占用灯开关后立即生效。</summary>
        private void OnPrivacyOptionsChanged()
        {
            _privacyMicActive = false;
            _privacyCamActive = false;
            _ticker?.TriggerNow("privacy");
        }
    }
}
