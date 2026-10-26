using System;
using alpha_island.Core;
using alpha_island.Services;

namespace alpha_island
{
    /// <summary>
    /// 服务装配（阶段 3.3）。这里只放「创建实例 / 订阅事件 / 退订释放」，
    /// 状态机、启停秩序与降级报告都交给 <see cref="IslandServiceHost"/>。
    /// <para>
    /// 装配纪律（历史事故换来的，改代码前必读）：
    /// <list type="number">
    ///   <item>先赋值字段、再把阻塞的 <c>Start()</c> 丢后台 —— 否则 <c>ApplyOptions</c> / 设置窗会看到
    ///     <c>== null</c> 而再 new 一个（旧实例的 COM 回调/WinRT watcher 永不释放）。</item>
    ///   <item>重型服务（SMTC / 频谱 / 蓝牙…）一律 <c>AutoStart = false</c>：只能由启动序列在问候动画之后
    ///     逐个 <c>EnsureStarted</c>，不能被构造函数里的 <c>ApplyOptions</c> 提前拽起来。</item>
    ///   <item>只有「用户开关直接控制」的三个服务（音量 / 电池 / 蓝牙）用 <c>AutoStart = true</c>。</item>
    /// </list>
    /// </para>
    /// </summary>
    public partial class MainWindow
    {
        // ================= 服务宿主（阶段 3.3） =================

        private readonly IslandServiceHost _services = new();

        /// <summary>注册全部服务并把自己的状态区块挂进「关于」页。只应在构造函数里调一次。</summary>
        private void BuildServiceHost()
        {
            _services.Register(new DelegateIslandService(IslandServiceIds.GameActivity, IslandServiceIds.DisplayName(IslandServiceIds.GameActivity),
                options => SettingsRegistry.GetBool(options, "NotificationGameQuietEnabled", true) || SettingsRegistry.GetBool(options, "ProductivityEnabled", true)
                    || SettingsRegistry.GetBool(options, "TimelineEnabled", true) || SettingsRegistry.GetBool(options, "AgentProactiveEnabled", true)
                    || SettingsRegistry.GetBool(options, "SystemHealthEnabled", true) || SettingsRegistry.GetBool(options, "HabitLearningEnabled", true),
                StartGameActivity, StopGameActivity, _ => OnGameActivityOptionsChanged(), autoStart: true));
            _services.Register(new DelegateIslandService(IslandServiceIds.Cpu, IslandServiceIds.DisplayName(IslandServiceIds.Cpu),
                options => SettingsRegistry.GetBool(options, "CpuMonitorEnabled", true) || SettingsRegistry.GetBool(options, "SystemHealthEnabled", true), StartCpuService, StopCpuService, autoStart: true));
            _services.Register(new DelegateIslandService(IslandServiceIds.DesktopNotifications, IslandServiceIds.DisplayName(IslandServiceIds.DesktopNotifications),
                options => SettingsRegistry.GetBool(options, "SystemToastEnabled", true), StartSystemToastService, StopSystemToastService, autoStart: true));
            _services.Register(new DelegateIslandService(IslandServiceIds.MessengerNotifications, IslandServiceIds.DisplayName(IslandServiceIds.MessengerNotifications),
                options => SettingsRegistry.GetBool(options, "NotificationMessengerEnabled", true),
                StartMessengerNotifications, StopMessengerNotifications, _ => OnMessengerOptionsChanged(), autoStart: true));
            _services.Register(new DelegateIslandService(IslandServiceIds.SessionEvents, IslandServiceIds.DisplayName(IslandServiceIds.SessionEvents),
                options => SettingsRegistry.GetBool(options, "SessionEventNotifyEnabled", true), StartSessionEvents, StopSessionEvents, autoStart: true));
            // 低频采样的统一节拍（阶段 3.6）。网速/内存/功率/无线开关/隐私灯都往这里挂订阅，
            // 不再各写一个 DispatcherTimer；没有活跃订阅时它自己会把定时器停掉。
            _resourceMonitor.Changed += () =>
            {
                if (_exiting) return;
                UpdateResourceSnapshot(_resourceMonitor.Values.Snapshot);
                _settingsWindow?.UpdateHardwarePowerState(_resourceMonitor.Power
                    ?? new(null, null, "硬件功耗监测未开启"));
            };
            _resourceMonitor.MemorySampled += OnMemorySampled;
            BindResourceState();
            IsVisibleChanged += (_, _) => SyncResourceSampling();
            StateChanged += (_, _) => SyncResourceSampling();
            DiagnosticsReport.RegisterSection("采样调度", () => _ticker.Report());
            _services.Register(new DelegateIslandService(IslandServiceIds.HardwarePower, IslandServiceIds.DisplayName(IslandServiceIds.HardwarePower),
                options => SettingsRegistry.GetBool(options, "StatusShowPower", true), StartHardwarePowerService, StopHardwarePowerService, autoStart: true));

            // 「关于」页的服务状态区块：直接读宿主，切到那页时现取。
            DiagnosticsReport.RegisterSection("服务状态", () => _services.Report());
            DiagnosticsReport.RegisterSection("助手接入", () => AgentIntegrationService.DiagnosticsText()
                + "\n" + (_bridge?.StatusDiagnostics ?? "状态桥接未运行"));
            DiagnosticsReport.RegisterSection("游戏免打扰", () => _gameActivity == null ? "检测未开启"
                : $"前台事件监听={_gameActivity.EventsRunning}；自动静默={GameDoNotDisturbActive}；游戏数={_gameActivity.Current.Count}；来源={_gameActivity.Current.Source}");

            // ── 常驻基础设施：Required，永远不停；创建时机由启动序列决定 ──
            _services.Register(new DelegateIslandService(
                IslandServiceIds.Lyrics, IslandServiceIds.DisplayName(IslandServiceIds.Lyrics),
                _ => true, StartLyricsService, StopLyricsService,
                options => { if (_lyrics != null) _lyrics.Enabled = options.LyricsEnabled; },
                required: true));

            _services.Register(new DelegateIslandService(
                IslandServiceIds.Clipboard, IslandServiceIds.DisplayName(IslandServiceIds.Clipboard),
                _ => true, StartClipboardService, StopClipboardService,
                required: true));

            _services.Register(new DelegateIslandService(
                IslandServiceIds.FileTransfer, IslandServiceIds.DisplayName(IslandServiceIds.FileTransfer),
                _ => true, StartFileTransferService, StopFileTransferService,
                required: true));

            _services.Register(new DelegateIslandService(
                IslandServiceIds.Smtc, IslandServiceIds.DisplayName(IslandServiceIds.Smtc),
                _ => true, StartSmtcService, StopSmtcService,
                required: true));

            _services.Register(new DelegateIslandService(
                IslandServiceIds.Spectrum, IslandServiceIds.DisplayName(IslandServiceIds.Spectrum),
                _ => true, StartSpectrumService, StopSpectrumService,
                required: true));

            // ── 用户开关直接控制的三个：AutoStart，由 Sync 负责启停 ──
            _services.Register(new DelegateIslandService(
                IslandServiceIds.Volume, IslandServiceIds.DisplayName(IslandServiceIds.Volume),
                options => options.VolumeNotifyEnabled,
                StartVolumeService, StopVolumeService,
                autoStart: true));

            _services.Register(new DelegateIslandService(
                IslandServiceIds.Battery, IslandServiceIds.DisplayName(IslandServiceIds.Battery),
                options => true, // Charging presence needs fresh battery data even when popups are disabled.
                StartBatteryService, StopBatteryService,
                _ => OnPowerOptionsChanged(),
                autoStart: true));

            _services.Register(new DelegateIslandService(
                IslandServiceIds.Bluetooth, IslandServiceIds.DisplayName(IslandServiceIds.Bluetooth),
                options => true, // The battery card also needs connected-device inventory, independently of reminders.
                StartBluetoothService, StopBluetoothService,
                autoStart: true));

            // ── 网速（纯用户开关控制，默认关闭，不需要提权）──
            _services.Register(new DelegateIslandService(
                IslandServiceIds.NetworkSpeed, IslandServiceIds.DisplayName(IslandServiceIds.NetworkSpeed),
                options => SettingsRegistry.GetBool(options, "NetSpeedEnabled", true) || SettingsRegistry.GetBool(options, "DailyTrafficEnabled", true),
                StartNetworkSpeedService, StopNetworkSpeedService,
                _ => OnNetSpeedOptionsChanged(),
                autoStart: true));

            // ── 物理内存占用采样 ──
            _services.Register(new DelegateIslandService(
                IslandServiceIds.MemoryMonitor, IslandServiceIds.DisplayName(IslandServiceIds.MemoryMonitor),
                options => SettingsRegistry.GetBool(options, "MemoryMonitorEnabled", false) || SettingsRegistry.GetBool(options, "SystemHealthEnabled", true),
                StartMemoryService, StopMemoryService,
                _ => OnMemoryOptionsChanged(),
                autoStart: true));

            // ── Wi-Fi / 蓝牙独立开关监听（只读）──
            _services.Register(new DelegateIslandService(
                IslandServiceIds.Telemetry, IslandServiceIds.DisplayName(IslandServiceIds.Telemetry),
                options => SettingsRegistry.GetBool(options, "NotificationWifiRadio", true)
                           || SettingsRegistry.GetBool(options, "NotificationBluetoothRadio", true),
                StartTelemetryService, StopTelemetryService,
                _ => OnTelemetryOptionsChanged(),
                autoStart: true));

            // ── 麦克风 / 摄像头占用灯（只读 ConsentStore 注册表，免提权免授权）──
            _services.Register(new DelegateIslandService(
                IslandServiceIds.PrivacyIndicator, IslandServiceIds.DisplayName(IslandServiceIds.PrivacyIndicator),
                options => SettingsRegistry.GetBool(options, "PrivacyIndicatorEnabled", false),
                StartPrivacyService, StopPrivacyService,
                _ => OnPrivacyOptionsChanged(),
                autoStart: true));

            // ── agent 工具权限审批桥接（自己绑 127.0.0.1，免提权；默认关闭）──
            _services.Register(new DelegateIslandService(
                IslandServiceIds.AgentBridge, IslandServiceIds.DisplayName(IslandServiceIds.AgentBridge),
                options => SettingsRegistry.GetBool(options, "AgentBridgeEnabled", false),
                StartAgentBridge, StopAgentBridge,
                _ => OnAgentBridgeOptionsChanged(),
                autoStart: true));

            _services.Register(new DelegateIslandService(
                IslandServiceIds.TrafficMonitorLink, IslandServiceIds.DisplayName(IslandServiceIds.TrafficMonitorLink),
                options => SettingsRegistry.GetBool(options, "DailyTrafficEnabled", true),
                StartTrafficLinkService, StopTrafficLinkService,
                _ => OnTrafficOptionsChanged(),
                autoStart: true));

            // ── 第二批③：下载进度（只读浏览器 History 与 Steam 清单；默认开启）──
            _services.Register(new DelegateIslandService(
                IslandServiceIds.DownloadMonitor, IslandServiceIds.DisplayName(IslandServiceIds.DownloadMonitor),
                options => SettingsRegistry.GetBool(options, "DownloadMonitorEnabled", true),
                StartDownloadService, StopDownloadService,
                _ => OnDownloadOptionsChanged(),
                autoStart: true));
        }

        // ================= 启动序列用的统一入口 =================

        /// <summary>
        /// 启动序列里的一个模块：拉起服务并打一行「模块 N/8 …」日志。
        /// 失败只记日志、不冒泡（启动期的任何一个模块都不允许拖垮整个程序）。
        /// </summary>
        private bool EnsureService(string id, string label)
        {
            try
            {
                bool ok = _services.EnsureStarted(id);
                IslandLog.Debug("Startup", ok ? label + " 就绪" : label + " 未启动（开关关闭或已降级）");
                return ok;
            }
            catch (Exception ex)
            {
                IslandLog.Error("Startup:" + id, ex);
                return false;
            }
        }

        // ================= 歌词服务 =================

        private void StartLyricsService()
        {
            if (_lyrics != null) return;

            _lyrics = new LyricsService { Enabled = _options.LyricsEnabled };
            _lyrics.Configure(_options);
            IslandLog.Debug("Service:lyrics", "歌词服务已装配");
        }

        private void StopLyricsService()
        {
            _lyricsCts?.Cancel();
            _lyricsCts?.Dispose();
            _lyricsCts = null;

            _lyrics?.Dispose();
            _lyrics = null;
        }

        // ================= 剪切板与热键 =================

        private void StartClipboardService()
        {
            // EnsureClipboardInitialized 自己做幂等（_clipboard != null 直接返回）并把异常收在内部，
            // 这里只把「没起来」翻译成宿主能看懂的降级原因。
            if (!EnsureClipboardInitialized())
            {
                throw new InvalidOperationException("剪切板/热键初始化失败（详见日志）");
            }
        }

        private void StopClipboardService()
        {
            _clipboard?.Dispose();
            _clipboard = null;
            _clipboardLocked = false;
            _clipboardPastedUntil = DateTime.MinValue;
            if (_layers.IsPinned("clipboard")) _layers.SetPinned("clipboard", false);
            _layers.Hide("clipboard");

            _hotkey?.Dispose();
            _hotkey = null;
        }

        // ================= 文件中转站 =================

        private void StartFileTransferService()
        {
            if (_fileTransfer != null) return;

            var transfer = new FileTransferService(Dispatcher);
            transfer.ItemsChanged += OnFileTransferItemsChanged;
            transfer.AllFilesEmptied += OnFileTransferAllFilesEmptied;
            _fileTransfer = transfer;
            TransferItemsList.ItemsSource = transfer.Items;
        }

        private void StopFileTransferService()
        {
            EndIncomingFileDrag(restore: true);
            if (_fileTransfer == null) return;

            _fileTransfer.ItemsChanged -= OnFileTransferItemsChanged;
            _fileTransfer.AllFilesEmptied -= OnFileTransferAllFilesEmptied;
            _fileTransfer = null;
            TransferItemsList.ItemsSource = null;
        }

        // ================= 系统媒体（SMTC） =================
        // 注意：InitializeAsync 的等待留在启动序列里（那里才是唯一关心「首帧有没有拿到」的地方），
        // 这里只负责实例与订阅 —— 必须先订阅再初始化，否则会把开机时正在播的那首歌整个漏掉。

        private void StartSmtcService()
        {
            if (_smtc != null) return;

            var smtc = new SmtcService(Dispatcher);
            smtc.NowPlayingChanged += OnNowPlayingChanged;
            smtc.Failed += (_, msg) => IslandLog.Debug("SMTC", msg);
            _smtc = smtc;

            // 调试钩子可能先于服务创建（ALPHA_ISLAND_FAKE_MEDIA）：服务一到手就把合成曲目灌进去。
            ApplyFakeMediaForDebug();
        }

        private void StopSmtcService()
        {
            _smtc?.Dispose();
            _smtc = null;
        }

        // ================= WASAPI 频谱 =================

        private void StartSpectrumService()
        {
            if (_audio != null) return;

            var audio = new AudioSpectrumService { Sensitivity = (float)_options.Sensitivity,
                VisualizationEnabled = (PillSpectrum.IsActive && PillSpectrum.IsVisible) || (CardWave.IsActive && CardWave.IsVisible) };
            audio.FrameReady += OnSpectrumUpdated;
            _audio = audio;
            _audioDeviceSuppressUntil = DateTime.UtcNow.AddSeconds(3);

            // Start() 内部自建采集线程，但仍然放到后台，避免任何意外的 COM 绑定占住 UI 线程。
            StartInBackground(audio.Start, "Audio");
        }

        private void StopSpectrumService()
        {
            var audio = _audio;
            _audio = null;
            if (audio == null) return;
            audio.FrameReady -= OnSpectrumUpdated;
            _ = Task.Run(audio.Dispose);
        }

        // ================= 系统音量 =================
        // 先订阅并赋值、再把 Start() 丢后台：Start() 里的 COM 端点绑定会阻塞调用线程，
        // 而赋值必须抢在任何并发路径看到 == null 之前完成（否则会多出一个永不注销
        // RegisterEndpointNotificationCallback 的僵尸实例）。

        private void StartVolumeService()
        {
            if (_volumeService != null) return;

            var volume = new VolumeService(Dispatcher);
            volume.VolumeChanged += OnVolumeChanged;
            volume.DeviceChanged += OnDeviceChanged;
            volume.ListsChanged += OnVolumeListsChanged;
            _volumeService = volume;
            StartInBackground(volume.Start, "Volume");
        }

        private void StopVolumeService()
        {
            StopVolumeFlyoutTakeover();
            if (_volumeService == null) return;

            _volumeService.VolumeChanged -= OnVolumeChanged;
            _volumeService.DeviceChanged -= OnDeviceChanged;
            _volumeService.ListsChanged -= OnVolumeListsChanged;
            _volumeService.Dispose();
            _volumeService = null;
        }

        // ================= 电池 =================

        private void StartBatteryService()
        {
            if (_battery != null) return;

            var battery = new BatteryService(Dispatcher);
            battery.BatteryStatusChanged += OnBatteryStatusChanged;
            _battery = battery;
            StartInBackground(battery.Start, "Battery");

            // 功率采样跟着电池服务一起活（ticker 5s 一拍）
            StartPowerSampler();
        }

        private void StopBatteryService()
        {
            StopPowerSampler();
            _battery?.Dispose();
            _battery = null;
        }

        // ================= 蓝牙 =================
        // 三个事件一次订阅齐：早期 ApplyOptions 路径只订阅「已连接 + 电量」，靠模块 8 再补「断开」，
        // 拆成两条路径纯粹是历史遗留 —— 合并成一条不会有行为差异，但少一个「谁先谁后」的坑。

        private void StartBluetoothService()
        {
            if (_bluetooth != null) return;

            var bluetooth = new BluetoothService(Dispatcher);
            bluetooth.DeviceConnected += OnBluetoothDeviceConnected;
            bluetooth.DeviceDisconnected += OnBluetoothDeviceDisconnected;
            bluetooth.BatteryUpdated += OnBluetoothBatteryUpdated;
            _bluetooth = bluetooth;
            StartInBackground(bluetooth.Start, "Bluetooth");
        }

        private void StopBluetoothService()
        {
            _bluetooth?.Dispose();
            _bluetooth = null;
        }
    }
}
