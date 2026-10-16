using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Interop;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;

internal static partial class Program
{
    private static void LiquidGlassLiveSceneChecks(alpha_island.MainWindow window)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
        options.GelAnimation = true; options.LiquidGlassBackdrop = true; options.GlassOpacity = .22;
        var artwork = new DrawingGroup();
        using (var dc = artwork.Open())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(73, 145, 180), Color.FromRgb(160, 125, 192), 25), null, new Rect(0, 0, 700, 460));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(237, 173, 136)), null, new Point(110, 120), 210, 160);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(121, 188, 192)), null, new Point(590, 400), 280, 200);
            for (int i = 0; i < 30; i++) dc.DrawLine(new System.Windows.Media.Pen(new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)), 1), new Point(i * 32 - 200, 0), new Point(i * 32 + 260, 460));
        }
        artwork.ClipGeometry = new RectangleGeometry(new Rect(0, 0, 700, 460));
        artwork.Freeze();
        var background = new Window { Left = 250, Top = 100, Width = 700, Height = 460, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true, Background = new DrawingBrush(artwork) };
        try
        {
            background.Show();
            window.Left = 0; window.Top = 135; window.Width = 1200; window.Height = 390; window.Show();
            PrivateCall(window, "EnsureBackdrop"); PrivateCall(window, "ApplyGlassAppearance");
            PrivateCall(window, "ShowVolumeNotify", 46, false); PumpAnimations(850);
            var backdrop = AgentField<BackdropWindow>(window, "_backdrop");
            Check(backdrop.ContourUpdates > 5, "完整窗口形变实际持续驱动 DWM 矢量裁剪");
            Check(!window.LiquidFrameLoopActive, "完整窗口的胶囊静止后解除玻璃渲染回调");
            int beforeIdle = backdrop.ContourUpdates; PumpAnimations(160);
            Check(backdrop.ContourUpdates == beforeIdle, "静止的真实玻璃由 DWM 更新背景，WPF 不空转提交");
            string folder = Path.GetFullPath("bin/liquid-glass-previews"); Directory.CreateDirectory(folder);
            void Capture(string name)
            {
                NativeMethods.DwmFlush();
                var a = background.PointToScreen(new Point()); var b = background.PointToScreen(new Point(700, 460));
                Console.WriteLine($"Preview {name}: {a.X:F0},{a.Y:F0} / {b.X-a.X:F0}x{b.Y-a.Y:F0}");
                using var bitmap = new System.Drawing.Bitmap((int)Math.Round(b.X - a.X), (int)Math.Round(b.Y - a.Y), System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) graphics.CopyFromScreen((int)a.X, (int)a.Y, 0, 0, bitmap.Size, System.Drawing.CopyPixelOperation.SourceCopy);
                bitmap.Save(Path.Combine(folder, name + ".png"));
            }
            Capture("v103-live-pill");
            var orb = (Border)PrivateCall(window, "EnsureSatelliteOrb", "bluetooth")!;
            orb.Visibility = Visibility.Visible; orb.Opacity = 1;
            var translate = ((TransformGroup)orb.RenderTransform).Children.OfType<TranslateTransform>().Single();
            translate.X = -window.IslandBody.ActualWidth / 2;
            window.UpdateLayout(); PumpAnimations(120); // Warm the newly loaded glyph before starting the test clock.
            int boundsBefore = backdrop.BoundsUpdates;
            translate.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(translate.X, -window.IslandBody.ActualWidth / 2 - 26, TimeSpan.FromMilliseconds(1200)) { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } });
            PrivateCall(window, "BeginLiquidOrbMotion", orb, TimeSpan.FromMilliseconds(1200));
            // A Background-priority sleep can resume after the whole animation under DWM load.
            // Stop on an actual rendered middle frame before examining the native contour.
            var splitFrame = new System.Windows.Threading.DispatcherFrame();
            var splitTimeout = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Send)
                { Interval = TimeSpan.FromSeconds(3) };
            long splitStarted = Environment.TickCount64;
            EventHandler middleFrame = (_, _) => { if (Environment.TickCount64 - splitStarted >= 280) splitFrame.Continue = false; };
            splitTimeout.Tick += (_, _) => { splitTimeout.Stop(); splitFrame.Continue = false; };
            CompositionTarget.Rendering += middleFrame; splitTimeout.Start();
            try { System.Windows.Threading.Dispatcher.PushFrame(splitFrame); }
            finally { splitTimeout.Stop(); CompositionTarget.Rendering -= middleFrame; }
            var motionDebug = AgentField<Dictionary<Border, (long Start, double Duration, LiquidMotionKind Kind)>>(window, "_liquidOrbMotions");
            string timing = motionDebug.TryGetValue(orb, out var lease) ? $"phase={(Environment.TickCount64 - lease.Start) / lease.Duration:F3}, kind={lease.Kind}" : "no motion lease";
            Console.WriteLine($"Native split: {timing}, state={AgentField<IslandState>(window, "_state")}, visible={orb.IsVisible}, opacity={orb.Opacity:F2}, main={PrivateCall(window, "GlassRect", window.IslandBody, false)}, orb={PrivateCall(window, "GlassRect", orb, false)}");
            Check(AgentField<LiquidBridgeAdorner>(window, "_liquidBridgeLayer").Bridges != null, "完整窗口的小岛分离实际绘制连接颈");
            var contours = AgentField<Dictionary<Border, LiquidIslandShape>>(window, "_liquidContours");
            Check(contours[window.IslandBody].HasContact && contours[orb].HasContact && !ReferenceEquals(contours[window.IslandBody].Geometry, contours[orb].Geometry), "原生玻璃实际分离帧保留各自独立的双侧形变轮廓");
            Capture("v103-live-splitting");
            PumpAnimations(1150); Capture("v103-live-separated");
            Check(!window.LiquidFrameLoopActive && AgentField<LiquidBridgeAdorner>(window, "_liquidBridgeLayer").Bridges == null, "实际分离动画收尾清除连接和渲染订阅");
            Check(backdrop.BoundsUpdates - boundsBefore <= 4, "多岛动画使用固定原生画布，不每帧移动窗口");
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.Volume); PumpAnimations(900);
            var chrome = AgentField<Dictionary<Border, LiquidGlassChrome>>(window, "_glassChrome");
            foreach (var item in chrome.Values) item.SetLight(new GlassLight(new Vector(-1, -.3), .8, .8, Colors.White), false);
            PumpAnimations(40); Capture("v103-live-card-left-light");
            foreach (var item in chrome.Values) item.SetLight(new GlassLight(new Vector(1, -.3), .8, .8, Colors.White), false);
            PumpAnimations(40); Capture("v103-live-card-right-light");
            Check(!window.LiquidFrameLoopActive, "真实卡片停稳后无常驻玻璃帧循环");
        }
        finally { background.Close(); }
    }
}
