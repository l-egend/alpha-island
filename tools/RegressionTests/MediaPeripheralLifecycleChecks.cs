using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AlphaIsland.PluginSdk;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void MediaPeripheralLifecycleChecks(alpha_island.MainWindow window, Grid root)
    {
        var failures = new List<Exception>();
        void Case(string name, Action action)
        {
            try { action(); }
            catch (Exception error) { failures.Add(error); Console.WriteLine("FAIL CASE: " + name + " / " + error.Message); }
        }
        var options = AgentField<IslandOptions>(window, "_options");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        using var media = new SmtcService(window.Dispatcher);
        SetAgentField(window, "_smtc", media);
        SetAgentField(window, "_sessionLocked", false);
        SetAgentField(window, "_gameQuietActive", false);
        SetAgentField(window, "_mediaSourceForeground", false);
        SetAgentField(window, "_isPinned", false);
        SettingsRegistry.Write(options, "MediaWindowHandoffEnabled", false);
        options.UpNextEnabled = options.PlayNotifyEnabled = true;
        PrivateCall(window, "DismissGreeting");
        layers.HideAll();
        MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
        var track = new NowPlayingInfo { Title = "启动前已在播放", Artist = "测试", SourceApp = "fixture-player.exe",
            SessionId = "startup-media", State = PlaybackState.Playing, Duration = TimeSpan.FromMinutes(3), CanToggle = true };
        void Apply(NowPlayingInfo info) { media.SetDebugNowPlaying(info); PrivateCall(window, "ApplyMedia", info); root.UpdateLayout(); }
        Case("startup media", () =>
        {
            PrivateCall(window, "ApplyNoMedia");
            PrivateCall(window, "SetState", IslandState.Bar, true);
            Apply(track);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill && AgentField<bool>(window, "_upNextShowing"),
                "启动首次发现已有播放会话时主动显示媒体提示");
        });
        Case("unpinned media", () =>
        {
            SetAgentField(window, "_musicIslandDismissed", false);
            PrivateCall(window, "SetState", IslandState.Pill, true);
            PrivateCall(window, "DismissUpNext");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "媒体仍在播放时，未置顶提示到期也会自动收回");
        });
        Case("pin feedback", () =>
        {
            PrivateCall(window, "SetState", IslandState.Pill, true);
            PrivateCall(window, "SetMusicPinned", true, false);
            PumpAnimations(420);
            Check(AgentField<bool>(window, "_isPinned") && window.PinBubble.Visibility == Visibility.Visible && window.PinBubble.Opacity > .9,
                "媒体置顶反馈在入场动画结束后仍可见");
            PrivateCall(window, "HidePinBubble", false);
            PrivateCall(window, "ShowPinBubble", true, "媒体播放器");
            PumpAnimations(420);
            Check(window.PinBubble.Visibility == Visibility.Visible && window.PinBubble.Opacity > .9,
                "旧取消动画的完成回调不能隐藏新的置顶反馈");
            PrivateCall(window, "SetMusicPinned", false, false); PumpAnimations(420);
            Check(!AgentField<bool>(window, "_isPinned") && window.PinBubble.Visibility == Visibility.Visible && window.PinBubble.Opacity > .9,
                "取消置顶触发自动收起后，取消反馈仍保持可见");
        });
        Case("charging start", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var policy = new DeviceBatteryReminderPolicy();
            var seed = new DeviceBatterySample("fixture", new PluginDeviceBattery("mouse", "测试接收器鼠标",
                DeviceBatteryTransport.WirelessReceiver, DeviceBatteryKind.Mouse, 61, true, Charging: true, PhysicalId: "charging-mouse"), now);
            var groups = DeviceBatteryCatalog.Group([seed], now);
            Check(policy.Observe(groups, now).Any(a => a.Detail.Contains("充电")),
                "接收器设备开始充电时发出充电提示");
            Check(policy.Observe(groups, now.AddSeconds(5)).Count == 0, "连续充电读数不会重复弹出提示");
        });
        Case("charging bolt", () =>
        {
            var bolt = (Geometry)typeof(DeviceBatteryIndicator).GetField("Bolt", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Check(bolt.Bounds.Height >= 13 && bolt.Bounds.Width >= 8,
                "原尺寸电池中的充电闪电扩大到清晰可见的高度和宽度");
        });
        Case("disconnected headset", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var device = new PluginDeviceBattery("mac:AABBCCDDEE01", "OPPO Enco Clip2", DeviceBatteryTransport.Bluetooth,
                DeviceBatteryKind.Audio, 90, true, PhysicalId: "mac:AABBCCDDEE01");
            var native = new DeviceBatterySample("bluetooth", device with { Connected = false, Percent = null }, now);
            var legacy = new DeviceBatterySample("oppo.pods", device with { Id = "OPPO Enco Clip2/left",
                Name = "OPPO Enco Clip2 · 左耳", PhysicalId = "" }, now.AddSeconds(-5), Expires: false);
            Check(DeviceBatteryCatalog.Group([native, legacy], now).Count == 0,
                "Windows 确认耳机断开后，插件残留的左右耳电量不能将设备重新显示为在线");
            Check(DeviceBatteryCatalog.Group([native with { Device = device, UpdatedAt = now.AddSeconds(-10) }, native], now).Count == 0,
                "同一物理设备的新断连快照优先于旧在线快照");
        });
        Case("connection revalidation", () =>
        {
            using var bluetooth = new BluetoothService(window.Dispatcher);
            const string key = "mac:AABBCCDDEE02";
            var info = new BluetoothDeviceInfo(key, "连接核验耳机", BluetoothDeviceCategory.Audio, 75, true);
            PrivateCall(bluetooth, "StoreBatterySnapshot", key, info);
            PrivateCall(bluetooth, "ApplyConnectionSnapshot", (object)new BluetoothConnectionSnapshot[]
                { new("fixture-aep", "AABBCCDDEE02", info.Name, null) });
            Check(bluetooth.BatterySnapshots.Count == 1, "Windows 未返回连接属性时，不把未知状态误判为断开");
            PrivateCall(bluetooth, "ApplyConnectionSnapshot", (object)new BluetoothConnectionSnapshot[]
                { new("fixture-aep", "AABBCCDDEE02", info.Name, false) });
            Check(bluetooth.BatterySnapshots.Count == 0 && bluetooth.DeviceBatterySamples is [{Device.Connected:false,Device.Percent:null}],
                "重新核实 Windows 断开状态后撤销在线快照和旧电量，并保留断开凭据");
            PrivateCall(bluetooth, "StoreBatterySnapshot", key, info with { BatteryPercent = 70 });
            Check(bluetooth.BatterySnapshots is [{Device.Connected:true,Device.Percent:70}], "重新连接能替换断开凭据并恢复新电量");
        });
        Case("background spectrum transition", () =>
        {
            SetAgentField(window, "_gameQuietActive", false); layers.HideAll();
            PrivateCall(window, "SetMusicPinned", true, true);
            SetAgentField(window, "_upNextShowing", false); SetAgentField(window, "_upNextUntil", DateTime.MinValue);
            PrivateCall(window, "SyncUpNextContent");
            PrivateCall(window, "SetState", IslandState.Pill, true); PumpAnimations(500); root.UpdateLayout();
            var album = System.Windows.Media.Color.FromRgb(96, 56, 128);
            SetAgentField(window, "_spectrumAlbumRamp", new[] { album, album, album, album });
            SetAgentField(window, "_spectrumBackdropBrightness", null);
            PrivateCall(window, "UpdateSpectrumColors", false);
            Check(window.PillSpectrum.IsVisible && window.PillSpectrum.ColorLow == album, "真实媒体频谱可见，初始颜色来自当前专辑");
            PrivateCall(window, "ApplySpectrumBackdrop", .01d);
            var target = CoverPalette.AgainstBackground(album, .01);
            Check(target != album && window.PillSpectrum.HasAnimatedProperties && window.PillSpectrum.ColorLow != target,
                "背景变暗后调整专辑色明度，启动过渡而非立即跳到新色");
            PumpAnimations(160);
            Check(window.PillSpectrum.ColorLow != album && window.PillSpectrum.ColorLow != target, "频谱颜色过渡具有真实中间帧");
            PumpAnimations(500);
            Check(window.PillSpectrum.ColorLow == target && !window.PillSpectrum.HasAnimatedProperties,
                "背景适配最终到达目标颜色并释放动画时钟");
            PrivateCall(window, "ApplySpectrumBackdrop", 1d); PumpAnimations(100);
            var during = window.PillSpectrum.ColorLow;
            PrivateCall(window, "ApplySpectrumBackdrop", .01d);
            // A WPF clock is evaluated on the next composition frame; inspect
            // the rendered handoff rather than its pending pre-frame snapshot.
            PumpAnimations(20);
            var restarted = window.PillSpectrum.ColorLow;
            Console.WriteLine($"Spectrum reversal: {during} -> {restarted}; target={target}; animated={window.PillSpectrum.HasAnimatedProperties}");
            Check(Math.Abs(during.R-restarted.R)+Math.Abs(during.G-restarted.G)+Math.Abs(during.B-restarted.B) < 8,
                "快速切换背景时从当前颜色继续过渡，不回跳到旧起点");
            PumpAnimations(600);
            static double Hue(System.Windows.Media.Color color) => System.Drawing.Color.FromArgb(color.R,color.G,color.B).GetHue();
            Check(Math.Abs(Hue(album)-Hue(window.PillSpectrum.ColorLow)) < 2, "背景适配保留专辑主色相");
            PrivateCall(window, "SetMusicPinned", false, false);
        });
        Case("game collapse", () =>
        {
            SetAgentField(window, "_isPinned", false); layers.HideAll();
            SettingsRegistry.Write(options, "NotificationGameQuietEnabled", true);
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.System); PumpAnimations(500);
            PrivateCall(window, "ApplyGameActivity", new GameActivityState(1, true, "动画回归游戏", "fixture", 1));
            PumpAnimations(500);
            PrivateCall(window, "FinishGameQuietIntro");
            var animator = AgentField<IslandAnimator>(window, "_animator");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar && animator.IsRunning,
                "游戏提示结束后使用正常收起动画，同时立即锁定最小态交互");
            PumpAnimations(1000);
            Check(!animator.IsRunning && Math.Abs(animator.CurrentGeometry().Height - IslandMetrics.BarHeight) < .01,
                "游戏收起动画最终稳定到最小态");
        });
        Case("normal foreground memory", () =>
        {
            var health = new SystemHealthMonitor();
            var now = DateTimeOffset.UtcNow;
            var process = new ProcessHealthSample(12345, 1, "正常前台应用", 1, 100 * 1048576L, 0);
            int alerts = 0;
            for (int i = 0; i <= 20; i++)
                alerts += health.ObserveProcesses([process with { PrivateBytes = (i < 3 ? 100 : 500) * 1048576L }], now.AddSeconds(i * 30), false).Length;
            Check(alerts == 0, "应用恢复前台时单次内存跃升后保持稳定，不误报持续增长");
            health.Reset(); alerts = 0;
            for (int i = 0; i <= 20; i++)
                alerts += health.ObserveProcesses([process with { PrivateBytes = (100 + i * 20) * 1048576L,
                    IsForeground = i >= 14 }], now.AddSeconds(i * 30), false).Length;
            Check(alerts == 0, "切到前台时重新建立应用趋势，避免沿用后台低占用基线");
            health.Reset(); alerts = 0;
            for (int i = 0; i <= 20; i++)
                alerts += health.ObserveProcesses([process with { PrivateBytes = (100 + i * 20) * 1048576L }], now.AddSeconds(i * 30), false).Length;
            Check(alerts == 1, "真正连续增长的后台进程仍能产生一次趋势提醒");
        });
        SetAgentField(window, "_gameQuietActive", false);
        if (failures.Count > 0) throw new AggregateException(failures);
    }
}
