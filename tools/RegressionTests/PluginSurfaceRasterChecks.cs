using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Plugins.Isolation;

internal static partial class Program
{
    private static int PluginButtonInk(string artwork)
    {
        using var stream = new MemoryStream(Convert.FromBase64String(artwork));
        var bitmap = new FormatConvertedBitmap(BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        int ink = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i + 3] > 100 && pixels[i] > 180 && pixels[i + 1] > 180 && pixels[i + 2] > 180) ink++;
        return ink;
    }
    private static void PluginSurfaceRasterChecks()
    {
        static (int Ink, RemoteVisual Item) Capture(Thickness margin)
        {
            var root = new Grid();
            root.Children.Add(new System.Windows.Controls.Button
            {
                Content = "发送", Width = 64, Height = 30, Margin = margin,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 34, 42)),
                Foreground = System.Windows.Media.Brushes.White, FontSize = 14
            });
            var frame = new PluginWorkerSurface(root).Capture(414, 294);
            var item = frame.Elements.Single(element => element.Kind == "button");
            return (PluginButtonInk(item.Artwork), item);
        }
        var origin = Capture(new Thickness(0));
        var displaced = Capture(new Thickness(300, 44, 0, 0));
        Console.WriteLine($"Plugin button raster ink: origin={origin.Ink}, displaced={displaced.Ink}");
        Check(origin.Ink > 20, "插件按钮在原点的图像保留可见文字");
        Check(displaced.Item.X == 300 && displaced.Item.Y == 44 && displaced.Ink >= origin.Ink / 2,
            "插件按钮位于页面右侧或下方时，独立图像仍包含按钮文字");
    }
}
