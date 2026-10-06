using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Core;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using FlowDirection = System.Windows.FlowDirection;

internal static partial class Program
{
    private static void MediaGenieGeometryChecks()
    {
        var window = new Rect(200, 120, 800, 600);
        foreach (var island in new[] { new Rect(740, 12, 344, 48), new Rect(-1280, -900, 344, 48),
            new Rect(1000, 1100, 344, 200), new Rect(250, 180, 344, 48) })
        {
            bool endpoints = true, ordered = true, contained = true;
            Rect extent = Rect.Union(window, island); extent.Inflate(.001, .001);
            for (int step = 0; step <= 100; step++)
            for (int row = 0; row <= 64; row++)
            {
                double t = step / 100.0, v = row / 64.0;
                var left = MediaHandoffPolicy.GeniePoint(window, island, t, 0, v);
                var right = MediaHandoffPolicy.GeniePoint(window, island, t, 1, v);
                ordered &= left.X < right.X && left.Y == right.Y;
                if (row > 0) ordered &= left.Y > MediaHandoffPolicy.GeniePoint(window, island, t, 0, (row - 1) / 64.0).Y;
                contained &= double.IsFinite(left.X) && double.IsFinite(left.Y) && extent.Contains(left) && extent.Contains(right);
                if (step == 0) endpoints &= left == new Point(window.Left, window.Top + window.Height * v)
                    && right == new Point(window.Right, window.Top + window.Height * v);
                if (step == 100) endpoints &= left == new Point(island.Left, island.Top + island.Height * v)
                    && right == new Point(island.Right, island.Top + island.Height * v);
            }
            Check(endpoints, "吸入网格两端精确对齐窗口与岛 " + island.Location);
            Check(ordered && contained, "吸入网格无翻折、负宽度或出界 " + island.Location);
        }
        var destination = new Rect(740, 12, 344, 48);
        double WidthAt(double v) => MediaHandoffPolicy.GeniePoint(window, destination, .4, 1, v).X
            - MediaHandoffPolicy.GeniePoint(window, destination, .4, 0, v).X;
        Check(WidthAt(0) < WidthAt(.5) && WidthAt(.5) < WidthAt(1), "靠近岛的一端先收窄形成吸入而非整张缩放");
        var edge = MediaHandoffPolicy.GeniePoint(window, destination, .4, 0, .5);
        var first = MediaHandoffPolicy.GeniePoint(window, destination, .4, 0, 0);
        var last = MediaHandoffPolicy.GeniePoint(window, destination, .4, 0, 1);
        Check(Math.Abs(edge.X - (first.X + last.X) / 2) > 1, "吸入边缘形成连续曲线");
        foreach (bool opening in new[] { false, true })
        {
            string label = "二次曲线 " + opening;
            double start = MediaHandoffPolicy.HandoffTravelProgress(0, opening);
            double early = MediaHandoffPolicy.HandoffTravelProgress(.25, opening);
            double late = MediaHandoffPolicy.HandoffTravelProgress(.75, opening);
            double travelEnd = MediaHandoffPolicy.HandoffTravelProgress(1, opening);
            Check(opening ? early - start > travelEnd - late : early - start < travelEnd - late,
                "固定二次曲线保持弹出减速、收回加速 " + label);
            Check(start == 0 && travelEnd == 1 && MediaHandoffPolicy.HandoffTravelProgress(-.1, opening) == 0
                && MediaHandoffPolicy.HandoffTravelProgress(1.2, opening) == 1,
                "曲线端点精确且限制在有效区间 " + label);
            Check(Enumerable.Range(0, 100).Select(i => MediaHandoffPolicy.HandoffTravelProgress(i / 100.0, opening))
                .Zip(Enumerable.Range(1, 100).Select(i => MediaHandoffPolicy.HandoffTravelProgress(i / 100.0, opening)),
                    (previous, next) => next >= previous).All(value => value), "全程向目标推进、不反向回弹 " + label);
            foreach (var island in new[] { destination, new Rect(-1280, -900, 344, 48), new Rect(1000, 1100, 344, 200) })
            {
                bool ordered = true, contained = true;
                Rect extent = Rect.Union(window, island); extent.Inflate(.001, .001);
                for (int step = 0; step <= 100; step++)
                for (int row = 0; row <= 64; row++)
                {
                    double t = step / 100.0, v = row / 64.0;
                    var left = MediaHandoffPolicy.HandoffPoint(window, island, t, opening, 0, v);
                    var right = MediaHandoffPolicy.HandoffPoint(window, island, t, opening, 1, v);
                    ordered &= left.X < right.X && left.Y == right.Y;
                    if (row > 0) ordered &= left.Y > MediaHandoffPolicy.HandoffPoint(window, island, t, opening, 0, (row - 1) / 64.0).Y;
                    contained &= double.IsFinite(left.X) && double.IsFinite(left.Y) && extent.Contains(left) && extent.Contains(right);
                }
                Rect source = opening ? island : window, end = opening ? window : island;
                Check(ordered && contained
                    && MediaHandoffPolicy.HandoffPoint(window, island, 0, opening, 0, 0) == source.TopLeft
                    && MediaHandoffPolicy.HandoffPoint(window, island, 1, opening, 0, 0) == end.TopLeft
                    && MediaHandoffPolicy.HandoffPoint(window, island, 1, opening, 1, 1) == end.BottomRight,
                    "固定网格无翻折、越界或回弹且首尾精确 " + label + " " + island.Location);
            }
            double MidWidthAt(double t) => MediaHandoffPolicy.HandoffPoint(window, destination, t, opening, 1, .5).X
                - MediaHandoffPolicy.HandoffPoint(window, destination, t, opening, 0, .5).X;
            Check(Enumerable.Range(0, 100).Select(i => MidWidthAt(i / 100.0))
                .Zip(Enumerable.Range(1, 100).Select(i => MidWidthAt(i / 100.0)),
                    (previous, next) => opening ? next >= previous - .001 : next <= previous + .001).All(value => value),
                "实际网格宽度单向变化、无回弹过冲 " + label);
        }
    }

    private static void MediaGenieRenderingChecks(ImageSource art)
    {
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(31, 33, 44)), null, new Rect(0, 0, 800, 600));
            draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(45, 47, 61)), null, new Rect(0, 0, 170, 600));
            draw.DrawRoundedRectangle(Brushes.IndianRed, null, new Rect(230, 95, 220, 220), 12, 12);
            for (int row = 0; row < 8; row++)
            {
                draw.DrawRoundedRectangle(Brushes.SlateGray, null, new Rect(30, 90 + 45 * row, 110, 9), 4, 4);
                draw.DrawRoundedRectangle(Brushes.LightSlateGray, null, new Rect(230, 355 + 22 * row, 390, 6), 3, 3);
            }
            draw.DrawText(new FormattedText("Media window", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 25, Brushes.White, 1), new Point(230, 37));
        }
        var snapshot = new RenderTargetBitmap(800, 600, 96, 96, PixelFormats.Pbgra32);
        snapshot.Render(visual); snapshot.Freeze();
        var from = new Rect(200, 120, 800, 600); var to = new Rect(740, 12, 344, 48);
        var animation = new MediaHandoffOverlay(from, to, snapshot, art, "Genie preview", opening: false);
        var frames = new List<BitmapSource>();
        try
        {
            animation.PlayAsync(); InvokeGenie(animation, "Finish", false);
            foreach (double progress in new[] { 0, .36, .58, .80, 1 })
            {
                InvokeGenie(animation, "ApplyFrame", progress); animation.UpdateLayout();
                var viewport = (Viewport3D)animation.Content;
                var frame = new RenderTargetBitmap((int)Math.Ceiling(viewport.ActualWidth), (int)Math.Ceiling(viewport.ActualHeight),
                    96, 96, PixelFormats.Pbgra32);
                frame.Render(viewport); frame.Freeze(); frames.Add(frame);
                var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4]; frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);
                int opaque = 0;
                for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] > 32) opaque++;
                Check(opaque > 500, "透明窗口中的吸入纹理实际可见 " + progress);
            }
        }
        finally { animation.Close(); }

        var sheet = new DrawingVisual();
        using (var draw = sheet.RenderOpen())
        {
            draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(16, 18, 25)), null, new Rect(0, 0, 1250, 240));
            for (int i = 0; i < frames.Count; i++)
            {
                draw.DrawImage(frames[i], new Rect(i * 250 + 5, 32, 240, 200));
                draw.DrawText(new FormattedText(new[] { "0%", "36%", "58%", "80%", "100%" }[i], CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 15, Brushes.White, 1), new Point(i * 250 + 10, 8));
            }
        }
        var preview = new RenderTargetBitmap(1250, 240, 96, 96, PixelFormats.Pbgra32); preview.Render(sheet);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(preview));
        using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, "media-genie-preview.png"))) encoder.Save(output);

        int revealed = 0, covered = 0; double revealProgress = -1;
        MediaHandoffOverlay? opening = null;
        opening = new MediaHandoffOverlay(to, from, snapshot, art, "打开动画", opening: true,
            reveal: () => { revealed++; revealProgress = opening!.Progress; }, sourceCovered: () => covered++);
        try
        {
            var task = opening.PlayAsync();
            Check(covered == 0 && opening.Progress == 0, "显示过渡窗口前准备首帧且原始卡片仍保留");
            alpha_island.Interop.NativeMethods.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(opening).Handle, out var initialBounds);
            Rect expectedBounds = Rect.Union(to, from); expectedBounds.Inflate(24, 24);
            Check(initialBounds.Left == (int)expectedBounds.Left && initialBounds.Top == (int)expectedBounds.Top
                && initialBounds.Right - initialBounds.Left == (int)expectedBounds.Width
                && initialBounds.Bottom - initialBounds.Top == (int)expectedBounds.Height, "首帧窗口已在正确位置与尺寸，不先显示默认空白窗口");
            PumpAnimations(140);
            Check(revealed == 0 && !task.IsCompleted && covered == 1, "快照呈现后才接管卡片，反向途中不提前显示真实矩形窗口");
            PumpAnimations(470);
            Check(task.IsCompletedSuccessfully && task.Result && revealed == 1 && revealProgress == 1,
                "反向展开结束时只还原真实窗口一次");
        }
        finally { opening.Close(); }
        MediaGenieTransferChecks(snapshot, art, from, to);
    }

    private static void MediaGenieTransferChecks(ImageSource snapshot, ImageSource art, Rect from, Rect to)
    {
        bool ready = false; int prepared = 0;
        var overlay = new MediaHandoffOverlay(from, to, snapshot, art, "收尾交接回归", opening: false,
            reveal: () => prepared++, destinationReady: () => ready);
        var opacity = new List<double>();
        EventHandler sample = (_, _) => opacity.Add(((Viewport3D)overlay.Content).Opacity);
        try
        {
            var task = overlay.PlayAsync();
            var arrival = System.Diagnostics.Stopwatch.StartNew();
            while (prepared == 0 && arrival.ElapsedMilliseconds < 500) PumpAnimations(10);
            Check(prepared == 1 && overlay.Progress == 1 && !task.IsCompleted && ((Viewport3D)overlay.Content).Opacity == 1,
                "目标尚未就绪时保留完整快照，不出现空白交接帧");
            CompositionTarget.Rendering += sample;
            ready = true; PumpAnimations(180);
            Check(task.IsCompletedSuccessfully && task.Result && opacity.Any(value => value > 0 && value < 1)
                && opacity.Count > 1 && opacity.Last() == 0,
                "目标就绪后快照经过可见的短淡出再释放");
            Check(opacity.Zip(opacity.Skip(1), (previous, next) => next <= previous).All(value => value) && prepared == 1,
                "交接透明度单向下降，不重复闪回或重新准备目标");
        }
        finally { CompositionTarget.Rendering -= sample; overlay.Close(); }
    }

    private static void InvokeGenie(MediaHandoffOverlay overlay, string method, object value) =>
        typeof(MediaHandoffOverlay).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(overlay, new[] { value });
}
