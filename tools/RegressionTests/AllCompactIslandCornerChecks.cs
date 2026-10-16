using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Core;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using Panel = System.Windows.Controls.Panel;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

internal static partial class Program
{
    private static void AllCompactIslandCornerChecks(alpha_island.MainWindow window, Grid root)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        string folder = Path.GetFullPath("bin/all-compact-corner-previews"); Directory.CreateDirectory(folder);
        var failures = new List<string>();
        void Measure(Border border, string label)
        {
            PumpAnimations(10); root.UpdateLayout();
            var full = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); full.Render(root);
            var transform = border.TransformToAncestor(root); var bounds = transform.TransformBounds(new Rect(border.RenderSize));
            int width = (int)Math.Round(border.ActualWidth), height = (int)Math.Round(border.ActualHeight);
            var pixels = new byte[full.PixelWidth * full.PixelHeight * 4]; full.CopyPixels(pixels, full.PixelWidth * 4, 0);
            int Alpha(int x, int y)
            {
                var point = transform.Transform(new Point(x, y));
                return pixels[((int)Math.Round(point.Y) * full.PixelWidth + (int)Math.Round(point.X)) * 4 + 3];
            }
            int maximum = new[] { (2, 2), (width - 3, 2), (2, height - 3), (width - 3, height - 3) }.Max(p => Alpha(p.Item1, p.Item2));
            Console.WriteLine($"Outline {label}: size={width}x{height}, cornerAlpha={maximum}");
            if (maximum > 1) failures.Add(label + "=" + maximum);
            else Check(true, label + " 四角透明");
            Check(Alpha(width / 2, height / 2) > 20, label + " 中央玻璃及内容仍可见");
            var image = new CroppedBitmap(full, new Int32Rect((int)Math.Floor(bounds.X) - 6, (int)Math.Floor(bounds.Y) - 6,
                (int)Math.Ceiling(bounds.Right) - (int)Math.Floor(bounds.X) + 12, (int)Math.Ceiling(bounds.Bottom) - (int)Math.Floor(bounds.Y) + 12));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = File.Create(Path.Combine(folder, label + ".png")); encoder.Save(output);
        }
        void Inspect(Border border, string key)
        {
            foreach (double opacity in new[] { .43, .65, .98 })
            {
                options.GlassOpacity = opacity; PrivateCall(window, "ApplyGlassAppearance");
                string suffix = opacity.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                Measure(border, key + "-" + suffix);
                // Album art and colour highlights can fill the child: verify the shared rounded clip too.
                if (border.Child is Panel panel)
                {
                    var old = panel.Background; panel.Background = Brushes.CornflowerBlue;
                    Measure(border, key + "-full-bleed-" + suffix); panel.Background = old;
                }
            }
        }
        foreach (string key in new[] { "music", "volume", "battery", "bluetooth", "download", "netspeed", "status", "charging", "charging-preview", "system", "clipboard", "approval", "agentstatus", "desktoptoast", "card/focus" })
        {
            var border = (Border)PrivateCall(window, "CreateSatelliteOrb", key)!;
            border.RenderTransform = Transform.Identity; border.HorizontalAlignment = HorizontalAlignment.Left;
            border.VerticalAlignment = VerticalAlignment.Top; border.Margin = new Thickness(80, 112, 0, 0);
            Panel.SetZIndex(border, 10); root.Children.Add(border);
            try { Inspect(border, key.Replace('/', '-')); }
            finally { root.Children.Remove(border); }
        }
        foreach (var entry in new[] { (window.PinBubble, "pin-bubble"), (window.SideBubble, "card-side"), (window.MusicOrbBubble, "music-orb") })
        {
            var border = entry.Item1; var oldTransform = border.RenderTransform; var oldMargin = border.Margin;
            border.RenderTransform = Transform.Identity; border.HorizontalAlignment = HorizontalAlignment.Left;
            border.Margin = new Thickness(80, 112, 0, 0); border.Visibility = Visibility.Visible; border.Opacity = 1;
            try { Inspect(border, entry.Item2); }
            finally { border.Visibility = Visibility.Collapsed; border.RenderTransform = oldTransform; border.Margin = oldMargin; }
        }
        if (failures.Count > 0) throw new Exception("Compact island outlines: " + string.Join(", ", failures));
    }
}
