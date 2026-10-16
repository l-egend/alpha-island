using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using alpha_island.Core;
using alpha_island.Services;
using Point = System.Windows.Point;
using FontFamily = System.Windows.Media.FontFamily;

internal static partial class Program
{
    // Render the real island with its production font; detached fixtures otherwise lose the Window font.
    private static void BatteryTextLayoutPreviews(alpha_island.MainWindow window, Grid root)
    {
        string folder = Path.GetFullPath("bin/battery-text-layout-previews"); Directory.CreateDirectory(folder);
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        var view = window.ChargingPrimary;
        var status = new BatteryStatusInfo(100, true, BatteryChargeType.StandardAC, BatteryPowerEvent.None, null, IsOnAcPower: true);
        var failures = new List<string>();
        root.UseLayoutRounding = window.UseLayoutRounding;
        try
        {
            layers.HideAll(); PrivateCall(window, "UpdateChargingIsland", status);
            PrivateCall(window, "SwitchMainIsland", "charging"); PrivateCall(window, "SetState", IslandState.Pill, true);
            foreach (var font in new[] { window.FontFamily, new FontFamily("Microsoft YaHei UI") })
            {
                System.Windows.Documents.TextElement.SetFontFamily(view, font);
                foreach (string text in new[] { "已连接电源", "正在充电", "节电模式", "测试已置顶" })
                {
                    view.SetBattery(status with { IsEnergySaverEnabled = text == "节电模式" });
                    view.StateText.Text = text; PumpAnimations(25); root.UpdateLayout();
                    SaveNetworkBitmap(RenderNetworkRegion(root, window.IslandBody), Path.Combine(folder,
                        (font == window.FontFamily ? "production-" : "yahei-") + text + ".png"));
                    FrameworkElement? ancestor = view.StateText;
                    bool clipped = false;
                    while (ancestor != null)
                    {
                        var clip = LayoutInformation.GetLayoutClip(ancestor) ?? ancestor.Clip;
                        var bounds = new Rect(view.StateText.TranslatePoint(new Point(), ancestor), view.StateText.RenderSize);
                        if (clip != null && !clip.Bounds.Contains(bounds))
                        {
                            clipped = true;
                            Console.WriteLine($"CLIPPED {font.Source} {text}: {ancestor.GetType().Name} clip={clip.Bounds}, text={bounds}");
                        }
                        if (ancestor == window.IslandSurface) break;
                        ancestor = VisualTreeHelper.GetParent(ancestor) as FrameworkElement;
                    }
                    Console.WriteLine($"Layout {text}: statusHeight={view.StateText.ActualHeight:0.##}, pillContentHeight={view.ActualHeight:0.##}, clipped={clipped}");
                    if (clipped) failures.Add(font.Source + " " + text);
                }
            }
            if (failures.Count > 0) throw new Exception("Battery text clipped: " + string.Join("; ", failures));
        }
        finally { layers.HideAll(); }
    }
}
