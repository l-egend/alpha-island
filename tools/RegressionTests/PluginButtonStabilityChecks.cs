using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Plugins.Isolation;
using AlphaIsland.Plugins.ApiChat;
using Button = System.Windows.Controls.Button;
using Panel = System.Windows.Controls.Panel;
using Brushes = System.Windows.Media.Brushes;
using Size = System.Windows.Size;

internal static partial class Program
{
    private static void PluginButtonStabilityChecks(string testRoot)
    {
        var options = new IslandOptions();
        SettingsRegistry.Write(options, "AiEnabled", true);
        SettingsRegistry.Write(options, "AiBaseUrl", "http://127.0.0.1:19731/v1");
        bool minimal = Environment.GetEnvironmentVariable("ALPHA_ISLAND_BUTTON_MINIMAL") == "1";
        SettingsRegistry.Write(options, "AiModel", minimal ? "test-model" : "deepseek-flash");
        using var view = new ApiChatView(new ConversationPluginFixture(options, Path.Combine(testRoot, "button-stability")));
        FrameworkElement content = view;
        if (minimal)
        {
            var copy = (Button)view.FindName("AiCopyButton");
            ((Panel)copy.Parent).Children.Remove(copy);
            content = new Grid { Children = { copy } };
        }
        var worker = new Window {
            Width = 414, Height = 314, Left = -32000, Top = -32000, Content = content,
            AllowsTransparency = true, Background = Brushes.Transparent, Opacity = 0,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        worker.Show(); content.UpdateLayout();
        using var host = new HwndSource(new HwndSourceParameters("API button snapshot host") {
            Width = 414, Height = 314, PositionX = -32000, PositionY = -32000,
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        var remote = new RemotePluginView("chat", _ => Task.CompletedTask, error => throw error);
        host.RootVisual = remote;
        var surface = new PluginWorkerSurface(content);
        bool reduce = MotionPolicy.ReduceMotion, power = MotionPolicy.PowerSave;
        int tier = MotionPolicy.RenderTier;
        var samples = new Dictionary<string, List<(int Worker, int Host, int AlphaError)>>();
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            PumpAnimations(80);
            for (int index = 0; index < (minimal ? 2 : 48); index++)
            {
                var frame = surface.Capture(414, 314);
                remote.Apply(frame);
                remote.Measure(new Size(414, 314)); remote.Arrange(new Rect(0, 0, 414, 314)); remote.UpdateLayout();
                PumpAnimations(25);
                foreach (string name in minimal ? new[] { "AiCopyButton" } : new[] { "AiCopyButton", "AiRetryButton", "AiClearButton" })
                {
                    var item = frame.Elements.Single(control => control.Name == name);
                    using var bytes = new MemoryStream(Convert.FromBase64String(item.Artwork));
                    var decoder = new PngBitmapDecoder(bytes, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    var button = (Button)remote.FindName(name);
                    var rendered = new RenderTargetBitmap(26, 26, 96, 96, PixelFormats.Pbgra32);
                    var drawing = new DrawingVisual();
                    using (var context = drawing.RenderOpen()) context.DrawRectangle(new VisualBrush(button), null, new Rect(0, 0, 26, 26));
                    rendered.Render(drawing);
                    if (!samples.TryGetValue(name, out var values)) samples[name] = values = new();
                    values.Add((ButtonInkPixels(decoder.Frames[0]), ButtonInkPixels(rendered), ButtonAlphaError(decoder.Frames[0], rendered)));
                }
                PumpAnimations(20);
            }
            Directory.CreateDirectory("bin/PluginButton157/previews");
            SaveThemePreview(remote, "bin/PluginButton157/previews/api-buttons.png");
            foreach (var pair in samples)
            {
                Console.WriteLine($"BUTTON {pair.Key}: worker ink={pair.Value.Min(x => x.Worker)}..{pair.Value.Max(x => x.Worker)}, host ink={pair.Value.Min(x => x.Host)}..{pair.Value.Max(x => x.Host)}, alpha error={pair.Value.Max(x => x.AlphaError)}");
                Check(pair.Value.All(x => x.Worker > 0), "连续刷新时插件按钮图标没有空白帧 " + pair.Key);
                Check(pair.Value.All(x => x.Host > 0), "连续刷新时宿主按钮图标没有空白帧 " + pair.Key);
                Check(pair.Value.All(x => x.AlphaError <= 8), "连续刷新之间按钮亮度与插件原图一致 " + pair.Key);
            }
        }
        finally
        {
            remote.Close(); worker.Content = null; worker.Close(); host.RootVisual = null;
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduce, power);
        }
    }

    private static int ButtonInkPixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight]; converted.CopyPixels(pixels, stride, 0);
        int count = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
            if (pixels[offset + 3] > 50) count++;
        return count;
    }

    private static int ButtonAlphaError(BitmapSource expected, BitmapSource actual)
    {
        var first = new FormatConvertedBitmap(expected, PixelFormats.Pbgra32, null, 0);
        var second = new FormatConvertedBitmap(actual, PixelFormats.Pbgra32, null, 0);
        int stride = first.PixelWidth * 4;
        var a = new byte[stride * first.PixelHeight]; var b = new byte[a.Length];
        first.CopyPixels(a, stride, 0); second.CopyPixels(b, stride, 0);
        int maximum = 0;
        for (int offset = 3; offset < a.Length; offset += 4) maximum = Math.Max(maximum, Math.Abs(a[offset] - b[offset]));
        return maximum;
    }
}
