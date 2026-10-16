using System.IO;
using System.Windows;
using System.Windows.Media;
using alpha_island.Interop;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

internal static partial class Program
{
    private static void BackdropOpacityChecks()
    {
        string folder = Path.GetFullPath("bin/backdrop-opacity-previews"); Directory.CreateDirectory(folder);
        var pattern = new DrawingGroup();
        using (var dc = pattern.Open())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(48, 120, 210)), null, new Rect(0, 0, 16, 16));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(240, 164, 72)), null, new Rect(0, 0, 8, 8));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(240, 164, 72)), null, new Rect(8, 8, 8, 8));
        }
        pattern.Freeze();
        var background = new DrawingBrush(pattern) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 16, 16), ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 16, 16) };
        background.Freeze();
        var work = SystemParameters.WorkArea;
        var host = new System.Windows.Window { Width = 520, Height = 400, Left = work.Left + (work.Width - 520) / 2,
            Top = work.Bottom - 424, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true, Background = background };
        alpha_island.Interop.BackdropWindow? backdrop = null;
        var failures = new List<string>();
        try
        {
            host.Show(); PumpAnimations(100); DwmFlush();
            backdrop = BackdropWindow.TryCreate();
            Check(backdrop is { IsAvailable: true }, "当前 Windows 原生模糊窗口可创建，检查覆盖真实 DWM 合成输出");
            backdrop!.SetVisible(false);
            foreach (var shape in new[] { (Name: "pill", Width: 344d, Height: 48d, Radius: 24d), (Name: "card", Width: 420d, Height: 280d, Radius: 28d) })
            {
                var start = host.PointToScreen(new Point(32, 32));
                var end = host.PointToScreen(new Point(32 + shape.Width, 32 + shape.Height));
                int x = (int)Math.Round(start.X) - 2, y = (int)Math.Round(start.Y) - 2;
                int w = (int)Math.Round(end.X - start.X) + 4, h = (int)Math.Round(end.Y - start.Y) + 4;
                double radius = shape.Radius * (end.X - start.X) / shape.Width;
                System.Drawing.Bitmap Capture(string label)
                {
                    PumpAnimations(120); DwmFlush();
                    var bitmap = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                        graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h), System.Drawing.CopyPixelOperation.SourceCopy);
                    bitmap.Save(Path.Combine(folder, label + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                    return bitmap;
                }
                using var plain = Capture(shape.Name + "-plain");
                foreach (double opacity in new[] { .45, .65, .98 })
                {
                    backdrop.SetBounds(x, y, w, h);
                    backdrop.SetIsland(2, 2, w - 4, h - 4, radius);
                    backdrop.SetOpacity(opacity * 1.1); backdrop.SetVisible(true);
                    using var image = Capture(shape.Name + "-" + opacity.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
                    int Difference(int px, int py)
                    {
                        var a = plain.GetPixel(px, py); var b = image.GetPixel(px, py);
                        return Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
                    }
                    int corners = new[] { (4, 4), (w - 5, 4), (4, h - 5), (w - 5, h - 5) }.Max(p => Difference(p.Item1, p.Item2));
                    int center = Difference(w / 2, h / 2);
                    Console.WriteLine($"Native {shape.Name} opacity={opacity:0.00}: cornerDifference={corners}, centerDifference={center}");
                    if (corners > 3) failures.Add($"{shape.Name}/{opacity:0.00} corners changed by {corners}");
                    else Check(true, $"{shape.Name} 在 {opacity:P0} 不透明度下四角保持桌面原色，没有矩形底色");
                    if (center < 15) failures.Add($"{shape.Name}/{opacity:0.00} backdrop absent");
                    else Check(true, $"{shape.Name} 在 {opacity:P0} 不透明度下中央仍有真实背景模糊");
                    backdrop.SetVisible(false);
                }
            }
            if (failures.Count > 0) throw new Exception("Native backdrop artifacts: " + string.Join("; ", failures));
        }
        finally { backdrop?.Dispose(); host.Close(); }
    }
}
