using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;

internal static partial class Program
{
    private static async Task SmartChargingReaderChecks()
    {
        foreach (var (text, expected) in new (string?, SmartChargingState)[]
        {
            ("电源 电池状态: 79% 剩余\r\n全智能充电", SmartChargingState.Active),
            ("电源 电池状态: 60% 剩余\r\n智能充电已打开", SmartChargingState.Active),
            ("Power 80% available (plugged in)\nFully Smart charged", SmartChargingState.Active),
            ("電源 電池狀態: 80%\n智慧充電", SmartChargingState.Active),
            ("电源 电池状态: 80% 剩余\r\n已接通电源，未充电", SmartChargingState.NotActive),
            ("电源 电池状态: 79% 剩余\r\n智能充电已关闭", SmartChargingState.NotActive),
            ("Power Battery status: 80%\nSmart charging is off", SmartChargingState.NotActive),
            ("Battery Flyout (80%) 智能充电", SmartChargingState.Unknown),
            ("80%", SmartChargingState.Unknown), (null, SmartChargingState.Unknown),
        }) Check(WindowsSmartChargingReader.ParsePowerIndicator(text) == expected, "系统电池文字解析: " + (text?.Replace("\r", "").Replace("\n", " · ") ?? "不可读"));

        static async Task WaitFor(Func<bool> ready)
        {
            var timer = Stopwatch.StartNew();
            while (!ready() && timer.ElapsedMilliseconds < 3000) await Task.Delay(10);
            if (!ready()) throw new Exception("Smart charging background query did not complete.");
        }
        DateTime now = new(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int reads = 0;
        var monitor = new SmartChargingMonitor(() =>
        {
            int read = Interlocked.Increment(ref reads);
            entered.Set(); release.Wait();
            return read == 1 ? SmartChargingState.Active : SmartChargingState.NotActive;
        }, () => now);
        try
        {
            var watch = Stopwatch.StartNew();
            Check(monitor.ReadCached() == SmartChargingState.Unknown && watch.ElapsedMilliseconds < 100,
                "首次智能充电读取立即返回未知，不阻塞电池采样");
            await WaitFor(() => entered.IsSet);
            for (int i = 0; i < 20; i++) monitor.ReadCached();
            Check(reads == 1, "系统辅助功能查询挂起时只保留一个后台查询");
            release.Set();
            await WaitFor(() => monitor.ReadCached() == SmartChargingState.Active);
            for (int i = 0; i < 20; i++) monitor.ReadCached();
            Check(reads == 1, "频繁电量采样复用五秒缓存，不反复遍历任务栏");
            entered.Reset(); release.Reset(); now = now.AddSeconds(6);
            monitor.ReadCached(); await WaitFor(() => entered.IsSet);
            now = now.AddSeconds(31);
            Check(monitor.ReadCached() == SmartChargingState.Unknown && reads == 2,
                "任务栏查询挂起超过三十秒后撤销旧状态，不一直显示智能充电");
            release.Set(); await WaitFor(() => monitor.ReadCached() == SmartChargingState.NotActive);
            Check(monitor.ReadCached() == SmartChargingState.NotActive, "系统取消智能充电指示后清除状态");
        }
        finally { release.Set(); }

        using var oldEntered = new ManualResetEventSlim();
        using var oldRelease = new ManualResetEventSlim();
        int generationReads = 0;
        var changing = new SmartChargingMonitor(() =>
        {
            if (Interlocked.Increment(ref generationReads) == 1)
            { oldEntered.Set(); oldRelease.Wait(); return SmartChargingState.Active; }
            return SmartChargingState.NotActive;
        });
        try
        {
            changing.ReadCached(); await WaitFor(() => oldEntered.IsSet);
            changing.Invalidate(); oldRelease.Set();
            bool staleActive = false;
            await WaitFor(() =>
            {
                var result = changing.ReadCached(); staleActive |= result == SmartChargingState.Active;
                return result == SmartChargingState.NotActive;
            });
            Check(!staleActive && generationReads == 2, "电源切换后丢弃先前未完成的结果，再读当前系统状态");
        }
        finally { oldRelease.Set(); }

        int attempts = 0;
        var failing = new SmartChargingMonitor(() => Interlocked.Increment(ref attempts) == 1
            ? SmartChargingState.Active : throw new InvalidOperationException("Explorer unavailable"), () => now);
        failing.ReadCached(); await WaitFor(() => failing.ReadCached() == SmartChargingState.Active);
        now = now.AddSeconds(6); failing.ReadCached();
        await WaitFor(() => attempts == 2 && AgentField<Task<SmartChargingState>?>(failing, "_query")?.IsCompleted == true);
        Check(failing.ReadCached() == SmartChargingState.Active, "Explorer 短暂不可读时保留有效的智能充电指示");
        Check(attempts == 2, "Explorer 读取失败后缓存周期内不重复查询");
        now = now.AddSeconds(31);
        Check(failing.ReadCached() == SmartChargingState.Unknown, "读取持续失败后旧指示按期失效");
    }

    private static void SmartChargingUiChecks(alpha_island.MainWindow window, Grid root)
    {
        using var service = new BatteryService(window.Dispatcher);
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        var updates = new List<BatteryStatusInfo>();
        string output = Path.GetFullPath("bin/smart-charging-previews"); Directory.CreateDirectory(output);
        service.BatteryStatusChanged += (_, info) =>
        { updates.Add(info); PrivateCall(window, "OnBatteryStatusChanged", null, info); };
        void Sample(BatteryStatusInfo info)
        { PrivateCall(service, "ProcessStatusUpdate", info); PumpAnimations(70); root.UpdateLayout(); }
        var normal = new BatteryStatusInfo(80, false, BatteryChargeType.StandardAC, BatteryPowerEvent.None, null,
            IsOnAcPower: true, SampledUtc: DateTime.UtcNow);
        try
        {
            layers.HideAll(); Sample(normal);
            Check(window.ChargingPrimary.StateText.Text == "已连接电源" && !layers.IsActive("charging"), "八十电量且未充电不推断为智能充电，也不自动常驻电量岛");
            var smart = normal with { SmartCharging = SmartChargingState.Active };
            Sample(smart);
            Check(updates.Count == 2 && window.ChargingPrimary.StateText.Text == "智能充电" && !layers.IsActive("battery")
                && !layers.IsActive("charging") && !PrivateValue<bool>(window, "get_ChargingIslandActive")
                && AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "进入智能充电后收回常驻主电量岛，不额外触发充放电动画");
            Sample(smart with { Percent = 79 });
            Check(!layers.IsActive("charging") && AgentField<IslandState>(window, "_state") == IslandState.Bar
                && window.SystemBatterySummary.PercentText.Text == "79%",
                "智能充电期间后台电量刷新只同步系统状态页，不重新展开电量岛");

            DateTime stableNow = DateTime.UtcNow;
            var shellState = SmartChargingState.Active;
            var stableMonitor = new SmartChargingMonitor(() => shellState, () => stableNow);
            void CompleteSample()
            {
                stableMonitor.ReadCached();
                var query = AgentField<Task<SmartChargingState>?>(stableMonitor, "_query");
                if (query != null && !query.Wait(3000)) throw new Exception("Fixture shell query timed out.");
                stableMonitor.ReadCached();
            }
            CompleteSample();
            int unchanged = updates.Count;
            stableNow = stableNow.AddSeconds(6); shellState = SmartChargingState.Unknown; CompleteSample();
            Sample(smart with { Percent = 79, SmartCharging = stableMonitor.ReadCached() });
            Check(updates.Count == unchanged && !layers.IsActive("charging")
                && AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "任务栏读取失败经真实电池发布与岛布局后仍保持隐藏，不重新弹出");
            stableMonitor.Invalidate();
            Sample(smart with { Percent = 79, SmartCharging = stableMonitor.ReadCached() });
            Check(updates.Count == unchanged && !layers.IsActive("charging"),
                "重复电源刷新经电池发布后不启动新的电量岛展示");

            Sample(normal with { SmartCharging = SmartChargingState.NotActive });
            Check(!layers.IsActive("charging") && AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "退出智能充电后仍不自动常驻普通电量岛");
            layers.SetPinned("netspeed", true);
            PrivateCall(window, "SwitchMainIsland", "netspeed"); PumpAnimations(70);
            Check(!AgentField<List<string>>(window, "_activeSatelliteOrder").Contains("charging"),
                "没有手动置顶的普通电量岛不会挤入其他主岛左侧");
            Sample(smart);
            Check(!AgentField<List<string>>(window, "_activeSatelliteOrder").Contains("charging")
                && AgentField<string>(window, "_activeMainIslandKey") == "netspeed" && layers.IsPinned("netspeed"),
                "进入智能充电收回左侧电量岛，保留其他主岛和置顶");
            SaveNetworkBitmap(RenderNetworkRegion(root, window.IslandBody), Path.Combine(output, "smart-primary.png"));
            var compact = new BatteryChargePill(true); compact.SetBattery(smart);
            Check(compact.StateText.Visibility == Visibility.Collapsed && compact.ToolTip.ToString()!.Contains("智能充电")
                && compact.PercentText.Text == "80%", "紧凑电量岛保留百分比并在悬浮提示显示智能充电");
            PrivateCall(window, "SelectComponent", IslandPageIds.System); PumpAnimations(70); root.UpdateLayout();
            var summary = (BatteryChargePill)window.FindName("SystemBatterySummary");
            Check(summary.StateText.Text == "智能充电" && window.SysCardBatteryDetail.Text.Contains("智能充电"),
                "系统状态页的电量环与供电说明使用同一智能充电状态");
            var ring = (FrameworkElement)summary.Children[0];
            var iconCenter = ring.TranslatePoint(new System.Windows.Point(ring.ActualWidth / 2, ring.ActualHeight / 2), root);
            var hit = root.InputHitTest(iconCenter) as UIElement;
            Check(hit != null && hit != window.IslandBody, "系统状态的电池图标区域可命中右键操作");
            var rightClick = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Right)
                { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseUpEvent };
            hit!.RaiseEvent(rightClick); PumpAnimations(70);
            Check(rightClick.Handled && layers.IsPinned("charging") && layers.IsActive("charging") && !layers.IsPinned("status")
                && AgentField<IslandState>(window, "_state") == IslandState.Card,
                "右键系统状态电池图标仅置顶电量岛，不顶置整张状态卡片或切换页面");
            Sample(smart with { Percent = 78 });
            Check(layers.IsActive("charging") && window.ChargingPrimary.PercentText.Text == "78%",
                "手动置顶后智能充电的后台采样继续刷新电量岛");
            rightClick = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Right)
                { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseUpEvent };
            hit.RaiseEvent(rightClick); PumpAnimations(70);
            Check(rightClick.Handled && !layers.IsPinned("charging") && !layers.IsActive("charging")
                && AgentField<IslandState>(window, "_state") == IslandState.Card && window.SysCardBatteryDetail.Text.Contains("智能充电"),
                "再次右键电池图标立即取消置顶并收回电量岛，保留系统状态页");
            SaveNetworkBitmap(RenderNetworkRegion(root, window.IslandBody), Path.Combine(output, "smart-system.png"));
            Sample(smart with { IsEnergySaverEnabled = true });
            Check(summary.StateText.Text == "节电模式" && summary.ToolTip.ToString()!.Contains("智能充电")
                && window.SysCardBatteryDetail.Text.Contains("节电模式"), "智能充电和节电模式可同时显示，保留节电优先的黄色圆环");
            Sample(normal with { SmartCharging = SmartChargingState.NotActive });
            Check(summary.StateText.Text == "已连接电源" && !summary.ToolTip.ToString()!.Contains("智能充电")
                && !window.SysCardBatteryDetail.Text.Contains("智能充电"), "系统移除智能充电指示后所有电量显示同步恢复");
            int before = updates.Count; Sample(normal with { SmartCharging = SmartChargingState.NotActive });
            Check(updates.Count == before, "未改变的智能充电采样不重复更新界面");
            Sample(smart with { HasBattery = false, Percent = -1 });
            Check(summary.PercentText.Text == "—" && !summary.ToolTip.ToString()!.Contains("智能充电"),
                "无电池时忽略任何旧的智能充电状态");
            layers.HideAll();
            PrivateCall(window, "SetState", IslandState.Bar, true);
            AgentField<IslandOptions>(window, "_options").BatteryNotifyEnabled = true;
            PrivateCall(window, "OnBatteryStatusChanged", null, smart with { EventType = BatteryPowerEvent.PluggedIn });
            PumpAnimations(70);
            Check(layers.IsActive("battery") && !layers.IsActive("charging"),
                "智能充电期间仍正常播放接通电源的一次性提醒");
            window.BatteryRipple.Stop();
            AgentField<System.Windows.Threading.DispatcherTimer?>(window, "_batteryAnimationTimer")?.Stop();
            SetAgentField(window, "_batteryAnimationTimer", null);
            layers.ShowUntil("battery", DateTime.UtcNow.AddSeconds(-1)); layers.ExpireIfStale("battery");
            PrivateCall(window, "SyncNotificationLayers"); PrivateCall(window, "TryMorphPillWidth"); PumpAnimations(200);
            Check(!layers.IsActive("charging") && AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "智能充电的一次性动画结束后收为细条，不交接成常驻电量岛");
            PrivateCall(window, "PreviewChargingIsland"); PumpAnimations(100);
            Check(layers.IsActive("charging-preview") && !layers.IsActive("charging"),
                "智能充电期间设置中的电量岛测试按钮仍正常显示临时预览");
        }
        finally { layers.SetPinned("charging", false); layers.HideAll(); }
    }

    private static T PrivateValue<T>(object target, string name, params object?[] args)
        => (T)target.GetType().GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(target, args)!;

    private static async Task SmartChargingLiveCheck()
    {
        var state = await Task.Run(WindowsSmartChargingReader.Read);
        Console.WriteLine("Current Windows smart charging indicator: " + state);
        Check(state != SmartChargingState.Unknown, "当前电脑可读取 Windows 电池控件的智能充电状态");
        using var service = new BatteryService(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        BatteryStatusInfo snapshot = service.QueryCurrentStatus();
        for (int attempt = 0; attempt < 20 && snapshot.SmartCharging == SmartChargingState.Unknown; attempt++)
        { await Task.Delay(100); snapshot = service.QueryCurrentStatus(); }
        Check(snapshot.HasBattery && snapshot.SmartCharging == state, "真实电池服务与 Windows 系统指示一致");
        Console.WriteLine($"All {_checks} live smart charging checks passed.");
    }
}
