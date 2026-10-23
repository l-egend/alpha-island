using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Controls;
using alpha_island.Core;
using Color = System.Windows.Media.Color;

internal static partial class Program
{
    private static void NotificationLogoFitChecks()
    {
        var pixels = new byte[64 * 64 * 4];
        for (int y = 24; y < 40; y++) for (int x = 24; x < 40; x++)
        { int at = (y * 64 + x) * 4; pixels[at] = 220; pixels[at + 1] = 160; pixels[at + 3] = 255; }
        var bitmap = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4); bitmap.Freeze();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new System.IO.MemoryStream(); encoder.Save(stream);
        var notice = new DesktopNotification("logo-fit", "logo-fit-fixture", "测试应用", "设备已连接", "准备就绪", DateTimeOffset.Now,
            DesktopEventKind.Message, Logo: stream.ToArray());
        var icon = new SemanticIcon { Width = 26, Height = 26, Shape = IconShape.Notification, Source = DesktopNotificationIcons.GetPresentationIcon(notice) };
        icon.Measure(new(26, 26)); icon.Arrange(new Rect(0, 0, 26, 26));
        var result = new RenderTargetBitmap(26, 26, 96, 96, PixelFormats.Pbgra32); result.Render(icon);
        byte[] output = new byte[26 * 26 * 4]; result.CopyPixels(output, 26 * 4, 0);
        int inkWidth = Enumerable.Range(0, 26).Count(x => Enumerable.Range(0, 26).Any(y => output[(y * 26 + x) * 4 + 3] > 80));
        Console.WriteLine("Padded notification logo visible width=" + inkWidth + "/26");
        Check(inkWidth >= 18 && inkWidth <= 26, "带透明留白的通知图标按实际图形放大，不在小岛里缩成几像素");
        Check(ReferenceEquals(DesktopNotificationIcons.GetPresentationIcon(notice), icon.Source), "图标归一化结果复用缓存，不逐帧重新裁切或分配图片");
    }
}
