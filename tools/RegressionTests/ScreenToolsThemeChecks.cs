using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AlphaIsland.PluginSdk;
using alpha_island.Controls;
using alpha_island.Core;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

internal static partial class Program
{
    private static void ScreenToolsThemeChecks(alpha_island.MainWindow window, Grid root)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        double previous = options.GlassOpacity;
        try
        {
            PrivateCall(window, "OpenComponentMenu", false);
            var menu = AgentField<ComponentRadialMenu>(window, "_componentMenu");
            menu.Measure(new System.Windows.Size(menu.Width,menu.Height));
            menu.Arrange(new Rect(0,0,menu.Width,menu.Height)); menu.UpdateLayout();
            var screen = menu.Children.OfType<Border>().Single(b => b.Child is GlyphIcon g && g.Text == GlyphArtwork.ScreenToolsGlyph);
            Check(screen.ToolTip?.ToString() == "屏幕工具" && GlyphArtwork.Titles[GlyphArtwork.ScreenToolsGlyph] == "屏幕工具",
                "切换菜单的名称与矢量图标目录统一为屏幕工具");
            var volume = menu.Children.OfType<Border>().Single(b => b.Child is GlyphIcon g && g.Text == "\uE767");
            Check(ReferenceEquals(screen.Background,volume.Background),"翻译按钮与其他按钮共享主题材质，浅色桌面上保留底色");
            var rendered = new List<(double Opacity,BitmapSource Image)>();
            foreach(double opacity in new[]{.08,.45,.8,.98})
            {
                options.GlassOpacity=opacity; PrivateCall(window,"ApplyGlassAppearance"); menu.Highlight(null); root.UpdateLayout();
                var screenPixels=RenderMenuButton(screen); var volumePixels=RenderMenuButton(volume);
                Color a=MenuPixel(screenPixels,17,6), b=MenuPixel(volumePixels,17,6);
                Check(Math.Abs(a.R-b.R)<=2 && Math.Abs(a.G-b.G)<=2 && Math.Abs(a.B-b.B)<=2,
                    $"{opacity:P0} 不透明度下翻译按钮的实际材质与其他按钮一致，内部没有第二道圈线");
                rendered.Add((opacity,screenPixels));
            }
            Check(MenuPixel(rendered[0].Image,17,6).R-MenuPixel(rendered[^1].Image,17,6).R>100,
                "菜单保持打开时调节透明度，翻译按钮会实时变深变浅");
            menu.Highlight(IslandPageIds.Volume); var expected=((GlyphIcon)volume.Child).Foreground; var expectedThickness=volume.BorderThickness;
            menu.Highlight("screen-tools");
            Check(ReferenceEquals(((GlyphIcon)screen.Child).Foreground,expected) && screen.BorderThickness==expectedThickness,
                "翻译按钮悬停使用与其他按钮一致的主题色和圈线反馈");
            var center=menu.Layout.Centers[menu.ItemIds.ToList().IndexOf("screen-tools")];
            Check(menu.ItemAt(center)=="screen-tools" && screen.Width==volume.Width && screen.Height==volume.Height,
                "主题修复保留原来的按钮大小与命中范围");
            Directory.CreateDirectory("bin/screen-tools-previews");
            SaveThemePreview(menu,"bin/screen-tools-previews/v101-themed-menu.png");
            foreach(var entry in rendered) SaveNetworkBitmap(entry.Image,$"bin/screen-tools-previews/v101-opacity-{entry.Opacity:0.00}.png");
        }
        finally
        {
            options.GlassOpacity=previous; PrivateCall(window,"ApplyGlassAppearance"); PrivateCall(window,"CloseComponentSwitcher");
        }
    }
    private static BitmapSource RenderMenuButton(FrameworkElement view)
    {
        var drawing=new DrawingVisual();
        using(var dc=drawing.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White,null,new Rect(0,0,34,34));
            dc.DrawRectangle(new VisualBrush(view),null,new Rect(0,0,34,34));
        }
        var bitmap=new RenderTargetBitmap(34,34,96,96,PixelFormats.Pbgra32); bitmap.Render(drawing); return bitmap;
    }
    private static Color MenuPixel(BitmapSource bitmap,int x,int y)
    {
        var bytes=new byte[4]; bitmap.CopyPixels(new Int32Rect(x,y,1,1),bytes,4,0); return Color.FromRgb(bytes[2],bytes[1],bytes[0]);
    }
}
