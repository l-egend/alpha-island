using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
using alpha_island.Core;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;

namespace alpha_island.Controls;

/// <summary>Only a recognizable device name selects a particular audio-device shape.</summary>
public enum BluetoothAudioForm { Generic, InEar, EarClip, Headphones, Speaker, OpenEar }

/// <summary>
/// Six shared, frozen vector images replace the continuously rotating device mesh.
/// Bootstrap SVG attribution is in Assets/Bluetooth/SOURCES.md; only those embedded,
/// path-only assets are decoded, once per form. No device image is downloaded at runtime.
/// </summary>
internal static class BluetoothDeviceArtwork
{
    private static readonly Brush Ink = FrozenInk();
    private static readonly Lazy<DrawingImage>[] Images = Enum.GetValues<BluetoothAudioForm>()
        .Select(form => new Lazy<DrawingImage>(() => Create(form))).ToArray();

    internal static BluetoothAudioForm Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return BluetoothAudioForm.Generic;
        string identity = string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        bool Has(params string[] words) => words.Any(identity.Contains);

        // More specific identities precede families: AirPods Max is not an earbud,
        // and FreeClip / Enco Clip are not the brands' in-ear families.
        if (Has("airpodsmax", "wh1000"))
            return BluetoothAudioForm.Headphones;
        if (Has("freeclip", "encoclip", "earclip", "cliponearbud", "耳夹", "夹耳"))
            return BluetoothAudioForm.EarClip;
        if (Has("speaker", "音箱", "音响", "jblflip", "jblcharge", "jblxtreme"))
            return BluetoothAudioForm.Speaker;
        if (Has("openfit", "aerofit", "openrun", "openmove", "openear", "boneconduction", "骨传导", "开放式", "不入耳"))
            return BluetoothAudioForm.OpenEar;
        if (Has("airpods", "freebuds", "galaxybuds", "wf1000"))
            return BluetoothAudioForm.InEar;
        if (Has("headphones", "headphone", "overear", "头戴", "头带", "耳罩"))
            return BluetoothAudioForm.Headphones;
        if (Has("earbud", "inear", "tws", "入耳", "耳塞")) return BluetoothAudioForm.InEar;
        return BluetoothAudioForm.Generic;
    }

    internal static ImageSource For(string? name) => Images[(int)Resolve(name)].Value;

    private static DrawingImage Create(BluetoothAudioForm form)
    {
        if (form == BluetoothAudioForm.OpenEar) return OriginalOpenEar();
        string asset = form switch
        {
            BluetoothAudioForm.InEar => "earbuds.svg",
            BluetoothAudioForm.EarClip => "earclip.svg",
            BluetoothAudioForm.Headphones => "headphones.svg",
            BluetoothAudioForm.Speaker or BluetoothAudioForm.Generic => "speaker.svg",
            _ => throw new ArgumentOutOfRangeException(nameof(form))
        };
        try { return ReadEmbeddedSvg(asset); }
        catch (Exception error) when (error is IOException or XmlException or FormatException or InvalidOperationException or NotSupportedException)
        {
            // A damaged package must not turn a harmless Bluetooth connection into a crash.
            IslandLog.Warn("BluetoothArtwork", $"二维素材 {asset} 无法读取，采用通用音频图标: {error.Message}");
            return OriginalAudioSymbol();
        }
    }

    private static DrawingImage ReadEmbeddedSvg(string asset)
    {
        var uri = new Uri($"/alpha-island;component/Assets/Bluetooth/{asset}", UriKind.Relative);
        using var stream = Application.GetResourceStream(uri)?.Stream
            ?? throw new IOException($"Missing embedded asset: {asset}");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var root = XDocument.Load(reader).Root ?? throw new FormatException("SVG root is missing.");
        if (root.Name.LocalName != "svg" || root.Elements().Any(element => element.Name.LocalName != "path"))
            throw new FormatException("The embedded artwork must contain only SVG paths.");
        var values = ((string?)root.Attribute("viewBox") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (values.Length != 4 || values.Any(value => !double.IsFinite(value)) || values[2] <= 0 || values[3] <= 0)
            throw new FormatException("SVG viewBox is invalid.");
        var paths = new DrawingGroup
        {
            Transform = new MatrixTransform(32 / values[2], 0, 0, 32 / values[3], -values[0] * 32 / values[2], -values[1] * 32 / values[3])
        };
        foreach (var path in root.Elements())
        {
            string data = (string?)path.Attribute("d") ?? throw new FormatException("SVG path data is missing.");
            string fillRule = (string?)path.Attribute("fill-rule") ?? (string?)root.Attribute("fill-rule") ?? "nonzero";
            var geometry = Geometry.Parse((fillRule == "evenodd" ? "F0 " : "F1 ") + data);
            geometry.Freeze();
            paths.Children.Add(new GeometryDrawing(Ink, null, geometry));
        }
        if (paths.Children.Count == 0) throw new FormatException("SVG has no paths.");
        var drawing = Canvas(); drawing.Children.Add(paths);
        return Freeze(drawing);
    }

    private static DrawingImage OriginalAudioSymbol()
    {
        var drawing = Canvas();
        var pen = new Pen(Ink, 2.3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        drawing.Children.Add(new GeometryDrawing(null, pen, Geometry.Parse("M9,3 H23 Q26,3 26,6 V26 Q26,29 23,29 H9 Q6,29 6,26 V6 Q6,3 9,3 Z M20,10 A4,4 0 1 1 12,10 A4,4 0 1 1 20,10 M21,22 A5,5 0 1 1 11,22 A5,5 0 1 1 21,22")));
        return Freeze(drawing);
    }

    private static DrawingImage OriginalOpenEar()
    {
        var drawing = Canvas();
        var pen = new Pen(Ink, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        // Generic open hooks and side-mounted speakers; no in-ear tips or brand shape.
        drawing.Children.Add(new GeometryDrawing(null, pen, Geometry.Parse("M12,9 C12,2 3,3 3,11 C3,19 8,24 13,23 M20,9 C20,2 29,3 29,11 C29,19 24,24 19,23")));
        drawing.Children.Add(new GeometryDrawing(Ink, null, new RectangleGeometry(new Rect(8, 9, 6, 9), 2.8, 2.8)));
        drawing.Children.Add(new GeometryDrawing(Ink, null, new RectangleGeometry(new Rect(18, 9, 6, 9), 2.8, 2.8)));
        return Freeze(drawing);
    }

    private static DrawingGroup Canvas()
    {
        var drawing = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(0, 0, 32, 32)) };
        // Equal view boxes avoid icon size changes when the device category changes.
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 32, 32))));
        return drawing;
    }
    private static DrawingImage Freeze(DrawingGroup drawing)
    {
        drawing.Freeze(); var image = new DrawingImage(drawing); image.Freeze(); return image;
    }
    private static Brush FrozenInk()
    {
        var ink = new SolidColorBrush(Color.FromRgb(0xE9, 0xF6, 0xFF)); ink.Freeze(); return ink;
    }
}
