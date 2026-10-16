using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void PowerIslandLifecycleChecks(alpha_island.MainWindow window, Grid root)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        var pages = AgentField<IslandPageHost>(window, "_pages");
        var oldMedia = AgentField<SmtcService?>(window, "_smtc");
        using var media = new SmtcService(window.Dispatcher);
        var failures = new List<string>();
        void Expect(bool condition, string message) { if (condition) Check(true, message); else failures.Add(message); }
        void FinishAlert()
        {
            window.BatteryRipple.Stop();
            AgentField<System.Windows.Threading.DispatcherTimer?>(window, "_batteryAnimationTimer")?.Stop();
            SetAgentField(window, "_batteryAnimationTimer", null);
            layers.ShowUntil("battery", DateTime.UtcNow.AddSeconds(-1)); layers.ExpireIfStale("battery");
            PrivateCall(window, "SyncNotificationLayers"); PrivateCall(window, "TryMorphPillWidth"); PumpAnimations(100); root.UpdateLayout();
        }
        try
        {
            options.BatteryNotifyEnabled = true;
            media.SetDebugNowPlaying(new NowPlayingInfo { Title = "电源提示后保留音乐", State = PlaybackState.Playing });
            SetAgentField(window, "_smtc", media); PrivateCall(window, "SetMusicPinned", true);
            var charging = new BatteryStatusInfo(80, true, BatteryChargeType.StandardAC, BatteryPowerEvent.PluggedIn, null, IsOnAcPower: true);
            PrivateCall(window, "OnBatteryStatusChanged", null, charging); PumpAnimations(100);
            var satellites = AgentField<List<string>>(window, "_activeSatelliteOrder");
            Expect(layers.IsActive("battery") && !satellites.Contains("charging"), "充电动画播放时不另外显示重复的电量小岛");
            PrivateCall(window, "TogglePin");
            Expect(layers.IsPinned("charging") && !layers.IsPinned("battery"), "展开提示的右键置顶作用于电量小岛，不把动画提示永久钉住");
            FinishAlert();
            Expect(layers.IsActive("charging") && AgentField<bool>(window, "_isPinned"), "展开提示结束后保留电量小岛和原来的音乐置顶");
            var battery = charging with { Percent = 79, IsCharging = false, ChargeType = BatteryChargeType.Unknown,
                EventType = BatteryPowerEvent.Unplugged, IsOnAcPower = false };
            PrivateCall(window, "OnBatteryStatusChanged", null, battery); PumpAnimations(80); FinishAlert();
            Expect(layers.IsActive("charging") && layers.IsPinned("charging") && window.ChargingPrimary.PercentText.Text == "79%",
                "拔出电源后保留同一个小岛、百分比及置顶状态");
            Expect(window.ChargingPrimary.StateText.Text == "电池供电", "放电的小岛不再误标为已连接电源");
            var orbs = AgentField<Dictionary<string, Border>>(window, "_satelliteOrbViews");
            PrivateCall(window, "SwitchMainIsland", "music"); PrivateCall(window, "SyncSatelliteOrbs"); PumpAnimations(80);
            if (orbs.TryGetValue("charging", out var orb))
            {
                orb.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
                PumpAnimations(80); root.UpdateLayout();
            }
            Expect(AgentField<IslandState>(window, "_state") == IslandState.Card && pages.ActiveId == IslandPageIds.System,
                "点击电量卫星直接进入系统状态页");
            var summary = window.FindName("SystemBatterySummary") as BatteryChargePill;
            Expect(summary?.PercentText.Text == "79%" && summary.StateText.Text == "电池供电", "系统状态顶部显示同一份实时电量和供电状态");
            PrivateCall(window, "UpdateChargingIsland", battery with { Percent = 78, EventType = BatteryPowerEvent.None });
            Expect(summary?.PercentText.Text == "78%" && AgentField<IslandState>(window, "_state") == IslandState.Card,
                "电量继续刷新时同步页首数值，不抢走已经打开的卡片");
            string output = System.IO.Path.GetFullPath("bin/power-island-previews"); System.IO.Directory.CreateDirectory(output);
            SaveNetworkBitmap(RenderNetworkRegion(root, window.IslandBody), System.IO.Path.Combine(output, "system-battery.png"));
            PrivateCall(window, "UpdateChargingIsland", battery with { HasBattery = false, Percent = -1 });
            Expect(!layers.IsActive("charging"), "无电池设备不保留虚假的电量小岛");
            PrivateCall(window, "SetMusicPinned", false); SetAgentField(window, "_smtc", null);
            PrivateCall(window, "SetState", IslandState.Bar, true);
            layers.ShowSeconds(IslandLayerHost.GreetingKey, 2);
            PrivateCall(window, "OnBatteryStatusChanged", null, charging); PumpAnimations(100);
            Expect(AgentField<bool>(window, "_powerTransitionPending") && layers.IsActive("battery"), "充电事件接替欢迎动画时，仍保留到小岛的交接阶段");
            FinishAlert();
            Expect(AgentField<IslandState>(window, "_state") == IslandState.Bar && !layers.IsActive("charging"),
                "没有音乐且未手动置顶时，普通充电提示结束后收为细条，不留下电量小岛");
            foreach (var state in new[] { SmartChargingState.Unknown, SmartChargingState.NotActive, SmartChargingState.Active })
            {
                PrivateCall(window, "UpdateChargingIsland", charging with { Percent = 76, EventType = BatteryPowerEvent.None, SmartCharging = state });
                Expect(!layers.IsActive("charging") && window.SystemBatterySummary.PercentText.Text == "76%",
                    "普通后台刷新只更新系统卡片，智能充电状态 " + state + " 不自动常驻");
            }
            PrivateCall(window, "SelectComponent", IslandPageIds.System); PumpAnimations(80); root.UpdateLayout();
            window.SystemBatterySummary.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                { RoutedEvent = UIElement.PreviewMouseUpEvent });
            Expect(layers.IsPinned("charging") && layers.IsActive("charging") && pages.ActiveId == IslandPageIds.System,
                "普通充电结束后仍可通过系统卡片右键手动置顶电量岛");
            PrivateCall(window, "SwitchMainIsland", "charging"); PrivateCall(window, "SetState", IslandState.Pill, true); PumpAnimations(80);
            SaveNetworkBitmap(RenderNetworkRegion(root, window.IslandBody), System.IO.Path.Combine(output, "compact-battery.png"));
            PrivateCall(window, "IslandBody_MouseLeftButtonDown", window.IslandBody,
                new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            Expect(AgentField<IslandState>(window, "_state") == IslandState.Card && pages.ActiveId == IslandPageIds.System,
                "主位置的电量小岛也能直接进入系统状态页");
            var beforePreview = AgentField<BatteryStatusInfo>(window, "_chargingStatus");
            PrivateCall(window, "PreviewBatteryAnimation", BatteryPowerEvent.Unplugged); PumpAnimations(80); FinishAlert();
            Expect(layers.IsActive("charging-preview") && ReferenceEquals(beforePreview, AgentField<BatteryStatusInfo>(window, "_chargingStatus")),
                "充放电动画测试也交接为临时小岛，并保留真实电池采样");
            if (failures.Count > 0) throw new Exception("FAIL: " + string.Join("；", failures));
        }
        finally
        {
            layers.SetPinned("battery", false); layers.SetPinned("charging", false); layers.HideAll();
            PrivateCall(window, "SetMusicPinned", false); SetAgentField(window, "_smtc", oldMedia);
        }
    }
}
