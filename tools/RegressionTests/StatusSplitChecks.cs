using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;

internal static partial class Program
{
    private static void StatusSplitUiChecks(alpha_island.MainWindow window, Grid root)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        var pages = AgentField<IslandPageHost>(window, "_pages");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        var oldNetwork = AgentField<NetworkSpeedService?>(window, "_netSpeed");
        var oldMedia = AgentField<SmtcService?>(window, "_smtc");
        var resource = new SystemResourceSnapshot(37, 81, 35, 72.8, 107.8, "已用 26 GB / 32 GB", "CPU 35.0 W · GPU 72.8 W");
        var plugged = new BatteryStatusInfo(80, true, BatteryChargeType.StandardAC, BatteryPowerEvent.None, 25_000, IsOnAcPower: true);
        using var network = new NetworkSpeedService();
        using var media = new SmtcService(window.Dispatcher);
        Directory.CreateDirectory("bin/status-split-previews");
        void View(string id) { PrivateCall(window, "SetState", IslandState.Card, true, id); PumpAnimations(150); root.UpdateLayout(); }
        void Resources() => PrivateCall(window, "UpdateResourceSnapshot", resource);
        void Battery(BatteryStatusInfo value) => PrivateCall(window, "UpdateChargingIsland", value);
        try
        {
            layers.HideAll();
            SetAgentField(window, "_netSpeed", network);
            network.ApplyByteDeltas(4_000_000, 528_000, 2);
            Check(pages.IndexOf(IslandPageIds.NetSpeed) >= 0 && pages.IndexOf(IslandPageIds.System) >= 0
                && pages.Pages.Single(p => p.Id == IslandPageIds.NetSpeed).View == window.NetSpeedCardLayer
                && pages.Pages.Single(p => p.Id == IslandPageIds.System).View == window.SystemCardLayer,
                "网速与系统状态分别注册独立卡片，直接使用各自的视图");
            Check((string)PrivateCall(window, "CardLayerForPage", IslandPageIds.NetSpeed)! == "netspeed"
                && (string)PrivateCall(window, "CardLayerForPage", IslandPageIds.System)! == "status", "两张卡片使用不同的置顶键");
            View(IslandPageIds.NetSpeed);
            for (int i = 0; i < 60; i++) PrivateCall(window, "UpdateNetSpeedSample", new NetworkSpeedSample(50_400 + 1_000_000 * Math.Pow(Math.Sin(i * .15), 2), 264_000));
            PrivateCall(window, "UpdateNetSpeedSample", network.Current);
            Check(window.IsNetSpeedCardVisible && !window.IsSystemCardVisible && window.NetworkCardIcon.Shape == IconShape.Network
                && window.NetworkCardIcon.ActualWidth >= 48, "独立网速卡片右侧使用原生云朵与上下箭头图标");
            Check(window.NetSpeedCardDownText.Text.Contains("MB/s") && window.NetSpeedRates.DisplayedItems.SequenceEqual(new[] { "upload", "download" })
                && !window.NetSpeedRates.ToolTip.ToString()!.Contains("CPU"), "网速卡片与胶囊只显示上下行，详情保留真实单位");
            Check(window.NetSpeedRates.UploadColor.Color == Color.FromRgb(61, 156, 245)
                && window.NetSpeedRates.DownloadColor.Color == Color.FromRgb(255, 116, 93), "上传蓝色、下载珊瑚色，与参考图箭头一致");
            SaveStatusSplitBody(window, root, "network-card");

            View(IslandPageIds.System); Resources(); root.UpdateLayout();
            Check(window.IsSystemCardVisible && !window.IsNetSpeedCardVisible && window.SysCardCpuValue.Text == "37%"
                && window.SysCardMemValue.Text == "81%" && window.SysCardPowerValue.Text == "107.8 W"
                && window.SysCardPowerDetail.Text.Contains("72.8 W"), "系统卡片完整显示同一份 CPU、内存及 CPU＋GPU 总功耗");
            Check(window.ResourceStatusRates.ResourceText.Text.Contains("CPU 37%") && window.ResourceStatusRates.ResourceText.Text.Contains("RAM 81%")
                && window.ResourceStatusRates.PowerText.Text.Contains("107.8 W"), "系统状态小岛与卡片读数一致，CPU/RAM 与总功耗分成两行");
            SaveStatusSplitBody(window, root, "system-card");
            PrivateCall(window, "UpdateResourceSnapshot", resource with { CpuWatts = null, TotalWatts = null, PowerDetail = "CPU 传感器不可用" });
            Check(window.SysCardPowerValue.Text == "— W" && window.ResourceStatusRates.PowerText.Text.EndsWith("— W")
                && window.ResourceStatusRates.ToolTip.ToString()!.Contains("不可用"), "不完整功耗明确显示未知，不用电池放电功率冒充 CPU＋GPU 功耗");
            Resources();

            SettingsRegistry.SetExtra(options, "StatusDisplayOrder", "power,cpu,memory,download,upload");
            SettingsRegistry.Write(options, "StatusShowUpload", false); PrivateCall(window, "OnStatusDisplayOptionsChanged"); Resources();
            Check(window.NetSpeedRates.DisplayedItems.SequenceEqual(new[] { "download" })
                && window.ResourceStatusRates.DisplayedItems.SequenceEqual(new[] { "power", "cpu", "memory" }), "原配置按两类过滤，隐藏上传不会影响资源状态");
            Check(((StackPanel)window.ResourceStatusRates.Children[0]).Children[0] == window.ResourceStatusRates.PowerText, "系统状态排序能把功耗移到第一行");
            PrivateCall(window, "SetLayerPinned", "status", true); PrivateCall(window, "SwitchMainIsland", "status");
            PrivateCall(window, "SetState", IslandState.Pill, true); PumpAnimations(200);
            Resources(); root.UpdateLayout(); SaveStatusSplitBody(window, root, "system-primary");
            SettingsRegistry.Write(options, "StatusShowMemory", false); PrivateCall(window, "OnStatusDisplayOptionsChanged"); Resources(); PumpAnimations(200);
            Check(Math.Abs(window.IslandBody.Width - StatusDisplayOptions.ResourceWidth(options)) < 1, "资源主岛根据当前选择改变宽度");
            SettingsRegistry.Write(options, "StatusShowMemory", true); PrivateCall(window, "OnStatusDisplayOptionsChanged"); Resources();
            PrivateCall(window, "SetLayerPinned", "netspeed", true); PrivateCall(window, "SwitchMainIsland", "netspeed"); PumpAnimations(200);
            Check(layers.IsPinned("status") && layers.IsPinned("netspeed"), "网速与系统状态可以同时独立置顶");
            var orbs = AgentField<Dictionary<string, Border>>(window, "_satelliteOrbViews");
            var statusOrb = orbs["status"];
            var statusView = ((Grid)statusOrb.Child).Children.OfType<SystemStatusPill>().Single();
            Check(statusOrb.Width == StatusDisplayOptions.ResourceWidth(options, compact: true)
                && statusView.DisplayedItems.SequenceEqual(window.ResourceStatusRates.DisplayedItems), "资源卫星使用相同选择与顺序，尺寸独立于网速");
            SaveStatusSplitElement(statusOrb, "system-satellite");
            PrivateCall(window, "UpdateCompactIslandContent", "status");
            Check(AgentField<SystemStatusPill>(window, "_sideStatusView").DisplayedItems.SequenceEqual(statusView.DisplayedItems), "资源侧边气泡同样显示两行数据");
            PrivateCall(window, "SetLayerPinned", "netspeed", false);
            Check(layers.IsPinned("status") && !layers.IsPinned("netspeed"), "取消网速置顶保留系统状态置顶");
            PrivateCall(window, "SetLayerPinned", "status", false);

            layers.HideAll(); PrivateCall(window, "SetState", IslandState.Bar, true);
            Battery(plugged); PumpAnimations(300); root.UpdateLayout();
            Check(!layers.IsActive("charging") && AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "接入电源不自动常驻电量小岛，系统卡片继续显示电量");
            PrivateCall(window, "SetLayerPinned", "charging", true);
            PrivateCall(window, "SetState", IslandState.Pill, true); PumpAnimations(300); root.UpdateLayout();
            Check(layers.IsActive("charging") && AgentField<string>(window, "_activeMainIslandKey") == "charging"
                && AgentField<IslandState>(window, "_state") == IslandState.Pill && window.ChargingPrimary.PercentText.Text == "80%",
                "手动置顶后显示独立电量小岛");
            Check(Math.Abs(window.IslandBody.Width - 148) < 1 && window.ChargingPrimary.StateText.Text == "正在充电", "充电主岛保持小尺寸，电量与充电状态可见");
            SaveStatusSplitBody(window, root, "charging-primary");
            Battery(plugged with { IsCharging = false, Percent = 100 });
            Check(layers.IsActive("charging") && window.ChargingPrimary.PercentText.Text == "100%"
                && window.ChargingPrimary.StateText.Text == "电池已充满", "充满但仍接通电源时保留电量小岛");
            Battery(plugged with { Percent = -1 });
            Check(window.ChargingPrimary.PercentText.Text == "—", "未知电量显示破折号，避免显示负百分比");
            Battery(plugged);
            SetAgentField(window, "_sessionLocked", true); Battery(plugged); layers.ExpireIfStale("charging");
            SetAgentField(window, "_sessionLocked", false); Battery(plugged);
            Check(layers.IsActive("charging"), "锁屏结束后充电状态能恢复，不需要重新插拔电源");

            media.SetDebugNowPlaying(new NowPlayingInfo { Title = "保持播放", State = PlaybackState.Playing });
            SetAgentField(window, "_smtc", media); PrivateCall(window, "SetMusicPinned", true);
            PrivateCall(window, "SwitchMainIsland", "music"); PumpAnimations(250);
            Battery(plugged with { Percent = 79 }); PumpAnimations(250); root.UpdateLayout();
            Check(AgentField<string>(window, "_activeMainIslandKey") == "music" && AgentField<bool>(window, "_isPinned")
                && layers.IsActive("charging"), "电量更新不抢走已置顶的音乐主岛");
            PrivateCall(window, "CheckPillLayerOverlapInvariant", "charging-satellite-regression");
            Check(layers.LastPlan?.AnyVisible == false && AgentField<DateTime>(window, "_pillOverlapSinceUtc") == DateTime.MinValue,
                "充电卫星与音乐主岛并存不会误报中央内容叠层");
            var chargeOrb = orbs["charging"];
            var chargeView = ((Grid)chargeOrb.Child).Children.OfType<BatteryChargePill>().Single();
            var ring = (FrameworkElement)chargeView.Children[0];
            Check(chargeOrb.Width == 100 && chargeView.PercentText.Text == "79%" && chargeView.StateText.Visibility == Visibility.Collapsed
                && chargeView.PercentText.TranslatePoint(new Point(), chargeView).X > ring.ActualWidth,
                "左侧充电卫星使用环形图标，百分比在右侧，不放在下方");
            SaveStatusSplitElement(chargeOrb, "charging-satellite");
            PrivateCall(window, "UpdateCompactIslandContent", "charging");
            Check(AgentField<BatteryChargePill>(window, "_sideChargingView").PercentText.Text == "79%", "充电旁路气泡使用真实电量");
            PrivateCall(window, "PreviewBatteryAnimation", BatteryPowerEvent.Unplugged);
            Check(layers.IsActive("charging") && window.ChargingPrimary.PercentText.Text == "79%", "动画测试不改变真实充电状态和电量");
            window.BatteryRipple.Stop(); layers.Hide("battery");
            layers.SetPinned("charging", true);
            Battery(plugged with { IsCharging = false, IsOnAcPower = false }); PumpAnimations(250);
            Check(layers.IsActive("charging") && layers.IsPinned("charging") && AgentField<bool>(window, "_isPinned"), "拔出电源保留电量小岛及其钉子，音乐置顶不受影响");
            Battery(plugged); Battery(plugged with { HasBattery = false });
            Check(!layers.IsActive("charging"), "无电池设备不显示充电小岛");
            PrivateCall(window, "SetMusicPinned", false); SetAgentField(window, "_smtc", null);
            layers.HideAll(); PrivateCall(window, "SetState", IslandState.Bar, true); Battery(plugged);
            layers.SetPinned("charging", true); layers.SetPinned("charging", false); Battery(plugged);
            Check(!layers.IsActive("charging"), "用户主动收起充电岛后，重复采样不会立即强行重新弹出");
            Battery(plugged with { IsCharging = false, IsOnAcPower = false }); PumpAnimations(250);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar, "独立充电岛拔出电源后直接恢复细条，不遗留空胶囊");
            foreach (string key in StatusDisplayOptions.All) SettingsRegistry.Write(options, StatusDisplayOptions.SettingKey(key), false);
            PrivateCall(window, "OnStatusDisplayOptionsChanged");
            Check(window.NetSpeedRates.DisplayedItems.Length == 0 && window.ResourceStatusRates.DisplayedItems.Length == 0,
                "两类小岛的项目均可分别隐藏");
        }
        finally
        {
            Battery(plugged with { IsCharging = false, IsOnAcPower = false });
            layers.HideAll(); SetAgentField(window, "_netSpeed", oldNetwork); SetAgentField(window, "_smtc", oldMedia);
            foreach (string key in StatusDisplayOptions.All) SettingsRegistry.Write(options, StatusDisplayOptions.SettingKey(key), true);
            SettingsRegistry.SetExtra(options, "StatusDisplayOrder", StatusDisplayOptions.DefaultOrder);
            PrivateCall(window, "OnStatusDisplayOptionsChanged"); PrivateCall(window, "SetState", IslandState.Bar, true);
        }
    }

    private static void SaveStatusSplitBody(alpha_island.MainWindow window, Grid root, string name)
    {
        root.UpdateLayout();
        var full = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(26, 33, 54)), null, new Rect(root.RenderSize));
        full.Render(backdrop); full.Render(root);
        var at = window.IslandBody.TransformToAncestor(root).Transform(new Point());
        var crop = new CroppedBitmap(full, new Int32Rect((int)Math.Round(at.X), (int)Math.Round(at.Y),
            (int)Math.Ceiling(window.IslandBody.ActualWidth), (int)Math.Ceiling(window.IslandBody.ActualHeight)));
        SaveStatusSplitBitmap(crop, name);
    }
    private static void SaveStatusSplitElement(FrameworkElement element, string name)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(26, 33, 54)), new Pen(System.Windows.Media.Brushes.SlateGray, 1),
                new Rect(1, 1, element.ActualWidth, element.ActualHeight), 20, 20);
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(1, 1, element.ActualWidth, element.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth + 2, (int)element.ActualHeight + 2, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        SaveStatusSplitBitmap(bitmap, name);
    }
    private static void SaveStatusSplitBitmap(BitmapSource bitmap, string name)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create("bin/status-split-previews/" + name + ".png"); encoder.Save(output);
    }
}
