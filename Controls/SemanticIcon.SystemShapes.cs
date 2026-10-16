using System.Windows;
using System.Windows.Media;
using AlphaIsland.PluginSdk;
using Point = System.Windows.Point;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;

namespace alpha_island.Controls;

public sealed partial class SemanticIcon
{
    private static Geometry Path(string data) { var path=Geometry.Parse(data); path.Freeze(); return path; }
    private static Brush Ink(byte r, byte g, byte b) { var brush=new SolidColorBrush(Color.FromRgb(r,g,b)); brush.Freeze(); return brush; }
    private static readonly Geometry[] WifiBands = [
        Path("M12,23 Q16,19 20,23 L16,27 Z"),
        Path("M8,16 Q16,9 24,16 L21,19 Q16,15 11,19 Z"),
        Path("M3,9 Q16,-2 29,9 L26,12 Q16,4 6,12 Z")];
    private static readonly Geometry BluetoothSymbol=Path("M13,3 V29 L23,21 L6,8 M6,24 L23,11 L13,3");
    private static readonly Geometry RadioSlash=Path("M4,5 L28,28");
    private static readonly Geometry RadioSignal=Path("M27,9 Q32,16 27,23");
    private static readonly Geometry PowerWire=Path("M1,16 H4");
    private static readonly Geometry LockShackle=Path("M10,14 V9 A6,6 0 0 1 22,9 V14");
    private Brush? _detailInk;
    private Pen? _detailPen;
    private static readonly Brush ChargeGreen=Ink(0x32,0xD7,0x74), LowOrange=Ink(0xFF,0xAA,0x3B), CriticalRed=Ink(0xFF,0x5C,0x6A);
    private static readonly Pen ChargeWirePen=GlyphArtwork.CreatePen(ChargeGreen);
    private static readonly Brush[] RadioInks=Enumerable.Range(0,33).Select(i=>
        Ink((byte)Lerp(0xFF,0x22,i/32d),(byte)Lerp(0x5C,0xD3,i/32d),(byte)Lerp(0x6A,0xEE,i/32d))).ToArray();
    private static readonly Pen[] RadioPens=RadioInks.Select(GlyphArtwork.CreatePen).ToArray();

    private void DrawWifiBands(DrawingContext dc, double state)
    {
        // Solid arc sectors reveal from the point outward, and erase in reverse order.
        for(int i=0;i<WifiBands.Length;i++)
        {
            double visible=Segment(state,i*.24,.4+i*.30);
            dc.PushOpacity(.16+.84*visible);
            dc.DrawGeometry(Foreground,null,WifiBands[i]); dc.Pop();
        }
        if(state<1) { dc.PushOpacity(1-state); dc.DrawGeometry(null,_pen,RadioSlash); dc.Pop(); }
    }
    private static void DrawBluetoothSymbol(DrawingContext dc,double state,bool signal)
    {
        int index=(int)Math.Round(Math.Clamp(state,0,1)*32);
        double slash = Segment(1 - state, .12, .92);
        // A transparent gap keeps the strike distinct from the symbol's own diagonal strokes.
        if (slash > 0) dc.PushClip(BluetoothSlashClips.Value[(int)Math.Round(slash * 64)]);
        dc.DrawGeometry(null,RadioPens[index],BluetoothSymbol);
        if (slash > 0) dc.Pop();
        if(signal) { dc.PushOpacity(state); dc.DrawGeometry(null,RadioPens[index],RadioSignal); dc.Pop(); }
        // Draw the off slash from its upper end downwards, rather than fading in a complete line.
        if (slash > 0)
            dc.DrawLine(RadioPens[index], new Point(4, 5), new Point(Lerp(4, 28, slash), Lerp(5, 28, slash)));
    }
    // Small frozen clip masks are reused; drawing a frame creates no mask geometry.
    private static readonly Lazy<Geometry[]> BluetoothSlashClips = new(() => Enumerable.Range(0, 65).Select(i =>
    {
        double length = i / 64d, norm = Math.Sqrt(24 * 24 + 23 * 23);
        double dx = 24 / norm, dy = 23 / norm, nx = -dy * 2.5, ny = dx * 2.5;
        double sx = 4 - dx * 1.4, sy = 5 - dy * 1.4;
        double ex = Lerp(4, 28, length) + dx * 1.4, ey = Lerp(5, 28, length) + dy * 1.4;
        var clip = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var figure = clip.Open())
        {
            figure.BeginFigure(new Point(-4, -4), true, true);
            figure.LineTo(new Point(36, -4), true, false); figure.LineTo(new Point(36, 36), true, false); figure.LineTo(new Point(-4, 36), true, false);
            figure.BeginFigure(new Point(sx + nx, sy + ny), true, true);
            figure.LineTo(new Point(ex + nx, ey + ny), true, false); figure.LineTo(new Point(ex - nx, ey - ny), true, false); figure.LineTo(new Point(sx - nx, sy - ny), true, false);
        }
        clip.Freeze(); return (Geometry)clip;
    }).ToArray());
    private void DrawBluetoothDevice(DrawingContext dc,double state)
    {
        double reveal=Segment(state,.16,.85);
        dc.PushOpacity(reveal); dc.DrawImage(Source,new Rect(2,3,25,25)); dc.Pop();
        // The Bluetooth mark itself moves into the badge; the device stays anchored, without
        // decorative sound waves. Reversing a connection follows exactly the same path back.
        double scale=Lerp(.90,.34,state);
        double x=Lerp(16,26,state), y=Lerp(16,25,state);
        dc.PushTransform(new TranslateTransform(x-16*scale,y-16*scale));
        dc.PushTransform(new ScaleTransform(scale,scale));
        // Device connection progress moves the enabled radio mark; losing an audio connection
        // does not mean Bluetooth itself was switched off. Only radio notices draw the red slash.
        DrawBluetoothSymbol(dc,1,false);
        dc.Pop(); dc.Pop();
    }
    private void DrawPlugBattery(DrawingContext dc, double charging, double progress)
    {
        double level = _transitioning ? Lerp(_fromLevel, _toLevel, progress) : Level;
        Brush fill = level <= .10 ? CriticalRed : level <= .20 ? LowOrange : charging > .88 ? ChargeGreen : Foreground;
        // The body starts centered. Only after it has moved right can the connector enter.
        // Reversing this shared connection coordinate first pulls out the cable, then recenters the body.
        double move = Segment(charging, 0, .35);
        move = move * move * (3 - 2 * move);
        dc.PushTransform(new TranslateTransform(Lerp(-4, 0, move), 0));
        dc.DrawRoundedRectangle(null, _pen, new Rect(11, 9, 18, 14), 3.5, 3.5);
        dc.DrawRoundedRectangle(Foreground, null, new Rect(30, 13, 1.5, 6), .7, .7);
        dc.PushOpacity(.15); dc.DrawRoundedRectangle(Foreground, null, new Rect(13, 11, 14, 10), 2, 2); dc.Pop();
        if (level > 0) dc.DrawRoundedRectangle(fill, null, new Rect(13, 11, Math.Max(.5, 14 * level), 10), 2, 2);
        dc.Pop();
        if (charging <= .35) return;
        double insert = Segment(charging, .35, .88);
        insert = insert * insert * (3 - 2 * insert);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, 32, 32)));
        dc.PushOpacity(Segment(charging, .35, .52));
        dc.PushTransform(new TranslateTransform(Lerp(-12, 0, insert), 0));
        dc.DrawGeometry(null, _pen, PowerWire);
        dc.DrawRoundedRectangle(Foreground, null, new Rect(4, 12, 6, 8), 2, 2);
        dc.DrawRoundedRectangle(Foreground, null, new Rect(10, 14, 2, 4), .8, .8);
        dc.PushOpacity(Segment(charging, .88, 1)); dc.DrawGeometry(null, ChargeWirePen, PowerWire); dc.Pop();
        dc.Pop(); dc.Pop(); dc.Pop();
    }
    private void DrawLock(DrawingContext dc,double progress)
    {
        double locked=_transitioning?Lerp(_fromLock,_toLock,progress):Text=="\uE785"||!Active?0:1;
        if (_detailPen == null || !ReferenceEquals(_detailInk, Foreground)) { _detailInk=Foreground; _detailPen=GlyphArtwork.CreatePen(Foreground); }
        Pen pen=_detailPen;
        dc.DrawRoundedRectangle(null,pen,new Rect(6,14,20,15),4,4);
        // Only the shackle turns at its hinge; the rounded lock body remains still.
        dc.PushTransform(new RotateTransform(Lerp(-24,0,locked),10,14));
        dc.DrawGeometry(null,pen,LockShackle); dc.Pop();
        dc.DrawEllipse(Foreground,null,new Point(16,21),1.3,1.3);
        dc.DrawRoundedRectangle(Foreground,null,new Rect(15.4,21,1.2,3),.6,.6);
    }
}
