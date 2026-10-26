using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Core;
using alpha_island.Controls;
using alpha_island.Services;
using alpha_island.Interop;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

internal static partial class Program
{
    private static double SpectrumLuminance(Color color)
    {
        static double Linear(byte channel) { double value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }

    private static double SpectrumRatio(Color first, Color second)
    {
        double a = SpectrumLuminance(first), b = SpectrumLuminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static void SpectrumVisibilityChecks(alpha_island.MainWindow window, Grid root)
    {
        using var media = new SmtcService(window.Dispatcher);
        SetAgentField(window, "_smtc", media);
        var options = AgentField<IslandOptions>(window, "_options");
        options.LyricsEnabled = false; options.UpNextEnabled = options.PlayNotifyEnabled = false;
        var track = new NowPlayingInfo { Title = "频谱可见性验证", SourceApp = "fixture-player.exe", SessionId = "spectrum-fixture",
            State = PlaybackState.Playing, Duration = TimeSpan.FromMinutes(3) };
        media.SetDebugNowPlaying(track); PrivateCall(window, "ApplyMedia", track);
        AgentField<IslandLayerHost>(window, "_layers").HideAll();
        PrivateCall(window, "SetMusicPinned", true, true);
        PrivateCall(window, "SetState", IslandState.Pill, true); PumpAnimations(450); root.UpdateLayout();
        var spectrum = window.PillSpectrum;
        Check(spectrum.IsVisible && spectrum.ActualWidth > 0, "真实媒体胶囊中的右侧频谱已经显示，排除空白绘制用例");
        var island = new Rect(window.IslandBody.PointToScreen(new Point()), window.IslandBody.PointToScreen(new Point(window.IslandBody.ActualWidth, window.IslandBody.ActualHeight)));
        var probe = (Rect?)PrivateCall(window, "SpectrumSurfaceProbe", island);
        Check(probe.HasValue && island.Contains(probe.Value) && probe.Value.Bottom < spectrum.PointToScreen(new Point()).Y,
            "背景取色位置位于频谱上方空白玻璃内，不读取频谱自身造成反馈");
        media.SetDebugNowPlaying(track with { State = PlaybackState.Paused });
        Check((bool)PrivateCall(window, "get_SpectrumBackdropNeeded")!, "播放器暂停但仍显示频谱时也继续适配背景");
        media.SetDebugNowPlaying(track);
        spectrum.IsActive = false;
        spectrum.BarCount = 5; spectrum.Width = 28; spectrum.Height = 22;
        var cases = new[]
        {
            (Name: "blue-wallpaper", Album: Color.FromRgb(117, 99, 92), Backdrop: Color.FromRgb(75, 104, 135)),
            (Name: "neutral-wallpaper", Album: Color.FromRgb(103, 94, 83), Backdrop: Color.FromRgb(112, 112, 112)),
            (Name: "bright-wallpaper", Album: Color.FromRgb(226, 211, 194), Backdrop: Color.FromRgb(224, 232, 242))
        };
        Directory.CreateDirectory("bin/SpectrumContrast163/previews");
        foreach (var item in cases)
        {
            SetAgentField(window, "_spectrumAlbumRamp", new[] { item.Album, item.Album, item.Album, item.Album });
            SetAgentField(window, "_spectrumBackdropBrightness", SpectrumLuminance(item.Backdrop));
            PrivateCall(window, "UpdateSpectrumColors", false);
            SetAgentField(spectrum, "_display", new float[] { .5f, .7f, .95f, .7f, .5f }); root.UpdateLayout();
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(new SolidColorBrush(item.Backdrop), null, new Rect(0, 0, 48, 42));
                var brush = new VisualBrush(spectrum) { ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(0, 0, 28, 22), Stretch = Stretch.Fill, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
                drawing.DrawRectangle(brush, null, new Rect(10, 10, 28, 22));
            }
            var bitmap = new RenderTargetBitmap(192, 168, 384, 384, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create($"bin/SpectrumContrast163/previews/{item.Name}.png")) encoder.Save(file);
            var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(96, 84, 1, 1), pixel, 4, 0);
            var painted = Color.FromRgb(pixel[2], pixel[1], pixel[0]);
            double contrast = SpectrumRatio(painted, item.Backdrop);
            Check(contrast >= 3.5, $"{item.Name}：真实小型频谱柱与背景的实际对比度至少 3.5（actual={contrast:F2}, album={item.Album}, target={spectrum.ColorLow}, painted={painted}）");
            static IEnumerable<GeometryDrawing> Shapes(Drawing drawing) => drawing is GeometryDrawing shape ? [shape]
                : drawing is DrawingGroup group ? group.Children.SelectMany(Shapes) : [];
            Check(Shapes(VisualTreeHelper.GetDrawing(spectrum)!).All(shape => shape.Pen == null), item.Name + " 增强颜色后仍没有轮廓描边");
        }
        double actualSurface = SpectrumLuminance(cases[0].Backdrop);
        PrivateCall(window, "ApplySpectrumSample", new DesktopLightSampler.Result(GlassLight.Default with { Brightness = .8 }, 1, 4, actualSurface));
        Check(AgentField<double?>(window, "_spectrumBackdropBrightness") == actualSurface,
            "桌面很亮但玻璃较暗时优先采用实际玻璃亮度");
        PrivateCall(window, "ApplySpectrumSample", new DesktopLightSampler.Result(GlassLight.Default with { Brightness = .42 }, 1, 4));
        Check(AgentField<double?>(window, "_spectrumBackdropBrightness") == .42, "玻璃采样不可用时保留桌面采样回退");
        foreach (var album in new[] { Colors.Red, Colors.Blue, Colors.Green, Colors.Cyan, Colors.Magenta, Colors.Yellow,
            Color.FromRgb(117,99,92), Color.FromRgb(96,56,128), Colors.Black, Colors.White })
        {
            var contrast = Enumerable.Range(0, 101).Select(value =>
            {
                double background = value / 100d;
                var color = CoverPalette.AgainstBackground(album, background);
                double foreground = SpectrumLuminance(color);
                return (Math.Max(foreground, background) + .05) / (Math.Min(foreground, background) + .05);
            }).Min();
            Check(contrast >= 3.5, $"专辑色 {album} 覆盖暗至亮的背景均保持至少 3.5 对比度（minimum={contrast:F2}）");
        }
    }
}
