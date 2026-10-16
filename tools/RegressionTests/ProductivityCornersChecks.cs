using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Core;
using Point = System.Windows.Point;
internal static partial class Program
{
    private static void ProductivityCornersChecks(alpha_island.MainWindow window)
    {
        MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(true, false);
        window.Left = -30000; window.Top = 0; window.Show();
        PrivateCall(window, "SetState", IslandState.Card, true, "productivity"); window.UpdateLayout();
        var root = (System.Windows.FrameworkElement)window.Content;
        var body = window.IslandBody;
        var top = body.TranslatePoint(new Point(), root);
        var bottom = body.TranslatePoint(new Point(0, body.ActualHeight), root);
        Console.WriteLine($"Production card: window={window.ActualHeight:F1}, root={root.ActualHeight:F1}, card={body.ActualHeight:F1}, top={top.Y:F1}, bottom={bottom.Y:F1}");
        string folder = Path.GetFullPath("bin/productivity-corner-previews"); Directory.CreateDirectory(folder);
        var image = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var file = File.Create(Path.Combine(folder, "productivity.png"))) encoder.Save(file);
        Check(bottom.Y + 2 <= root.ActualHeight, "生产力卡片完整落在真实窗口中，底部圆角不被裁成直角");
        var pixels = new byte[4];
        image.CopyPixels(new Int32Rect((int)top.X+2,(int)bottom.Y-3,1,1),pixels,4,0);
        Check(pixels[3] == 0, "生产力卡片左下圆角外的实际渲染像素透明");
        image.CopyPixels(new Int32Rect((int)(top.X+body.ActualWidth)-3,(int)bottom.Y-3,1,1),pixels,4,0);
        Check(pixels[3] == 0, "生产力卡片右下圆角外的实际渲染像素透明");
        PrivateCall(window, "SetState", IslandState.Bar, true, null); window.UpdateLayout();
        Check(window.Height == 340, "收起高卡片后释放多余宿主空间");
        MotionPolicy.Apply(false, false);
        PrivateCall(window, "SetState", IslandState.Card, true, "productivity"); window.UpdateLayout();
        double reserved = window.Height;
        Check(reserved >= 412, "展开动画开始之前已预留完整高卡片空间");
        PumpAnimations(100);
        Check(window.Height == reserved, "形变过程中不逐帧调整原生宿主大小");
        PumpAnimations(650); window.UpdateLayout();
        Check(body.TranslatePoint(new Point(0,body.ActualHeight),root).Y+2<=root.ActualHeight,
            "生产力卡片弹性展开结束后仍完整保留底部圆角");
        MotionPolicy.Apply(true, false);
        PrivateCall(window, "SetState", IslandState.Card, true, "volume");
        PrivateCall(window, "SetCardDrawerExtra", 210d); window.UpdateLayout();
        Check(body.TranslatePoint(new Point(0,body.ActualHeight),root).Y+2<=root.ActualHeight,
            "音量抽屉同样使用完整宿主空间");
        PrivateCall(window, "SetState", IslandState.Card, true, "productivity"); window.UpdateLayout();
        Check(window.Height >= 412 && body.TranslatePoint(new Point(0,body.ActualHeight),root).Y+2<=root.ActualHeight,
            "从音量抽屉切换到生产力卡片，不被旧的恢复高度逻辑截断");
    }
}