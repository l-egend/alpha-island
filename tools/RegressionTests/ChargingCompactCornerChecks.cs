using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Core;
using alpha_island.Services;
using Point = System.Windows.Point;

internal static partial class Program
{
    private static void ChargingCompactCornerChecks(alpha_island.MainWindow window, Grid root)
    {
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        var options = AgentField<IslandOptions>(window, "_options");
        var previousMedia = AgentField<SmtcService?>(window, "_smtc");
        using var media = new SmtcService(window.Dispatcher);
        var failures = new List<string>();
        string folder = Path.GetFullPath("bin/charging-compact-corner-previews"); Directory.CreateDirectory(folder);
        void Capture(Border border, string label)
        {
            PumpAnimations(40); root.UpdateLayout();
            if (border.Visibility != Visibility.Visible || border.ActualWidth < 2 || border.ActualHeight < 2)
                throw new Exception("Compact corner fixture is not visible: " + label);
            var full = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); full.Render(root);
            var transform = border.TransformToAncestor(root);
            var bounds = transform.TransformBounds(new Rect(border.RenderSize));
            int width = (int)Math.Round(border.ActualWidth), height = (int)Math.Round(border.ActualHeight), margin = 6;
            var image = new CroppedBitmap(full, new Int32Rect((int)Math.Floor(bounds.X) - margin, (int)Math.Floor(bounds.Y) - margin,
                (int)Math.Ceiling(bounds.Right) - (int)Math.Floor(bounds.X) + margin * 2, (int)Math.Ceiling(bounds.Bottom) - (int)Math.Floor(bounds.Y) + margin * 2));
            var pixels = new byte[full.PixelWidth * full.PixelHeight * 4]; full.CopyPixels(pixels, full.PixelWidth * 4, 0);
            int Alpha(int x, int y)
            {
                var at = transform.Transform(new Point(x, y));
                return pixels[((int)Math.Round(at.Y) * full.PixelWidth + (int)Math.Round(at.X)) * 4 + 3];
            }
            int maximum = new[] { (2, 2), (width - 3, 2), (2, height - 3), (width - 3, height - 3) }.Max(p => Alpha(p.Item1, p.Item2));
            Console.WriteLine($"Compact {label}: {width}x{height}, displayed={bounds.Width:0.##}x{bounds.Height:0.##}, maximumCornerAlpha={maximum}, centerAlpha={Alpha(width / 2, height / 2)}");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(Path.Combine(folder, label + ".png"))) encoder.Save(stream);
            if (maximum > 1) failures.Add(label + "=" + maximum);
            else Check(true, label + " 的四角透明，没有投影形成的矩形残留");
            Check(Alpha(width / 2, height / 2) > 20, label + " 保留完整的玻璃电量胶囊");
        }
        try
        {
            media.SetDebugNowPlaying(new NowPlayingInfo { Title = "保留音乐主岛", State = PlaybackState.Playing });
            SetAgentField(window, "_smtc", media); PrivateCall(window, "SetMusicPinned", true);
            PrivateCall(window, "UpdateChargingIsland", new BatteryStatusInfo(80, true, BatteryChargeType.StandardAC, BatteryPowerEvent.None, null, IsOnAcPower: true));
            PrivateCall(window, "SetLayerPinned", "charging", true);
            PrivateCall(window, "SetState", IslandState.Pill, true);
            PumpAnimations(80); root.UpdateLayout();
            PrivateCall(window, "SwitchMainIsland", "music");
            PrivateCall(window, "SyncSatelliteOrbs"); PumpAnimations(80); root.UpdateLayout();
            var orbs = AgentField<Dictionary<string, Border>>(window, "_satelliteOrbViews");
            foreach (string key in new[] { "charging", "charging-preview" })
            {
                if (key == "charging-preview")
                {
                    PrivateCall(window, "PreviewChargingIsland"); PrivateCall(window, "SwitchMainIsland", "music");
                    PrivateCall(window, "SyncSatelliteOrbs");
                }
                foreach (double opacity in new[] { .43, .65, .98 })
                {
                    options.GlassOpacity = opacity; PrivateCall(window, "ApplyGlassAppearance");
                    Capture(orbs[key], "satellite-" + key + "-" + opacity.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            window.SatellitesHost.Visibility = Visibility.Collapsed;
            window.SideBubble.Visibility = Visibility.Visible; window.SideBubble.Opacity = 1;
            window.SideBubbleTranslate.X = -300; window.SideBubbleTranslate.Y = 0;
            window.SideBubbleScale.ScaleX = window.SideBubbleScale.ScaleY = 1;
            foreach (double opacity in new[] { .43, .65, .98 })
            {
                options.GlassOpacity = opacity; PrivateCall(window, "ApplyGlassAppearance");
                foreach (string key in new[] { "charging", "charging-preview" })
                {
                    PrivateCall(window, "UpdateCompactIslandContent", key);
                    Capture(window.SideBubble, "card-side-" + key + "-" + opacity.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            if (failures.Count > 0) throw new Exception("Compact battery corner artifacts: " + string.Join(", ", failures));
        }
        finally
        {
            window.SideBubble.Visibility = Visibility.Collapsed; window.SatellitesHost.Visibility = Visibility.Visible;
            layers.SetPinned("charging", false); layers.HideAll();
            PrivateCall(window, "SetMusicPinned", false); SetAgentField(window, "_smtc", previousMedia);
        }
    }
}
