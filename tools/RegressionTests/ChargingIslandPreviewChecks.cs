using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;

internal static partial class Program
{
    private static void ChargingIslandPreviewChecks(alpha_island.MainWindow window, Grid root)
    {
        const string preview = "charging-preview";
        var options = AgentField<IslandOptions>(window, "_options");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        var oldMedia = AgentField<SmtcService?>(window, "_smtc");
        var real = new BatteryStatusInfo(43, false, BatteryChargeType.Unknown, BatteryPowerEvent.None, null, IsOnAcPower: false);
        using var media = new SmtcService(window.Dispatcher);
        var settings = new alpha_island.Settings.SettingsWindow(false);
        var output = Path.GetFullPath("bin/charging-island-preview"); Directory.CreateDirectory(output);
        bool RightClick(UIElement target, RoutedEvent? routedEvent = null)
        {
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                { RoutedEvent = routedEvent ?? UIElement.PreviewMouseRightButtonUpEvent };
            target.RaiseEvent(args);
            return args.Handled;
        }
        void Expire() { layers.ShowUntil(preview, DateTime.UtcNow.AddSeconds(-1)); layers.ExpireIfStale(preview); PrivateCall(window, "SyncNotificationLayers"); PumpAnimations(150); }
        try
        {
            PrivateCall(window, "UpdateChargingIsland", real); layers.HideAll();
            options.BatteryNotifyEnabled = false; settings.LoadFrom(options);
            settings.ChargingIslandPreviewRequested += (_, _) => PrivateCall(window, "PreviewChargingIsland");
            int changed = 0; settings.OptionsChanged += (_, _) => changed++;
            var button = (Button)settings.FindName("PreviewChargingIslandButton");
            Check(button != null && button.IsEnabled && button.Content.ToString() == "测试充电小岛", "设置提供独立充电小岛测试按钮，关闭日常通知仍可用");
            string before = JsonSerializer.Serialize(options);
            PrivateCall(window, "SetState", IslandState.Bar, true);
            button!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpAnimations(200); root.UpdateLayout();
            Check(layers.IsActive(preview) && !layers.IsActive("charging") && window.ChargingPreviewPrimary.PercentText.Text == "80%"
                && window.ChargingPreviewPrimary.StateText.Text == "测试预览" && Math.Abs(window.IslandBody.Width - 148) < 1,
                "点击按钮显示独立的80%环形电量预览，主岛保持小尺寸并明确标记测试");
            var claim = layers.Peek(preview);
            Check(claim?.Lifetime == NotificationLifetime.Timed && claim.Value.UntilUtc > DateTime.UtcNow.AddSeconds(6)
                && claim.Value.UntilUtc <= DateTime.UtcNow.AddSeconds(8), "预览使用既有通知时钟，8秒后自动结束");
            Check(ReferenceEquals(AgentField<BatteryStatusInfo>(window, "_chargingStatus"), real)
                && JsonSerializer.Serialize(options) == before && changed == 0, "测试不伪造真实电源状态或修改设置");
            bool primaryHandled = RightClick(window.IslandBody);
            Check(primaryHandled && layers.IsPinned(preview) && window.ChargingPreviewPrimary.StateText.Text == "测试已置顶",
                "主充电预览右键直接置顶，立即更新标签");
            bool primaryCancelHandled = RightClick(window.IslandBody);
            Check(primaryCancelHandled && !layers.IsPinned(preview) && !layers.IsActive(preview), "主充电预览再次右键直接取消并收回");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpAnimations(150); root.UpdateLayout();
            SaveNetworkBitmap(RenderNetworkRegion(root, window.IslandBody), Path.Combine(output, "primary.png"));
            Expire();
            Check(!layers.IsActive(preview) && !layers.IsActive("charging"), "未连接电源时预览到期收回，不留下充电小岛");

            media.SetDebugNowPlaying(new NowPlayingInfo { Title = "预览后恢复音乐", State = PlaybackState.Playing });
            SetAgentField(window, "_smtc", media); PrivateCall(window, "SetMusicPinned", true);
            PrivateCall(window, "SwitchMainIsland", "music"); PrivateCall(window, "SetState", IslandState.Pill, true);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpAnimations(200);
            PrivateCall(window, "SwitchMainIsland", "music"); PumpAnimations(200); root.UpdateLayout();
            var orbs = AgentField<Dictionary<string, Border>>(window, "_satelliteOrbViews");
            var orb = orbs[preview]; var pill = ((Grid)orb.Child).Children.OfType<BatteryChargePill>().Single();
            var ring = (FrameworkElement)pill.Children[0];
            Check(orb.Width == 100 && pill.PercentText.Text == "80%" && pill.StateText.Visibility == Visibility.Collapsed
                && pill.PercentText.TranslatePoint(new Point(), pill).X > ring.ActualWidth, "切回音乐后可看到左侧缩小态，圆环与右侧百分比完整显示");
            SaveThemePreview(orb, Path.Combine(output, "satellite.png"));
            var at = ring.TranslatePoint(new Point(), orb);
            var end = pill.PercentText.TranslatePoint(new Point(pill.PercentText.ActualWidth, 0), orb);
            Check(Math.Abs(at.X - (orb.ActualWidth - end.X)) < 1.5, "缩小态图标与数字作为一组居中，左右留白对称");
            bool satelliteHandled = RightClick(orb);
            Check(satelliteHandled && layers.IsPinned(preview) && orb.ContextMenu == null, "缩小态右键直接置顶，无额外菜单");
            layers.ShowUntil(preview, DateTime.UtcNow.AddSeconds(-1)); layers.ExpireIfStale(preview);
            Check(layers.IsPinned(preview) && layers.IsActive(preview), "测试小岛置顶后超过8秒仍保持显示");
            bool satelliteCancelHandled = RightClick(orb);
            Check(satelliteCancelHandled && !layers.IsPinned(preview), "缩小态再次右键直接取消置顶");
            Check(!layers.IsActive(preview) && AgentField<bool>(window, "_isPinned"), "取消测试置顶会收回模拟小岛，保留音乐置顶");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PrivateCall(window, "SwitchMainIsland", "music"); PumpAnimations(100);
            PrivateCall(window, "UpdateCompactIslandContent", preview);
            PrivateCall(window, "UpdateChargingIsland", real with { Percent = 42 });
            Check(AgentField<BatteryChargePill>(window, "_sideChargingView").PercentText.Text == "80%"
                && pill.PercentText.Text == "80%", "真实采样不会覆盖临时预览的百分比");
            PrivateCall(window, "SwitchMainIsland", preview); PumpAnimations(100); Expire();
            Check(AgentField<string>(window, "_activeMainIslandKey") == "music" && AgentField<bool>(window, "_isPinned"),
                "到期恢复原音乐主岛，音乐置顶仍然保留");
            var plugged = real with { Percent = 63, IsCharging = true, IsOnAcPower = true };
            PrivateCall(window, "UpdateChargingIsland", plugged); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Expire();
            Check(!layers.IsActive("charging") && window.ChargingPrimary.PercentText.Text == "63%", "已连接电源时测试到期只保留真实采样，不额外弹出电量岛");
            PrivateCall(window, "SelectComponent", IslandPageIds.System); PumpAnimations(80);
            bool realHandled = RightClick(window.SystemBatterySummary, UIElement.PreviewMouseUpEvent);
            Check(realHandled && layers.IsPinned("charging") && layers.IsActive("charging"), "系统状态电池图标右键直接置顶真实电量岛");
            PrivateCall(window, "SwitchMainIsland", "music"); PrivateCall(window, "SetState", IslandState.Pill, true);
            PrivateCall(window, "SyncSatelliteOrbs"); PumpAnimations(100);
            bool realCancelHandled = RightClick(orbs["charging"]);
            Check(realCancelHandled && !layers.IsPinned("charging") && AgentField<bool>(window, "_isPinned"), "真实充电小岛再次右键独立取消，保留音乐置顶");

            var settingsRoot = (Grid)settings.Content; settings.Content = null; settingsRoot.Resources = settings.Resources;
            using var source = new HwndSource(new HwndSourceParameters("Charging preview settings")
            { Width = (int)settings.MinWidth, Height = 640, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
            source.RootVisual = settingsRoot; PrivateCall(settings, "SelectPageByTag", "system"); settingsRoot.UpdateLayout();
            var row = (Grid)settings.FindName("ChargingIslandPreviewRow"); row.BringIntoView(); PumpAnimations(100); settingsRoot.UpdateLayout();
            Check(row.ActualHeight >= 50 && button.ActualWidth >= 100
                && button.TranslatePoint(new Point(button.ActualWidth, 0), row).X <= row.ActualWidth + 1, "最小设置宽度下测试按钮完整显示且文字不被遮挡");
            SaveThemePreview(settingsRoot, Path.Combine(output, "settings.png"));
        }
        finally
        {
            layers.HideAll(); PrivateCall(window, "SetMusicPinned", false); SetAgentField(window, "_smtc", oldMedia);
            PrivateCall(window, "UpdateChargingIsland", real); settings.ForceClose = true; settings.Close();
        }
    }
}
