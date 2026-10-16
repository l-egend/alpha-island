using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AlphaIsland.PluginSdk;
using alpha_island.Core;
using alpha_island.Controls;
using alpha_island.Services;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Color = System.Windows.Media.Color;

internal static partial class Program
{
    private static void NetworkMenuAndCurveChecks(alpha_island.MainWindow window, Grid root)
    {
        var oldNetwork = AgentField<NetworkSpeedService?>(window, "_netSpeed");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        using var network = new NetworkSpeedService();
        const double mb = 1024 * 1024;
        var output = Path.GetFullPath("bin/network-menu-previews"); Directory.CreateDirectory(output);
        var down = AgentField<List<double>>(window, "_netSpeedDownHistory");
        var up = AgentField<List<double>>(window, "_netSpeedUpHistory");
        try
        {
            layers.HideAll(); SetAgentField(window, "_netSpeed", network);
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.NetSpeed);
            PrivateCall(window, "ClearNetSpeedHistory");
            var samples = Enumerable.Range(0, 70).Select(i => new NetworkSpeedSample(
                (1 + 5 * Math.Pow(Math.Sin(i * .15), 2)) * mb, (.4 + 2.2 * Math.Pow(Math.Cos(i * .2), 2)) * mb)).ToArray();
            foreach (var sample in samples) PrivateCall(window, "UpdateNetSpeedSample", sample);
            var last = new NetworkSpeedSample(mb, 3 * mb);
            PrivateCall(window, "UpdateNetSpeedSample", last); root.UpdateLayout();
            Check(down.Count == 60 && up.Count == 60 && down[^1] == mb && up[^1] == 3 * mb,
                "上传下载按同一拍独立记录，仅保留最近60个样本，不合并字节数");
            Check(window.NetSpeedSparkline.Values.SequenceEqual(down) && window.NetSpeedUpSparkline.Values.SequenceEqual(up),
                "卡片分别绑定真实下载与上传历史");
            Check(Math.Abs(window.NetSpeedSparkline.FixedMax - window.NetSpeedUpSparkline.FixedMax) < .001
                && window.NetSpeedSparkline.FixedMax > Math.Max(down.Max(), up.Max()), "两条线共用同一刻度，并为峰值保留顶空");
            Check(((SolidColorBrush)window.NetSpeedSparkline.Stroke!).Color == Color.FromRgb(255, 116, 93)
                && ((SolidColorBrush)window.NetSpeedUpSparkline.Stroke!).Color == Color.FromRgb(61, 156, 245), "下载橙色、上传蓝色，和文字及箭头对应");
            Check(window.NetSpeedCardPeakText.Text.Contains(NetworkSpeedService.FormatSpeedFull(down.Max()))
                && window.NetSpeedCardUpPeakText.Text.Contains(NetworkSpeedService.FormatSpeedFull(up.Max())), "图例分别显示上传下载及各自峰值");
            var graph = (Grid)VisualTreeHelper.GetParent(window.NetSpeedSparkline);
            var frame = RenderNetworkRegion(root, graph);
            var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4]; frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);
            int blue = 0, orange = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i] > 130 && pixels[i] - pixels[i + 2] > 80) blue++;
                if (pixels[i + 2] > 150 && pixels[i + 2] - pixels[i] > 90) orange++;
            }
            Check(blue > 80 && orange > 80, "实际渲染中同时可见蓝色上传线和橙色下载线");
            SaveNetworkBitmap(RenderNetworkRegion(root, window.IslandBody), Path.Combine(output, "two-curves-card.png"));

            PrivateCall(window, "SetLayerPinned", "netspeed", true);
            PrivateCall(window, "SwitchMainIsland", "netspeed"); PrivateCall(window, "SetState", IslandState.Pill, true);
            Check((string)PrivateCall(window, "CurrentComponentId")! == IslandPageIds.NetSpeed, "网速主岛的切换菜单高亮网速页，不再错误指向系统页");
            PrivateCall(window, "OpenComponentMenu", false);
            var menu = AgentField<ComponentRadialMenu>(window, "_componentMenu");
            menu.Measure(new Size(menu.Width, menu.Height)); menu.Arrange(new Rect(0, 0, menu.Width, menu.Height)); menu.UpdateLayout();
            var circle = menu.Children.OfType<Border>().Single(x => (string?)x.ToolTip == "实时网速");
            var icon = (GlyphIcon)circle.Child;
            Check(icon.Text == GlyphArtwork.NetworkTransferGlyph && icon.Text != "\uE80F" && icon.ActualWidth >= 20,
                "右侧实际切换按钮使用重绘云朵与上下箭头，替换默认房子");
            string svg = GlyphArtwork.ExportSvg(icon.Text)!;
            Check(svg.Contains("viewBox=\"0 0 32 32\"") && !svg.Contains("<text"), "菜单图标为可缩放的原生矢量，使用主题颜色，无字体依赖");
            File.WriteAllText(Path.Combine(output, "network-transfer.svg"), svg);
            SaveThemePreview(menu, Path.Combine(output, "right-switch-menu.png"));
            SaveThemePreview(circle, Path.Combine(output, "network-switch-icon.png"));
            var center = menu.Layout.Centers[menu.ItemIds.ToList().IndexOf(IslandPageIds.NetSpeed)];
            Check(menu.ItemAt(center) == IslandPageIds.NetSpeed && menu.CaptionText == "实时网速", "替换图标保留原菜单命中与名称提示");
            PrivateCall(window, "UpdateComponentHover", center, true);
            Check(window.IsNetSpeedCardVisible, "点击重绘图标直接进入独立网速卡片");
            PrivateCall(window, "SetState", IslandState.Pill, true); PrivateCall(window, "StepComponent", 1);
            Check(window.IsSystemCardVisible, "从网速小岛向后切换正确进入下一张系统状态卡片");
            PrivateCall(window, "ClearNetSpeedHistory");
            Check(down.Count == 0 && up.Count == 0 && window.NetSpeedSparkline.Values.Count == 0 && window.NetSpeedUpSparkline.Values.Count == 0,
                "重置采样来源时两个历史与图表一起清空，不串入旧曲线");
        }
        finally
        {
            PrivateCall(window, "CloseComponentSwitcher"); layers.HideAll(); SetAgentField(window, "_netSpeed", oldNetwork);
        }
    }
    private static CroppedBitmap RenderNetworkRegion(Grid root, FrameworkElement view)
    {
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(26, 33, 54)), null, new Rect(root.RenderSize));
        bitmap.Render(backdrop); bitmap.Render(root);
        var origin = view.TransformToAncestor(root).Transform(new Point());
        return new CroppedBitmap(bitmap, new Int32Rect((int)Math.Round(origin.X), (int)Math.Round(origin.Y),
            (int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight)));
    }
    private static void SaveNetworkBitmap(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(path); encoder.Save(output);
    }
}
