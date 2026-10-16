using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AlphaIsland.PluginSdk;
using alpha_island.Controls;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

internal static partial class Program
{
    private static byte[] GlyphPixels(GlyphIcon glyph)
    {
        glyph.Width=32; glyph.Height=32; glyph.Measure(new(32,32)); glyph.Arrange(new(0,0,32,32)); glyph.UpdateLayout();
        var bitmap=new RenderTargetBitmap(32,32,96,96,PixelFormats.Pbgra32); bitmap.Render(glyph);
        var pixels=new byte[32*32*4]; bitmap.CopyPixels(pixels,32*4,0); return pixels;
    }
    private static void RedesignedIconChecks()
    {
        var invisible=new List<string>(); var fontDependent=new List<string>();
        foreach(var (glyph,title) in GlyphArtwork.Titles)
        {
            var icon=new GlyphIcon { Text=glyph, Foreground=Brushes.White, FontFamily=new("Segoe MDL2 Assets") };
            byte[] first=GlyphPixels(icon);
            if(Enumerable.Range(0,32*32).Count(i=>first[i*4+3]>100)<8) invisible.Add(title);
            icon.FontFamily=new("Georgia");
            if(!first.SequenceEqual(GlyphPixels(icon))) fontDependent.Add(title);
        }
        Check(GlyphArtwork.Titles.Count>=99 && invisible.Count==0,"设置、插件和控件目录的全部重绘图标能在 32px 清晰渲染");
        Check(fontDependent.Count==0,"重绘图标完全由矢量路径绘制，更换字体不会变成缺字框");
        foreach (string glyph in GlyphArtwork.Titles.Keys)
        {
            var document=System.Xml.Linq.XDocument.Parse(GlyphArtwork.ExportSvg(glyph)!);
            foreach(var path in document.Root!.Elements()) _=Geometry.Parse((string)path.Attribute("d")!);
        }
        Check(true,"全部重绘路径可导出可解析的 SVG，供后续编辑审阅");
        foreach(string glyph in new[]{"\uE713","\uE74D","\uE721","\uEA86","\uE8D6","\uE8C1","\uE962","\uE950","\uE72E","\uE785"})
            Check(GlyphArtwork.Titles.ContainsKey(glyph),$"常用入口 U-{(int)glyph[0]:X4} 保持原 glyph ID 可供插件使用");
        var converter=new GlyphContentConverter();
        Check(converter.Convert("\uE892",typeof(object),null!,CultureInfo.InvariantCulture) is GlyphIcon { Text:"\uE892" },"媒体按钮的字符串图标实际转换为重绘矢量控件");
        Check((string?)converter.Convert("打开设置",typeof(object),null!,CultureInfo.InvariantCulture)=="打开设置","普通按钮文字保持原文字，不当作图标绘制");
        var child=new SemanticIcon();
        Check(ReferenceEquals(child,converter.Convert(child,typeof(object),null!,CultureInfo.InvariantCulture)),"播放暂停等动态图标按钮保留原控件和动画绑定");

        var battery=new SemanticIcon { Width=64, Height=64, Shape=IconShape.Battery, Active=false, Level=.15, Foreground=Brushes.Cyan };
        battery.Measure(new(64,64)); battery.Arrange(new(0,0,64,64));
        byte[] low=SemanticIconPixels(battery); battery.Level=.05; byte[] critical=SemanticIconPixels(battery);
        Check(HasColour(low,colour=>colour.R>180 && colour.G is >70 and <200 && colour.B<100),"低电量实际填充为橙色");
        Check(HasColour(critical,colour=>colour.R>180 && colour.G<130 && colour.B<150),"极低电量实际填充为红色");
        battery.Level=.75; battery.Active=true; byte[] charge=SemanticIconPixels(battery);
        Check(HasColour(charge,colour=>colour.G>180 && colour.R<100 && colour.B<180),"接通电源的连线、充电口和电量包含绿色状态");
        battery.Active=false; byte[] unplug=SemanticIconPixels(battery);
        Check(!HasColour(unplug,colour=>colour.G>180 && colour.R<100 && colour.B<180),"拔电源后静态图标没有充电连线");

        var bluetooth=new SemanticIcon { Width=64,Height=64,Shape=IconShape.Bluetooth,Active=true,Source=BluetoothDeviceArtwork.For("AirPods Pro") };
        bluetooth.Measure(new(64,64)); bluetooth.Arrange(new(0,0,64,64));
        byte[] connected=SemanticIconPixels(bluetooth);
        Check(HasColour(connected,colour=>colour.B>150 && colour.G>150 && colour.R<100),"耳机图标的右下角保留蓝牙徽标");
        bluetooth.Active=false; byte[] disconnected=SemanticIconPixels(bluetooth);
        Check(HasColour(disconnected,colour=>colour.B>150 && colour.G>150 && colour.R<100)
            && !HasColour(disconnected,colour=>colour.R>180 && colour.G<130 && colour.B<150)
            && SemanticPixelDifference(connected,disconnected)>50,"设备断联返回已开启的蓝色蓝牙图标，设备轮廓退出");
    }
    private static bool HasColour(byte[] pixels,Func<Color,bool> predicate) => Enumerable.Range(0,pixels.Length/4)
        .Any(i=>pixels[i*4+3]>180 && predicate(Color.FromRgb(pixels[i*4+2],pixels[i*4+1],pixels[i*4])));
}
