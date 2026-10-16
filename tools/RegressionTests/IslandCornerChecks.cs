using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using alpha_island.Core;
using alpha_island.Services;
using Point = System.Windows.Point;

internal static partial class Program
{
    private static void IslandCornerChecks(alpha_island.MainWindow window, Grid root)
    {
        var oldShadow = window.IslandBody.Effect;
        var oldSurfaceBackground = window.IslandSurface.Background;
        var oldMedia = AgentField<SmtcService?>(window, "_smtc");
        using var media = new SmtcService(window.Dispatcher);
        var output = Path.GetFullPath("bin/island-corner-previews"); Directory.CreateDirectory(output);
        var failures = new List<string>();
        void Capture(string label)
        {
            root.UpdateLayout();
            var full = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32); full.Render(root);
            var origin = window.IslandBody.TransformToAncestor(root).Transform(new Point());
            int width = (int)Math.Round(window.IslandBody.ActualWidth), height = (int)Math.Round(window.IslandBody.ActualHeight);
            var image = new CroppedBitmap(full, new Int32Rect((int)Math.Round(origin.X), (int)Math.Round(origin.Y), width, height));
            var pixels = new byte[width * height * 4]; image.CopyPixels(pixels, width * 4, 0);
            var corners = new[] { ("TL", 2, 2), ("TR", width - 3, 2), ("BL", 2, height - 3), ("BR", width - 3, height - 3) };
            foreach (var (name, x, y) in corners)
            {
                byte alpha = pixels[(y * width + x) * 4 + 3];
                Console.WriteLine($"Corner {label}/{name}: alpha={alpha}");
                if (alpha > 1) failures.Add(label + "/" + name + "=" + alpha);
            }
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(Path.Combine(output, label + ".png")); encoder.Save(stream);
        }
        try
        {
            // Keep the shipping effect enabled: material-only checks miss shader output.
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.System); PumpAnimations(50); Capture("system-card");
            media.SetDebugNowPlaying(new NowPlayingInfo { Title = "圆角检查", Artist = "测试媒体", State = PlaybackState.Playing });
            SetAgentField(window, "_smtc", media);
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.Music); PumpAnimations(100); Capture("music-card");
            var waveOpacity = window.CardWave.Opacity;
            window.CardWave.Opacity = 0; Capture("music-card-without-wave");
            window.CardWave.Opacity = waveOpacity;
            PrivateCall(window, "SetState", IslandState.Pill, true); PumpAnimations(50); Capture("music-pill");
            // Full bleed payloads exercise every corner, including the upper corners
            // that the current music wave does not reach.
            window.IslandSurface.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(48, 20, 180, 220));
            Capture("full-bleed-pill");
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.System); PumpAnimations(50); Capture("full-bleed-card");
            var clip = window.IslandSurface.Clip;
            root.UpdateLayout();
            Check(ReferenceEquals(clip, window.IslandSurface.Clip), "静止岛复用圆角几何，不添加常驻刷新或重复分配");
            Check(clip != null && clip.FillContains(new Point(window.IslandSurface.ActualWidth / 2, window.IslandSurface.ActualHeight / 2)), "圆角裁剪保留岛中央的正常内容");
            window.IslandSurface.Background = oldSurfaceBackground;
            PrivateCall(window, "SetState", IslandState.Pill, true); PumpAnimations(50);
            foreach (double phase in new[] { .3, .6, .85 })
            {
                window.BatteryRipple.Stop();
                SetAgentField(window.BatteryRipple, "_progress", phase);
                window.BatteryRipple.InvalidateVisual();
                PumpAnimations(20); Capture("power-ripple-" + phase.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            }
            window.BatteryRipple.Stop();
            // Radius changes can precede layout updates during the opening spring.
            var radius = window.IslandBody.CornerRadius;
            window.IslandBody.CornerRadius = new CornerRadius(18);
            PumpAnimations(20); Capture("transition-radius");
            window.IslandBody.CornerRadius = radius;
            PrivateCall(window, "SetState", IslandState.Bar, true); PumpAnimations(50);
            var barClip = window.IslandSurface.Clip;
            Check(barClip != null && barClip.FillContains(new Point(.5, .5)) && !barClip.FillContains(new Point(.5, window.IslandSurface.ActualHeight - .5)), "收起的细条保留直顶边，并正确裁切下方圆角");
            Check(failures.Count == 0, "实际岛体的材质和内容在四个圆角外透明；残留：" + string.Join(", ", failures));
            IslandNativeCornerChecks(window, root);
        }
        finally
        {
            window.BatteryRipple.Stop(); window.IslandSurface.Background = oldSurfaceBackground;
            window.IslandBody.Effect = oldShadow; SetAgentField(window, "_smtc", oldMedia);
        }
    }

    private static void IslandNativeCornerChecks(alpha_island.MainWindow window, Grid root)
    {
        var source = (HwndSource)PresentationSource.FromVisual(root);
        var work = SystemParameters.WorkArea;
        var popup = new System.Windows.Window
        {
            Width = 1200, Height = 340, Left = work.Left + (work.Width - 1200) / 2,
            Top = work.Bottom - 364, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = null, Topmost = true, ShowActivated = false, ShowInTaskbar = false
        };
        try
        {
            source.RootVisual = null;
            popup.Content = root;
            popup.Show();
            var handle = new WindowInteropHelper(popup).Handle;
            foreach (var state in new[] { IslandState.Card, IslandState.Pill })
            {
                PrivateCall(window, "SetState", state, true, IslandPageIds.System);
                PumpAnimations(70); root.UpdateLayout(); DwmFlush();
                var body = window.IslandBody;
                bool Hits(double x, double y)
                {
                    var point = body.PointToScreen(new Point(x, y));
                    return WindowFromPoint(new ComponentProbePoint { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) }) == handle;
                }
                Check(!Hits(2, 2) && !Hits(body.ActualWidth - 3, 2) && !Hits(2, body.ActualHeight - 3) && !Hits(body.ActualWidth - 3, body.ActualHeight - 3),
                    $"真实逐像素透明窗口的 {state} 四角均穿透，没有残留的半透明矩形像素");
                Check(Hits(body.ActualWidth / 2, body.ActualHeight / 2), $"真实 {state} 窗口中央仍可点击，未通过隐藏整岛消除边角");
            }
        }
        finally
        {
            popup.Content = null; popup.Close();
            source.RootVisual = root;
        }
    }
}
