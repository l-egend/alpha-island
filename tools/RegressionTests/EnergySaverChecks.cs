using System.IO;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void EnergySaverUiChecks(alpha_island.MainWindow window, Grid root)
    {
        using var service = new BatteryService(window.Dispatcher);
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        var pages = AgentField<IslandPageHost>(window, "_pages");
        var updates = new List<BatteryStatusInfo>();
        string output = Path.GetFullPath("bin/energy-saver-previews"); Directory.CreateDirectory(output);
        service.BatteryStatusChanged += (_, info) =>
        {
            updates.Add(info);
            PrivateCall(window, "OnBatteryStatusChanged", null, info);
        };
        int YellowPixels(BatteryChargePill pill)
        {
            var ring = (FrameworkElement)pill.Children[0];
            var image = RenderNetworkRegion(root, ring);
            var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
            image.CopyPixels(pixels, image.PixelWidth * 4, 0);
            return Enumerable.Range(0, pixels.Length / 4)
                .Count(i => pixels[i * 4 + 2] > 160 && pixels[i * 4 + 1] > 125 && pixels[i * 4] < 110);
        }
        void Sample(BatteryStatusInfo info)
        {
            PrivateCall(service, "ProcessStatusUpdate", info);
            PumpAnimations(70); root.UpdateLayout();
        }
        try
        {
            var live = service.QueryCurrentStatus();
            Console.WriteLine($"Live Windows snapshot: energy saver={live.IsEnergySaverEnabled}, battery={live.HasBattery}, percent={live.Percent}");
            layers.HideAll();
            var normal = new BatteryStatusInfo(80, false, BatteryChargeType.Unknown, BatteryPowerEvent.None, null,
                IsOnAcPower: false, SampledUtc: DateTime.UtcNow);
            Sample(normal);
            Check(updates.Count == 1 && window.ChargingPrimary.PercentText.Text == "80%" && !layers.IsActive("battery"),
                "启动采样同步真实电量，普通状态刷新不触发充放电提醒");
            PrivateCall(window, "SetLayerPinned", "charging", true);
            PrivateCall(window, "SetState", IslandState.Pill, true);
            PumpAnimations(80); root.UpdateLayout();
            SaveNetworkBitmap(RenderNetworkRegion(root, window.IslandBody), Path.Combine(output, "normal.png"));
            Sample(normal with { IsEnergySaverEnabled = true });
            Check(updates.Count == 2 && YellowPixels(window.ChargingPrimary) > 40
                && window.ChargingPrimary.StateText.Text == "节电模式" && window.ChargingPrimary.PercentText.Text == "80%"
                && !layers.IsActive("battery"), "电量不变时开启节电模式也即时刷新为黄色圆环，无额外提醒动画");
            SaveNetworkBitmap(RenderNetworkRegion(root, window.IslandBody), Path.Combine(output, "energy-saver.png"));
            PrivateCall(window, "SelectComponent", IslandPageIds.System); PumpAnimations(70); root.UpdateLayout();
            var summary = (BatteryChargePill)window.FindName("SystemBatterySummary");
            Check(YellowPixels(summary) > 30 && summary.ToolTip.ToString()!.Contains("节电模式")
                && window.SysCardBatteryDetail.Text.Contains("节电模式"), "系统状态页的电量环和供电描述同步显示节电模式");
            SaveNetworkBitmap(RenderNetworkRegion(root, window.IslandBody), Path.Combine(output, "system-energy-saver.png"));
            Sample(normal);
            Check(YellowPixels(summary) == 0 && summary.StateText.Text == "电池供电" && pages.ActiveId == IslandPageIds.System
                && layers.IsPinned("charging") && !layers.IsActive("battery"), "关闭节电模式恢复供电颜色，保留当前卡片及电量岛置顶");
            Sample(normal with { SampledUtc = DateTime.UtcNow });
            Check(updates.Count == 3, "相同电量和节电状态的兜底采样不重复刷新界面");
            Sample(normal with { HasBattery = false, Percent = -1, IsEnergySaverEnabled = true });
            Check(!layers.IsActive("charging") && summary.PercentText.Text == "—" && summary.StateText.Text == "未检测到电池",
                "无电池时即使系统节电开启也不显示虚假电量岛");
            service.Dispose(); int before = updates.Count;
            Sample(normal with { IsEnergySaverEnabled = true });
            Check(updates.Count == before, "电池服务关闭后不再推送节电状态");
        }
        finally { layers.SetPinned("charging", false); layers.HideAll(); }
    }
}
