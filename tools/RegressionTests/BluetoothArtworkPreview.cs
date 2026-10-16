using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Controls;
using FlowDirection = System.Windows.FlowDirection;

internal static partial class Program
{
    private static void SaveBluetoothArtworkGallery()
    {
        var samples = new[] { ("入耳式", "AirPods Pro"), ("耳夹式", "FreeClip"), ("头戴式", "AirPods Max"),
            ("音箱", "JBL Flip6"), ("开放式", "OpenFit"), ("通用音频", "unknown") };
        var view = new DrawingVisual();
        using (var dc = view.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x12, 0x1A, 0x25)), null, new Rect(0, 0, 660, 170));
            for (int index = 0; index < samples.Length; index++)
            {
                double x = 12 + index * 108;
                dc.DrawRoundedRectangle(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x2C, 0x3C)), null, new Rect(x, 12, 96, 146), 14, 14);
                dc.DrawImage(BluetoothDeviceArtwork.For(samples[index].Item2), new Rect(x + 24, 27, 48, 48));
                var text = new FormattedText(samples[index].Item1, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei UI"), 13, System.Windows.Media.Brushes.White, 1);
                dc.DrawText(text, new System.Windows.Point(x + (96 - text.Width) / 2, 87));
                dc.DrawImage(BluetoothDeviceArtwork.For(samples[index].Item2), new Rect(x + 32, 114, 32, 32));
            }
        }
        var bitmap = new RenderTargetBitmap(660, 170, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        Directory.CreateDirectory("bin/icon-previews");
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create("bin/icon-previews/bluetooth-artwork.png"); png.Save(file);
    }
}
