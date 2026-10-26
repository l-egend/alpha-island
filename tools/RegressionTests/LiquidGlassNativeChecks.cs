using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Interop;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;

internal static partial class Program
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LiquidScreenPoint { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static extern IntPtr LiquidWindowFromPoint(LiquidScreenPoint point);

    private static void LiquidGlassNativeChecks()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { LiquidGlassNativeOutput(); } catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }

    private static void LiquidGlassNativeOutput()
    {
        string folder = Path.GetFullPath("bin/liquid-glass-previews"); Directory.CreateDirectory(folder);
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(50, 105, 170)), null, new Rect(0, 0, 12, 12));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(245, 177, 120)), null, new Rect(0, 0, 6, 6));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(245, 177, 120)), null, new Rect(6, 6, 6, 6));
        }
        drawing.Freeze();
        var pattern = new DrawingBrush(drawing) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 12, 12) };
        var host = new Window { Left = 100, Top = 140, Width = 680, Height = 390, WindowStyle = WindowStyle.None,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true, Background = pattern, ResizeMode = ResizeMode.NoResize };
        BackdropWindow? backdrop = null;
        try
        {
            host.Show(); PumpAnimations(120); NativeMethods.DwmFlush();
            var top = host.PointToScreen(new Point()); var bottom = host.PointToScreen(new Point(680, 390));
            double sx = (bottom.X - top.X) / 680, sy = (bottom.Y - top.Y) / 390;
            int width = (int)Math.Round(bottom.X - top.X), height = (int)Math.Round(bottom.Y - top.Y);
            System.Drawing.Bitmap Capture(string name)
            {
                PumpAnimations(100); NativeMethods.DwmFlush();
                var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) graphics.CopyFromScreen((int)top.X, (int)top.Y, 0, 0, new System.Drawing.Size(width, height));
                bitmap.Save(Path.Combine(folder, name + ".png")); return bitmap;
            }
            using var plain = Capture("v103-plain");
            var hitPoint = new LiquidScreenPoint { X = (int)top.X + 20, Y = (int)top.Y + 20 };
            var baselineHit = LiquidWindowFromPoint(hitPoint);
            backdrop = BackdropWindow.TryCreate(); Check(backdrop is { IsAvailable: true }, "像素验证使用真实 DWM 背景合成");
            backdrop!.SetBounds((int)top.X, (int)top.Y, width, height);
            var shape = new LiquidIslandShape();
            foreach (var sample in new[] { (Name: "pill", W: 344d, H: 48d, X: .9, Y: 0d), (Name: "card", W: 430d, H: 240d, X: .15, Y: .8) })
            {
                shape.Update(new(sample.W, sample.H, 0, new CornerRadius(24)), sample.X, sample.Y);
                var scene = new GeometryGroup { FillRule = FillRule.Nonzero };
                var main = shape.Geometry.Clone(); main.Transform = new TranslateTransform(170, 65); scene.Children.Add(main);
                scene.Children.Add(new RectangleGeometry(new Rect(120, 71, 36, 36), 18, 18));
                if (sample.Name == "pill" && LiquidCellMotion.Evaluate(new Rect(120, 71, 36, 36), new Rect(170, 65, sample.W, sample.H), .5, LiquidMotionKind.Split).Neck is { } bridge) scene.Children.Add(bridge);
                scene.Transform = new ScaleTransform(sx, sy);
                Check(backdrop.SetOutline(scene), "真实 " + sample.Name + " 液态与卫星复合裁剪被接受");
                backdrop.SetOpacity(.7); backdrop.SetVisible(true);
                using var image = Capture("v103-native-" + sample.Name);
                var hit = LiquidWindowFromPoint(hitPoint);
                Check(hit == baselineHit && hit != backdrop.Handle, "透明材质画布不挡住下方窗口鼠标命中 " + sample.Name);
                var boundary = scene.GetWidenedPathGeometry(new Pen(Brushes.White, 6 * Math.Max(sx, sy)));
                int outside = 0, changedInside = 0, checkedInside = 0;
                for (int y = 4; y < height - 4; y += 5) for (int x = 4; x < width - 4; x += 5)
                {
                    var a = plain.GetPixel(x, y); var b = image.GetPixel(x, y);
                    int difference = Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
                    var point = new Point(x, y);
                    if (boundary.FillContains(point)) continue;
                    if (!scene.FillContains(point)) outside = Math.Max(outside, difference);
                    else { checkedInside++; if (difference > 10) changedInside++; }
                }
                Console.WriteLine($"Native {sample.Name}: outside={outside}, changedInside={changedInside}/{checkedInside}");
                Check(outside <= 3, sample.Name + " 液态凹口、所有圆角及多岛间隙没有矩形底色");
                Check(checkedInside > 10 && changedInside > checkedInside * .65, sample.Name + " 内部保留真实桌面模糊而非纯色填充");
                backdrop.SetVisible(false);
            }
            var times = new List<double>();
            for (int i = 0; i < 100; i++)
            {
                shape.Update(new(344, 48, 0, new CornerRadius(24)), LiquidIslandShape.Pulse(i / 99d), 0);
                long start = Stopwatch.GetTimestamp(); Check(backdrop.SetOutline(shape.Geometry), "原生连续路径提交 " + i);
                times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            Check(backdrop.BoundsUpdates == 1, "一百帧形变保持原生窗口固定，不逐帧 SetWindowPos");
            times.Sort();
            Console.WriteLine($"Contour submit ms: median={times[50]:F3}, p95={times[95]:F3}");
            Check(times[95] < 12, "矢量裁剪 CPU 提交在帧预算内");
            var sampleTimes = new List<double>();
            var screenBody = new Rect(top.X + 180 * sx, top.Y + 170 * sy, 300 * sx, 48 * sy);
            var screenArea = new Rect(top, bottom);
            for (int i = 0; i < 12; i++)
            {
                var result = DesktopLightSampler.Capture(screenBody, screenArea, new[] { screenBody });
                Check(result is { Patches: > 0 and <= 4 }, "小区域采样只读取至多四个色块 " + i);
                sampleTimes.Add(result!.Value.Milliseconds);
            }
            host.Background = new LinearGradientBrush(Colors.White, Colors.Black, 0); PumpAnimations(80); NativeMethods.DwmFlush();
            var lightLeft = DesktopLightSampler.Capture(screenBody, screenArea, new[] { screenBody });
            Check(lightLeft is { } leftResult && leftResult.Light.Direction.X < -.5, "真实左侧亮背景得到左向光源");
            host.Background = new LinearGradientBrush(Colors.Black, Colors.White, 0); PumpAnimations(80); NativeMethods.DwmFlush();
            var lightRight = DesktopLightSampler.Capture(screenBody, screenArea, new[] { screenBody });
            Check(lightRight is { } rightResult && rightResult.Light.Direction.X > .5, "真实右侧亮背景得到右向光源");
            host.Background = new SolidColorBrush(Color.FromRgb(146, 196, 248));
            var panel = new System.Windows.Controls.Canvas();
            var material = new System.Windows.Controls.Border { Width = 300, Height = 48, CornerRadius = new CornerRadius(24),
                Background = new SolidColorBrush(Color.FromRgb(75, 104, 135)) };
            System.Windows.Controls.Canvas.SetLeft(material, 180); System.Windows.Controls.Canvas.SetTop(material, 170);
            panel.Children.Add(material); host.Content = panel; PumpAnimations(100); NativeMethods.DwmFlush();
            var surfaceProbe = new Rect(host.PointToScreen(new Point(297, 176)), host.PointToScreen(new Point(303, 180)));
            var materialSample = DesktopLightSampler.Capture(screenBody, screenArea, new[] { screenBody }, surfaceProbe);
            double expectedSurface = SpectrumLuminance(Color.FromRgb(75, 104, 135));
            Check(materialSample is { SurfaceBrightness: double surface } && Math.Abs(surface - expectedSurface) < .01
                && materialSample.Value.Light.Brightness > surface + .15,
                "真实第五块采样分离较暗的玻璃与明亮桌面，五列缓冲区步幅正确");
            var handleBackground = new System.Windows.Controls.Border { Width = 48, Height = 48, Background = Brushes.White };
            System.Windows.Controls.Canvas.SetLeft(handleBackground, 485); System.Windows.Controls.Canvas.SetTop(handleBackground, 170);
            panel.Children.Add(handleBackground); PumpAnimations(80); NativeMethods.DwmFlush();
            var handleProbe = new Rect(host.PointToScreen(new Point(498, 188)), host.PointToScreen(new Point(502, 194)));
            var combined = DesktopLightSampler.Capture(screenBody, screenArea, new[] { screenBody }, surfaceProbe, handleProbe);
            Check(combined is { SurfaceBrightness: double materialLight, HandleBrightness: double handleLight }
                && Math.Abs(materialLight - expectedSurface) < .01 && handleLight > .97,
                "六列缓冲区同时读取玻璃和切换条旁的局部背景，亮度互不混淆");
            var handleOnly = DesktopLightSampler.Capture(screenBody, screenArea, new[] { screenBody }, null, handleProbe);
            Check(handleOnly is { SurfaceBrightness: null, HandleBrightness: > .97 }, "未显示频谱时切换条仍可独立采样，第五列留空不影响步幅");
            var excludedHandle = DesktopLightSampler.Capture(screenBody, screenArea, new[] { screenBody, handleProbe }, null, handleProbe);
            Check(excludedHandle is { HandleBrightness: null }, "切换条采样与其他岛重叠时跳过，避免读取岛内图标");
            sampleTimes.Sort();
            Console.WriteLine($"Desktop light sample ms: median={sampleTimes[6]:F3}, p95={sampleTimes[^1]:F3}, maxPixels=384, cadenceMs=700+");
            var report = new { ContourMedianMs = times[50], ContourP95Ms = times[95], SampleMedianMs = sampleTimes[6], SampleP95Ms = sampleTimes[^1], MaxReadbackPixels = 384, MinimumSampleIntervalMs = 700, NativeBoundsChangesFor100Frames = backdrop.BoundsUpdates };
            File.WriteAllText(Path.Combine(folder, "v103-performance.json"), System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        finally { backdrop?.Dispose(); host.Close(); }
    }
}
