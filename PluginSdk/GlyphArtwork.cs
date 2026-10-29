using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Media;

namespace AlphaIsland.PluginSdk;

/// <summary>
/// Original 32-unit rounded artwork. Frozen paths are decoded once and shared across the host,
/// plugin pages and review exporter. Existing glyph IDs remain stable for plugin compatibility.
/// </summary>
public static class GlyphArtwork
{
    private const string MessageOutlinePath = "M7,5 H25 Q28,5 28,8 V21 Q28,24 25,24 H15 L8,29 V24 H7 Q4,24 4,21 V8 Q4,5 7,5 Z";
    private const string MessageLinesPath = "M9,11 H23 M9,17 H20";
    private static readonly Geometry MessageOutline = FrozenDeletionPath(MessageOutlinePath);
    /// <summary>A finite lower-left entrance followed by two writing strokes; progress 1 is static.</summary>
    public static void DrawMessage(DrawingContext dc, Pen pen, double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        double move = Math.Clamp(progress / .38, 0, 1);
        move = 1 - Math.Pow(1 - move, 3);
        dc.PushTransform(new TranslateTransform(-2 * (1 - move), 2 * (1 - move)));
        dc.DrawGeometry(null, pen, MessageOutline);
        void Line(double y, double end, double from, double to)
        {
            double reveal = Math.Clamp((progress - from) / (to - from), 0, 1);
            if (reveal > 0) dc.DrawLine(pen, new(9, y), new(9 + (end - 9) * reveal, y));
        }
        Line(11, 23, .38, .60); Line(17, 20, .64, .90);
        dc.Pop();
    }
    public const string NetworkTransferGlyph = "\uE753";
    public const string ScreenToolsGlyph = "\uE8B5";
    public const string ReducedMotionGlyph = "\uEAF1";
    public const string FileShelfGlyph = "\uEAF2";
    public const string MemoryMonitorGlyph = "\uEAF3";
    public const string ProductivityGlyph = "\uEAF4";
    public const string NotificationSoundGlyph = "\uEAF5";
    private static readonly Geometry DeletionBasket = FrozenDeletionPath("M8,11 L9.2,25.5 Q9.4,28 12,28 H20 Q22.6,28 22.8,25.5 L24,11 M13,14 L13.5,23 M19,14 L18.5,23");
    private static readonly Geometry DeletionLid = FrozenDeletionPath("M6,8 H26 M12,8 V5 Q12,4 13,4 H19 Q20,4 20,5 V8");
    private static Geometry FrozenDeletionPath(string data) { var path = Geometry.Parse(data); path.Freeze(); return path; }
    private sealed record Layer(Geometry Geometry, bool Filled);
    private sealed record Icon(string Title, Layer[] Layers);
    private static readonly IReadOnlyDictionary<string, Icon> Icons = Create();
    public static IReadOnlyDictionary<string, string> Titles { get; } = new ReadOnlyDictionary<string,string>(
        Icons.ToDictionary(pair => pair.Key, pair => pair.Value.Title));

    public static Pen CreatePen(Brush ink)
    {
        var pen = new Pen(ink, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (pen.CanFreeze) pen.Freeze();
        return pen;
    }
    public static bool TryDraw(DrawingContext dc, string glyph, Brush ink, Pen pen)
    {
        if (!Icons.TryGetValue(glyph, out var icon)) return false;
        foreach (var layer in icon.Layers) dc.DrawGeometry(layer.Filled ? ink : null, layer.Filled ? null : pen, layer.Geometry);
        return true;
    }
    public static void DrawDeletion(DrawingContext dc, Pen pen, double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        dc.DrawGeometry(null, pen, DeletionBasket);
        dc.PushTransform(new RotateTransform(13 * progress, 25, 8));
        dc.PushTransform(new TranslateTransform(0, -.5 * progress));
        dc.DrawGeometry(null, pen, DeletionLid);
        dc.Pop(); dc.Pop();
    }
    /// <summary>Exports the same frozen paths as editable SVG, with no dependency on a font file.</summary>
    public static string? ExportSvg(string glyph)
    {
        if (!Icons.TryGetValue(glyph, out var icon)) return null;
        var svg = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 32 32\" color=\"#22D3EE\">\n");
        foreach (var layer in icon.Layers)
        {
            string data=layer.Geometry.ToString(CultureInfo.InvariantCulture);
            if(data.StartsWith("F0",StringComparison.Ordinal) || data.StartsWith("F1",StringComparison.Ordinal)) data=data[2..].TrimStart();
            svg.Append("  <path d=\"").Append(data).Append("\" fill=\"").Append(layer.Filled?"currentColor":"none")
                .Append("\" stroke=\"").Append(layer.Filled?"none":"currentColor").Append("\" stroke-width=\"2.2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>\n");
        }
        return svg.Append("</svg>\n").ToString();
    }
    private static IReadOnlyDictionary<string, Icon> Create()
    {
        var icons = new Dictionary<string,Icon>();
        Layer Path(string data, bool filled=false)
        {
            var geometry = Geometry.Parse(data); geometry.Freeze(); return new(geometry, filled);
        }
        void Add(string title, string codes, params Layer[] layers)
        {
            var icon = new Icon(title, layers);
            foreach (string code in codes.Split(' ')) icons.Add(char.ConvertFromUtf32(Convert.ToInt32(code,16)), icon);
        }
        Add("削减动效", "EAF1",
            Path("M4,8 H13 M4,16 H10 M4,24 H7 M18,6 L28,16 L18,26"));
        Add("通知音效", "EAF5", Path("M14,4 A2,2 0 0 1 18,4 V5 Q25,6 25,14 V19 Q25,22 29,25 H3 Q7,22 7,19 V14 Q7,6 14,5 Z M12,27 H20 A4,4 0 0 1 12,27 Z", true));
        Add("文件拖拽中转站", "EAF2",
            Path("M8,17 V5 Q8,3 10,3 H18 L24,9 V17 M18,3 V9 H24"),
            Path("M16,11 V21 M12,17 L16,21 L20,17 M4,20 V26 Q4,28 6,28 H26 Q28,28 28,26 V20"));
        Add("内存占用检测", "EAF3",
            Path("M5,7 H27 Q29,7 29,9 V22 H3 V9 Q3,7 5,7 Z M7,22 V27 M13,22 V27 M19,22 V27 M25,22 V27"),
            Path("M7,11 H12 V18 H7 Z M20,11 H25 V18 H20 Z M16,11 V18"));
        Add("生产力统计", "EAF4",
            Path("M5,18 H9 Q10,18 10,19 V27 Q10,28 9,28 H5 Q4,28 4,27 V19 Q4,18 5,18 Z M14,11 H18 Q19,11 19,12 V27 Q19,28 18,28 H14 Q13,28 13,27 V12 Q13,11 14,11 Z M23,4 H27 Q28,4 28,5 V27 Q28,28 27,28 H23 Q22,28 22,27 V5 Q22,4 23,4 Z"));
        Add("菜单", "E700", Path("M5,8 H27 M5,16 H27 M5,24 H27"));
        Add("Wi-Fi", "E701 E6E2", Path("M3,9 Q16,-2 29,9 L26,12 Q16,4 6,12 Z M8,16 Q16,9 24,16 L21,19 Q16,15 11,19 Z M12,23 Q16,19 20,23 L16,27 Z", true));
        Add("蓝牙", "E702", Path("M14,3 V29 L24,21 L7,8 M7,24 L24,11 L14,3"));
        Add("亮度", "E706", Path("M22,16 A6,6 0 1 1 10,16 A6,6 0 1 1 22,16 M16,3 V6 M16,26 V29 M3,16 H6 M26,16 H29 M7,7 L9,9 M23,23 L25,25 M7,25 L9,23 M23,9 L25,7"));
        Add("飞行模式", "E709", Path("M4,14 H11 L8,4 H12 L18,14 H26 Q29,14 29,16 Q29,18 26,18 H18 L12,28 H8 L11,18 H4 Z"));
        Add("展开", "E70D", Path("M7,12 L16,21 L25,12"));
        Add("收起", "E70E", Path("M7,20 L16,11 L25,20"));
        Add("关闭", "E711 E8BB", Path("M8,8 L24,24 M24,8 L8,24"));
        Add("设置", "E713", Path("M13,3 H19 L20,7 L24,9 L28,8 L31,13 L28,16 L28,20 L30,23 L26,28 L22,26 L19,28 L18,31 H12 L11,27 L7,25 L3,26 L1,21 L4,18 V14 L2,11 L6,6 L10,8 Z M21,17 A5,5 0 1 1 11,17 A5,5 0 1 1 21,17"));
        Add("摄像头", "E714", Path("M5,7 H20 Q22,7 22,9 V23 Q22,25 20,25 H5 Q3,25 3,23 V9 Q3,7 5,7 Z M22,12 L29,8 V24 L22,20"));
        Add("置顶", "E718", Path("M10,5 H22 M12,5 V14 L8,18 V20 H24 V18 L20,14 V5 M16,20 V29"));
        Add("取消置顶", "E77A", Path("M10,5 H22 M12,5 V14 L8,18 V20 H24 V18 L20,14 V5 M16,20 V29 M5,5 L28,28"));
        Add("停止", "E71A", Path("M8,6 H24 Q26,6 26,8 V24 Q26,26 24,26 H8 Q6,26 6,24 V8 Q6,6 8,6 Z"));
        Add("链接", "E71B", Path("M12,21 H9 A7,7 0 0 1 9,7 H14 A7,7 0 0 1 21,14 M20,11 H23 A7,7 0 0 1 23,25 H18 A7,7 0 0 1 11,18 M11,16 H21"));
        Add("麦克风", "E720", Path("M12,5 Q12,2 16,2 Q20,2 20,5 V17 Q20,21 16,21 Q12,21 12,17 Z M8,14 V17 Q8,25 16,25 Q24,25 24,17 V14 M16,25 V30 M11,30 H21"));
        Add("搜索", "E721", Path("M23,13 A10,10 0 1 1 3,13 A10,10 0 1 1 23,13 M20,20 L29,29"));
        Add("截图", "E722", Path("M5,9 H9 L11,5 H21 L23,9 H27 Q29,9 29,11 V25 Q29,27 27,27 H5 Q3,27 3,25 V11 Q3,9 5,9 Z M22,17 A6,6 0 1 1 10,17 A6,6 0 1 1 22,17"));
        Add("发送", "E724", Path("M4,4 L29,16 L4,28 L9,16 Z M9,16 H22"));
        Add("刷新", "E72C", Path("M26,12 A11,11 0 1 1 17,5 M17,5 H27 V13"));
        Add("重置", "E777", Path("M19,6 A10.5,10.5 0 1 0 26,16 L26,7 M21.5,11.5 L26,7 L30.5,11.5"));
        Add("锁定", "E72E", Path("M8,14 H24 Q26,14 26,17 V26 Q26,29 23,29 H9 Q6,29 6,26 V17 Q6,14 8,14 Z M10,14 V9 A6,6 0 0 1 22,9 V14 M16,21 V24"));
        Add("解锁", "E785", Path("M8,14 H24 Q26,14 26,17 V26 Q26,29 23,29 H9 Q6,29 6,26 V17 Q6,14 8,14 Z M10,14 V9 A6,6 0 0 1 22,9 M16,21 V24"));
        Add("收藏", "E735", Path("M16,3 L20,11 L29,12 L22,19 L24,28 L16,24 L8,28 L10,19 L3,12 L12,11 Z"));
        Add("完成", "E73E", Path("M5,16 L12,23 L27,8"));
        Add("删除", "E74D", new Layer(DeletionBasket, false), new Layer(DeletionLid, false));
        Layer speaker=Path("M14,5 L7,11 H3 V21 H7 L14,27 Z");
        Add("静音", "E74F", speaker, Path("M20,11 L29,21 M29,11 L20,21"));
        Add("音量零", "E992", speaker);
        Add("音量低", "E993", speaker, Path("M19,12 Q23,16 19,20"));
        Add("音量中", "E994", speaker, Path("M19,12 Q23,16 19,20 M23,8 Q31,16 23,24"));
        Add("音量高", "E995 E767", speaker, Path("M18,12 Q22,16 18,20 M22,8 Q29,16 22,24 M26,4 Q36,16 26,28"));
        Add("键盘", "E765", Path("M5,7 H27 Q29,7 29,9 V24 Q29,26 27,26 H5 Q3,26 3,24 V9 Q3,7 5,7 Z M7,12 H8 M12,12 H13 M17,12 H18 M23,12 H24 M7,17 H8 M12,17 H13 M17,17 H18 M23,17 H24 M9,22 H23"));
        Add("播放", "E768", Path("M9,5 L27,16 L9,27 Z",true));
        Add("随机播放", "E8B1", Path("M3,8 H7 Q10,8 13,13 L19,22 Q22,26 25,26 H29 M24,21 L29,26 L24,30 M3,25 H7 Q10,25 13,20 L19,11 Q22,7 25,7 H29 M24,2 L29,7 L24,12"));
        Add("单曲循环", "E8ED", Path("M7,8 H23 Q29,8 29,14 V17 M24,12 L29,17 L31,12 M25,25 H9 Q3,25 3,19 V16 M1,21 L3,16 L8,21 M14,13 L17,11 V22"));
        Add("顺序播放", "E8EF", Path("M3,7 H19 M3,15 H19 M3,23 H14 M23,17 V28 M19,24 L23,28 L28,24"));
        Add("暂停", "E769", Path("M9,5 H12 Q14,5 14,7 V25 Q14,27 12,27 H9 Q7,27 7,25 V7 Q7,5 9,5 Z M21,5 H24 Q26,5 26,7 V25 Q26,27 24,27 H21 Q19,27 19,25 V7 Q19,5 21,5 Z",true));
        Add("网络", "E774", Path("M29,16 A13,13 0 1 1 3,16 A13,13 0 1 1 29,16 M3,16 H29 M5,9 H27 M5,23 H27 M16,3 C7,9 7,23 16,29 C25,23 25,9 16,3 Z"));
        Add("文字识别", "E8A5", Path("M10,3 H5 Q3,3 3,5 V10 M22,3 H27 Q29,3 29,5 V10 M3,22 V27 Q3,29 5,29 H10 M29,22 V27 Q29,29 27,29 H22 M10,10 H22 M16,10 V22 M12,22 H20"));
        Add("复制", "E77F E8C8", Path("M11,10 H26 Q28,10 28,12 V27 Q28,29 26,29 H11 Q9,29 9,27 V12 Q9,10 11,10 Z M23,6 V3 H4 Q2,3 2,5 V22 H5"));
        Add("提示", "E783", Path("M29,16 A13,13 0 1 1 3,16 A13,13 0 1 1 29,16 M16,8 V18 M16,23 V24"));
        Add("外观", "E790", Path("M16,3 C7,3 2,10 3,18 C4,25 9,29 15,28 C20,27 15,23 19,21 C22,19 27,24 29,17 C32,8 24,3 16,3 Z"), Path("M11,9 A1.4,1.4 0 1 1 8.2,9 A1.4,1.4 0 1 1 11,9 M19,7 A1.4,1.4 0 1 1 16.2,7 A1.4,1.4 0 1 1 19,7 M26,12 A1.4,1.4 0 1 1 23.2,12 A1.4,1.4 0 1 1 26,12 M9,17 A1.4,1.4 0 1 1 6.2,17 A1.4,1.4 0 1 1 9,17",true));
        Add("音量混音器", "E7AC", Path("M11,7 H27 M11,16 H23 M11,25 H27 M3,5 H6 V8 H3 Z M3,14 H6 V17 H3 Z M3,23 H6 V26 H3 Z"));
        Add("警告", "E7BA", Path("M14,4 Q16,1 18,4 L29,26 Q30,29 27,29 H5 Q2,29 3,26 Z M16,11 V20 M16,24 V25"));
        Add("布局", "E7C4", Path("M4,4 H22 V12 H4 Z M4,18 H22 V28 H4 Z M27,4 V10 M27,18 V28"));
        Add("消息", "E7E7 E8BD E8F2 E91C", Path(MessageOutlinePath + " " + MessageLinesPath));
        Add("电源", "E7E8", Path("M16,2 V15 M9,6 A13,13 0 1 0 23,6"));
        Add("通知免打扰", "E7ED", Path("M7,23 H25 L23,19 V12 A7,7 0 0 0 9,12 V19 Z M13,27 Q16,31 19,27 M24,23 L30,29 M30,23 L24,29"));
        Add("显示器", "E7F4", Path("M5,4 H27 Q29,4 29,6 V22 Q29,24 27,24 H5 Q3,24 3,22 V6 Q3,4 5,4 Z M16,24 V29 M10,29 H22"));
        Add("扬声器", "E7F5 E92C", Path("M9,3 H23 Q26,3 26,6 V26 Q26,29 23,29 H9 Q6,29 6,26 V6 Q6,3 9,3 Z M20,10 A4,4 0 1 1 12,10 A4,4 0 1 1 20,10 M21,22 A5,5 0 1 1 11,22 A5,5 0 1 1 21,22"));
        Add("耳机", "E7F6", Path("M5,18 V14 A11,11 0 0 1 27,14 V18 M5,16 H9 V27 H6 Q3,27 3,24 V19 Q3,16 5,16 Z M27,16 H23 V27 H26 Q29,27 29,24 V19 Q29,16 27,16 Z"));
        Add("手柄", "E7FC", Path("M11,8 H21 Q26,8 28,14 L30,24 Q30,29 26,29 Q23,29 21,24 H11 Q9,29 6,29 Q2,29 2,24 L4,14 Q6,8 11,8 Z M10,13 V21 M6,17 H14"), Path("M23,14 A1.5,1.5 0 1 1 20,14 A1.5,1.5 0 1 1 23,14 M27,19 A1.5,1.5 0 1 1 24,19 A1.5,1.5 0 1 1 27,19",true));
        Add("网格", "E809", Path("M8,4 H24 L29,28 H3 Z M6,12 H26 M5,20 H27 M13,4 L11,28 M19,4 L21,28"));
        Add("网速", "E753", Path("M7,22 H6 C1,22 1,14 6,12 C5,6 11,2 16,5 C20,2 27,6 26,12 C32,12 32,20 28,22 H27"),
            Path("M11,28 V13 M6,18 L11,13 L16,18 M23,14 V28 M18,23 L23,28 L28,23"));
        Add("首页", "E80F", Path("M2,15 L16,3 L30,15 M6,12 V29 H12 V20 H20 V29 H26 V12"));
        Add("时钟", "E121 E823", Path("M29,16 A13,13 0 1 1 3,16 A13,13 0 1 1 29,16 M16,7 V17 H24"));
        Layer battery = Path("M4,8 H26 Q29,8 29,11 V21 Q29,24 26,24 H4 Q1,24 1,21 V11 Q1,8 4,8 Z M31,13 V19");
        Add("电池空", "E850", battery);
        foreach (var (code, width, title) in new[] {("E852",4,"电池低"),("E853",9,"电池中"),("E855",15,"电池高"),("E83F",21,"电池满")})
            Add(title,code,battery,Path($"M4,11 H{4+width} V21 H4 Z",true));
        Add("电源接入", "E85E EA93 EBA8 EBA9 EBAA EBAB EBAC", battery, Path("M11,5 L18,15 H12 L19,27"));
        Add("上一首", "E892", Path("M7,5 V27 M26,5 L10,16 L26,27 Z"));
        Add("下一首", "E893", Path("M25,5 V27 M6,5 L22,16 L6,27 Z"));
        Add("同步", "E895", Path("M5,13 A11,11 0 0 1 25,8 M25,8 H18 M25,8 V2 M27,19 A11,11 0 0 1 7,24 M7,24 H14 M7,24 V30"));
        Add("下载", "E896", Path("M16,3 V23 M9,16 L16,23 L23,16 M4,28 H28"));
        Add("打开应用", "E8A7", Path("M19,3 H29 V13 M29,3 L16,16 M14,6 H6 Q3,6 3,9 V26 Q3,29 6,29 H23 Q26,29 26,26 V19"));
        Add("分区", "E8A9", Path("M6,3 H26 Q29,3 29,6 V26 Q29,29 26,29 H6 Q3,29 3,26 V6 Q3,3 6,3 Z M16,3 V29 M3,16 H29"));
        Add("小岛", "E8B7", Path("M12,3 H20 Q23,3 23,6 V26 Q23,29 20,29 H12 Q9,29 9,26 V6 Q9,3 12,3 Z"));
        Add("翻译", "E8C1", Path("M3,8 H20 M12,3 V8 M7,8 Q8,17 20,22 M17,8 Q16,17 3,23 M21,14 L15,29 M21,14 L27,29 M17,25 H25"));
        Add("屏幕工具", "E8B5",
            Path("M5,5 H27 Q29,5 29,7 V22 Q29,24 27,24 H5 Q3,24 3,22 V7 Q3,5 5,5 Z M16,24 V29 M11,29 H21"),
            Path("M8,11 V9 H11 M21,9 H24 V11 M8,17 V20 H11 M21,20 H24 V17"));
        Add("剪切", "E8C6", Path("M12,23 A4,4 0 1 1 4,23 A4,4 0 1 1 12,23 M28,23 A4,4 0 1 1 20,23 A4,4 0 1 1 28,23 M10,20 L24,3 M22,20 L8,3"));
        Add("字体", "E8D2", Path("M3,27 L11,5 L19,27 M6,20 H16 M20,27 L25,13 L30,27 M22,23 H28"));
        Add("音乐", "E8D6", Path("M12,23 V6 L28,3 V20 M12,11 L28,8 M12,23 A4,3 0 1 1 4,23 A4,3 0 1 1 12,23 M28,20 A4,3 0 1 1 20,20 A4,3 0 1 1 28,20"));
        Add("密钥", "E8D7", Path("M18,10 A8,8 0 1 1 2,10 A8,8 0 1 1 18,10 M16,15 L29,28 H23 V23 H18 V18 M8,8 A1.5,1.5 0 1 1 5,8 A1.5,1.5 0 1 1 8,8"));
        Add("手机", "E8EA", Path("M10,2 H22 Q25,2 25,5 V27 Q25,30 22,30 H10 Q7,30 7,27 V5 Q7,2 10,2 Z M14,26 H18"));
        Add("番茄钟", "E916", Path("M27,19 A11,11 0 1 1 5,19 A11,11 0 1 1 27,19 M16,8 V3 M12,3 H20 M24,9 L27,6 M16,12 V20 H22"));
        Add("窗口", "E91B E922", Path("M6,4 H26 Q29,4 29,7 V25 Q29,28 26,28 H6 Q3,28 3,25 V7 Q3,4 6,4 Z M3,10 H29"));
        Add("最小化", "E921", Path("M6,22 H26"));
        Add("还原窗口", "E923", Path("M11,8 V3 H29 V21 H24 M5,8 H22 Q24,8 24,10 V27 Q24,29 22,29 H5 Q3,29 3,27 V10 Q3,8 5,8 Z"));
        Add("开发接口", "E943", Path("M12,3 H9 Q6,3 6,7 V11 Q6,16 2,16 Q6,16 6,21 V25 Q6,29 9,29 H12 M20,3 H23 Q26,3 26,7 V11 Q26,16 30,16 Q26,16 26,21 V25 Q26,29 23,29 H20"));
        Add("功率", "E945", Path("M18,2 L6,18 H15 L13,30 L27,13 H18 Z"));
        Add("关于", "E946", Path("M29,16 A13,13 0 1 1 3,16 A13,13 0 1 1 29,16 M16,14 V24 M16,8 V9"));
        Add("CPU", "E950", Path("M9,6 H23 Q26,6 26,9 V23 Q26,26 23,26 H9 Q6,26 6,23 V9 Q6,6 9,6 Z M12,12 H20 V20 H12 Z M12,2 V6 M20,2 V6 M12,26 V30 M20,26 V30 M2,12 H6 M2,20 H6 M26,12 H30 M26,20 H30"));
        Add("鼠标", "E962", Path("M27,13 V20 A11,11 0 0 1 5,20 V13 A11,11 0 0 1 27,13 Z M18,8 V12 A2,2 0 0 1 14,12 V8 A2,2 0 0 1 18,8 Z"));
        Add("投屏", "E968", Path("M12,2 H22 Q24,2 24,4 V21 Q24,23 22,23 H12 Q10,23 10,21 V4 Q10,2 12,2 Z M13,27 H21 M6,30 H28 M17,23 V30 M14,19 H20"));
        Add("助手", "E99A", Path("M7,9 H25 Q28,9 28,12 V23 Q28,26 25,26 H7 Q4,26 4,23 V12 Q4,9 7,9 Z M16,9 V4 M1,14 V21 M31,14 V21 M10,15 H11 M21,15 H22 M11,22 H21"),Path("M17.5,3 A1.5,1.5 0 1 1 14.5,3 A1.5,1.5 0 1 1 17.5,3",true));
        Add("性能监测", "E9D9", Path("M6,3 H26 Q29,3 29,6 V26 Q29,29 26,29 H6 Q3,29 3,26 V6 Q3,3 6,3 Z M6,20 H10 L14,9 L19,24 L23,16 H27"));
        Add("调节", "E9E9", Path("M3,7 H12 M19,7 H29 M3,16 H21 M28,16 H29 M3,25 H6 M13,25 H29 M15,3 Q12,3 12,7 Q12,11 15,11 Q18,11 18,7 Q18,3 15,3 Z M24,12 Q21,12 21,16 Q21,20 24,20 Q27,20 27,16 Q27,12 24,12 Z M9,21 Q6,21 6,25 Q6,29 9,29 Q12,29 12,25 Q12,21 9,21 Z"));
        Add("提示灯", "EA80", Path("M12,24 V21 Q6,18 7,11 A9,9 0 0 1 25,11 Q26,18 20,21 V24 Z M12,28 H20 M14,31 H18"));
        Add("插件", "EA86", Path("M4,5 H11 V3 A4,4 0 0 1 19,3 V5 H27 V12 H29 A4,4 0 0 1 29,20 H27 V28 H19 V26 A4,4 0 0 0 11,26 V28 H4 V20 H6 A4,4 0 0 0 6,12 H4 Z"));
        return new ReadOnlyDictionary<string,Icon>(icons);
    }
}
