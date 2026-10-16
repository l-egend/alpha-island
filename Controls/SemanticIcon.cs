using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using alpha_island.Animation;
using AlphaIsland.PluginSdk;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using FlowDirection = System.Windows.FlowDirection;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Control = System.Windows.Controls.Control;

namespace alpha_island.Controls;

public enum IconShape { Auto, Bluetooth, Wifi, PlayPause, Volume, Battery, Agent, Notification, Download, Clipboard, Timer, Approval, Generic, Weather, Network }

/// <summary>
/// Draws state changes inside a stable 32-unit icon. A single finite WPF clock drives all strokes;
/// the owner, its text and its original logo never rotate or scale. Idle icons have no clock.
/// </summary>
public sealed partial class SemanticIcon : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(SemanticIcon),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(nameof(Shape), typeof(IconShape), typeof(SemanticIcon),
        new FrameworkPropertyMetadata(IconShape.Auto, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ActiveProperty = DependencyProperty.Register(nameof(Active), typeof(bool), typeof(SemanticIcon),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(SemanticIcon),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender, null, (_, value) => Math.Clamp(double.IsFinite((double)value) ? (double)value : 0, 0, 1)));
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(SemanticIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register("Progress", typeof(double), typeof(SemanticIcon),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public IconShape Shape { get => (IconShape)GetValue(ShapeProperty); set => SetValue(ShapeProperty, value); }
    public bool Active { get => (bool)GetValue(ActiveProperty); set => SetValue(ActiveProperty, value); }
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public double TransitionProgress => (double)GetValue(ProgressProperty);
    internal double DisplayedState => ResolvedShape == IconShape.Battery && _preparedBatteryState.HasValue
        ? _preparedBatteryState.Value : _transitioning
        ? Lerp(_fromActive, _toActive, Math.Clamp(TransitionProgress, 0, 1)) : TargetActive ? 1 : 0;
    private bool _transitioning;
    private double? _preparedBatteryState;
    private bool _settledActive = true;
    private double _settledLevel = 1;
    private bool _hasSettled;
    private string _settledText = "";
    private double _fromLock, _toLock;
    private double _fromActive, _fromLevel;
    private double _toActive, _toLevel;
    private double _fromVolumeWaves, _toVolumeWaves;
    private IconShape _targetShape;
    private string _targetText = "";
    private ImageSource? _targetSource;
    private int? _targetWeatherCode;
    private ActionIconAnimation _cue;
    private Brush? _penBrush;
    private Pen? _pen;
    internal bool VolumeStateChanging => ResolvedShape == IconShape.Volume &&
        (_transitioning ? _toActive != (TargetActive ? 1 : 0) : _hasSettled && _settledActive != TargetActive);

    public SemanticIcon()
    {
        Focusable = false;
        IsHitTestVisible = false;
        Foreground = Brushes.White;
    }

    /// <summary>Hold an explicit power event's source until the island has opened.</summary>
    internal void PrepareBatteryConnectionTransition(bool connected)
    {
        EndTransition();
        if (MotionPolicy.InstantMorph || MotionPolicy.PowerSave) return;
        _preparedBatteryState = connected ? 0 : 1;
        InvalidateVisual();
    }

    internal void BeginTransition(ActionIconAnimation cue, AnimationTimeline animation)
    {
        bool redirect = _transitioning;
        double currentActive = Lerp(_fromActive, _toActive, TransitionProgress);
        double currentLevel = Lerp(_fromLevel, _toLevel, TransitionProgress);
        double currentVolumeWaves = Lerp(_fromVolumeWaves, _toVolumeWaves, TransitionProgress);
        double currentLock = Lerp(_fromLock, _toLock, TransitionProgress);
        _cue = cue;
        bool target = TargetActive;
        _fromActive = redirect ? currentActive : _hasSettled ? (_settledActive ? 1 : 0) : target ? 0 : 1;
        if (ResolvedShape == IconShape.Battery && _preparedBatteryState.HasValue)
        {
            _fromActive = _preparedBatteryState.Value;
            _preparedBatteryState = null;
        }
        _fromLevel = redirect ? currentLevel : _hasSettled ? _settledLevel : 0;
        // An action in the same state reveals its own strokes once, without moving the silhouette.
        if (!redirect && ResolvedShape is IconShape.Wifi or IconShape.Bluetooth && _fromActive == (target ? 1 : 0))
            _fromActive = target ? 0 : 1;
        if (!redirect && ResolvedShape == IconShape.Volume && _fromActive == (target ? 1 : 0) && Math.Abs(_fromLevel - Level) < .001)
            _fromActive = target ? 0 : 1;
        _toActive = target ? 1 : 0;
        _toLevel = Level;
        _toLock = Text == "\uE785" || !Active ? 0 : 1;
        _fromLock = redirect ? currentLock : _hasSettled && _settledText is "\uE72E" or "\uE785"
            ? (_settledText == "\uE785" || !_settledActive ? 0 : 1) : 1 - _toLock;
        _fromVolumeWaves = redirect ? currentVolumeWaves : VolumeWaveCount(_fromLevel, _fromActive > .5);
        _toVolumeWaves = VolumeWaveCount(Level, target);
        _targetShape = ResolvedShape;
        _targetText = Text;
        _targetSource = Source;
        _targetWeatherCode = WeatherCode;
        _transitioning = true;
        SetValue(ProgressProperty, 1d);
        AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(this,ProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    internal bool NeedsRetarget(ActionIconAnimation cue) => _transitioning
        && (_toActive != (TargetActive ? 1 : 0) || Math.Abs(_toLevel - Level) > .001
            || _targetShape != ResolvedShape || _targetText != Text || !ReferenceEquals(_targetSource, Source)
            || _targetWeatherCode != WeatherCode || _cue != cue);

    internal void EndTransition()
    {
        AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(this,ProgressProperty, null);
        SetValue(ProgressProperty, 1d);
        _transitioning = false;
        _preparedBatteryState = null;
        _settledActive = TargetActive;
        _settledLevel = Level;
        _settledText = Text;
        _hasSettled = true;
        InvalidateVisual();
    }

    private bool TargetActive => ResolvedShape == IconShape.PlayPause ? Text == "\uE769" || (Text.Length == 0 && Active) : Active;

    public IconShape ResolvedShape => Shape != IconShape.Auto ? Shape : Text switch
    {
        "\uE702" => IconShape.Bluetooth,
        "\uE701" or "\uE6E2" => IconShape.Wifi,
        "\uE768" or "\uE769" or "\uE893" => IconShape.PlayPause,
        "\uE74F" or "\uE992" or "\uE993" or "\uE994" or "\uE995" or "\uE767" => IconShape.Volume,
        "\uE83F" or "\uE850" or "\uE85E" or "\uE945" or "\uEBA8" or "\uEBA9" or "\uEBAA" or "\uEBAB" or "\uEBAC" => IconShape.Battery,
        "\uE99A" => IconShape.Agent,
        "\uE8F2" or "\uE8BD" or "\uE8D6" or "\uE91C" => IconShape.Notification,
        "\uE896" => IconShape.Download,
        "\uE8C8" or "\uE77F" => IconShape.Clipboard,
        "\uE916" or "\uE823" or "\uE121" => IconShape.Timer,
        "\uE73E" or "\uE7BA" => IconShape.Approval,
        "\uE9BD" => IconShape.Weather,
        _ => IconShape.Generic
    };

    protected override Size MeasureOverride(Size constraint) => new(
        double.IsNaN(Width) ? Math.Max(12, FontSize) : Width,
        double.IsNaN(Height) ? Math.Max(12, FontSize) : Height);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double extent = Math.Min(ActualWidth, ActualHeight);
        if (extent <= 0) return;
        dc.PushTransform(new TranslateTransform((ActualWidth - extent) / 2, (ActualHeight - extent) / 2));
        dc.PushTransform(new ScaleTransform(extent / 32, extent / 32));
        Brush brush = Foreground ?? Brushes.White;
        if (_pen == null || !ReferenceEquals(brush, _penBrush))
        {
            _penBrush = brush;
            _pen = new Pen(brush, 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            if (_pen.CanFreeze) _pen.Freeze();
        }
        double p = _transitioning ? Math.Clamp(TransitionProgress, 0, 1) : 1;
        double state = DisplayedState;
        if (Source != null && ResolvedShape == IconShape.Bluetooth)
        {
            DrawBluetoothDevice(dc, state);
        }
        else if (Source != null)
        {
            // Preserve application logos and the selected earphone silhouette. Only the connection
            // strokes or a message corner fold change, never the original image itself.
            bool roundMedia = ResolvedShape == IconShape.PlayPause;
            if (roundMedia) dc.PushClip(new RectangleGeometry(new Rect(3, 3, 26, 26), 6.5, 6.5));
            if (ResolvedShape != IconShape.Agent || !AgentAvatarMotion.HasOverflowVisual(this))
                dc.DrawImage(Source, ResolvedShape == IconShape.Agent ? new Rect(0, 0, 32, 32) : new Rect(3, 3, 26, 26));
            if (roundMedia) dc.Pop();
            if (ResolvedShape == IconShape.PlayPause)
            {
                dc.PushTransform(new TranslateTransform(17, 17));
                dc.DrawRoundedRectangle(Brushes.Black, null, new Rect(0, 0, 15, 15), 4, 4);
                dc.PushTransform(new ScaleTransform(.46, .46));
                DrawPlayPause(dc, state);
                dc.Pop(); dc.Pop();
            }
            else if (_transitioning) DrawMessageFold(dc, p);
        }
        else switch (ResolvedShape)
        {
            case IconShape.PlayPause: DrawPlayPause(dc, state); break;
            case IconShape.Wifi: DrawWifi(dc, state); break;
            case IconShape.Bluetooth: DrawBluetooth(dc, state, p); break;
            case IconShape.Volume: DrawVolume(dc, p); break;
            case IconShape.Battery: DrawBattery(dc, state, p); break;
            case IconShape.Agent: DrawAgent(dc, p); break;
            case IconShape.Notification: DrawNotification(dc, p); break;
            case IconShape.Download: DrawDownload(dc, p); break;
            case IconShape.Clipboard: DrawClipboard(dc, p, false); break;
            case IconShape.Approval: DrawClipboard(dc, p, true); break;
            case IconShape.Timer: DrawTimer(dc, p); break;
            case IconShape.Weather: DrawWeather(dc, p); break;
            case IconShape.Network: DrawNetwork(dc, p); break;
            default: DrawGeneric(dc, p); break;
        }
        dc.Pop(); dc.Pop();
        if (!_transitioning && !_preparedBatteryState.HasValue) { _settledActive = TargetActive; _settledLevel = Level; _settledText = Text; _hasSettled = true; }
    }

    private void DrawPlayPause(DrawingContext dc, double pause)
    {
        // Two polygons share a vertical split of the play triangle; their vertices continuously
        // move into the two pause bars, so there is no crossfade or whole-icon transform.
        Point[] playLeft = [new(8, 5), new(16, 9.63), new(16, 22.37), new(8, 27)];
        Point[] playRight = [new(16, 9.63), new(27, 16), new(27, 16), new(16, 22.37)];
        Point[] pauseLeft = [new(8, 6), new(13, 6), new(13, 26), new(8, 26)];
        Point[] pauseRight = [new(20, 6), new(25, 6), new(25, 26), new(20, 26)];
        Polygon(dc, Interpolate(playLeft, pauseLeft, pause));
        Polygon(dc, Interpolate(playRight, pauseRight, pause));
        if (Text == "\uE893") Stroke(dc, [new(27, 5), new(27, 27)], 1);
    }

    private void DrawWifi(DrawingContext dc, double state)
    {
        DrawWifiBands(dc, state);
    }

    private void DrawBluetooth(DrawingContext dc, double state, double p)
    {
        DrawBluetoothSymbol(dc, state, true);
    }

    private static readonly Point[] VolumeInner = Arc(14, 16, 6, -52, 52);
    private static readonly Point[] VolumeMiddle = Arc(14, 16, 10, -52, 52);
    private static readonly Point[] VolumeOuter = Arc(14, 16, 14, -52, 52);
    private static int VolumeWaveCount(double level, bool active) => !active || level <= 0 ? 0
        : level < .33 ? 1 : level < .66 ? 2 : 3;

    private void DrawVolume(DrawingContext dc, double p)
    {
        Stroke(dc, [new(13, 6), new(7, 11), new(3, 11), new(3, 21), new(7, 21), new(13, 26), new(13, 6)], 1);
        // Stable volume bands show one, two or three full-size waves. Interpolating
        // their count reveals each arc in order; already visible arcs never scale
        // together. Retargeting preserves this fractional count during held input.
        double waves = _transitioning ? Lerp(_fromVolumeWaves, _toVolumeWaves, p) : VolumeWaveCount(Level, TargetActive);
        DrawVolumeWave(dc, VolumeInner, 6, Math.Clamp(waves, 0, 1));
        DrawVolumeWave(dc, VolumeMiddle, 10, Math.Clamp(waves - 1, 0, 1));
        DrawVolumeWave(dc, VolumeOuter, 14, Math.Clamp(waves - 2, 0, 1));
        double muted = _transitioning ? 1 - Lerp(_fromActive, _toActive, p) : (TargetActive ? 0 : 1);
        if (muted > 0)
        {
            dc.PushOpacity(muted);
            Stroke(dc, [new(19, 11), new(29, 21)], 1);
            Stroke(dc, [new(29, 11), new(19, 21)], 1); dc.Pop();
        }
    }

    private void DrawVolumeWave(DrawingContext dc, Point[] arc, double radius, double visibility)
    {
        if (visibility <= 0) return;
        double size = .6 + .4 * visibility;
        dc.PushOpacity(visibility);
        dc.PushTransform(new ScaleTransform(size, size, 14 + radius, 16));
        Stroke(dc, arc, 1);
        dc.Pop(); dc.Pop();
    }

    private void DrawBattery(DrawingContext dc, double charging, double p)
    {
        DrawPlugBattery(dc, charging, p);
    }

    private void DrawAgent(DrawingContext dc, double p)
    {
        dc.DrawRoundedRectangle(null, _pen, new Rect(5, 9, 22, 17), 5, 5);
        Stroke(dc, [new(16, 9), new(16, 4)], 1);
        dc.DrawEllipse(Foreground, null, new Point(16, 3), 1.5, 1.5);
        Stroke(dc, [new(2, 14), new(2, 21)], 1); Stroke(dc, [new(30, 14), new(30, 21)], 1);
        double eye = _transitioning ? Math.Sin(p * Math.PI * 2) * 2 : 0;
        dc.DrawEllipse(Foreground, null, new Point(11 + eye, 15), 1.5, 1.5);
        dc.DrawEllipse(Foreground, null, new Point(21 + eye, 15), 1.5, 1.5);
        if (Text == "\uE783")
            Stroke(dc, [new(12, 23), new(16, 20), new(20, 23)], _transitioning ? p : 1);
        else if (Text == "\uE73E")
            Stroke(dc, [new(11, 21), new(15, 24), new(22, 20)], _transitioning ? Segment(p, .3, .9) : 1);
        else if (!Active)
            Stroke(dc, [new(11, 21), new(16, 23), new(21, 21)], 1);
        else for (int i = 0; i < 3; i++)
        {
            double reveal = _transitioning ? Segment(p, .2 + i * .16, .45 + i * .16) : 1;
            dc.PushOpacity(reveal); dc.DrawEllipse(Foreground, null, new Point(11 + i * 5, 22), 1.2, 1.2); dc.Pop();
        }
    }

    private void DrawNotification(DrawingContext dc, double p)
    {
        dc.DrawRoundedRectangle(null, _pen, new Rect(3, 7, 26, 19), 3, 3);
        // An envelope opens its own flap, reveals the letter, then closes into the resting icon.
        double open = _transitioning ? Math.Sin(Math.PI * p) : 0;
        double apex = Lerp(18, 1, open);
        Stroke(dc, [new(4, 8), new(16, apex), new(28, 8)], 1);
        if (open > .05)
        {
            dc.PushOpacity(open);
            dc.DrawRoundedRectangle(Foreground, null, new Rect(9, 8 - open * 5, 14, 12), 1, 1);
            dc.Pop();
        }
        Stroke(dc, [new(4, 25), new(12, 18)], 1); Stroke(dc, [new(28, 25), new(20, 18)], 1);
    }

    private void DrawMessageFold(DrawingContext dc, double p)
    {
        double reveal = Math.Sin(Math.PI * p);
        dc.PushOpacity(reveal);
        dc.DrawRoundedRectangle(Brushes.Black, _pen, new Rect(18, 19, 13, 11), 3, 3);
        Stroke(dc, [new(21, 23), new(24, 26), new(28, 22)], Segment(p, .15, .6));
        dc.Pop();
    }

    private void DrawDownload(DrawingContext dc, double p)
    {
        Stroke(dc, [new(4, 21), new(4, 28), new(28, 28), new(28, 21)], 1);
        if (Text == "\uE73E")
        {
            Stroke(dc, [new(8, 13), new(14, 19), new(25, 5)], _transitioning ? Segment(p, .15, .9) : 1);
            return;
        }
        if (Text == "\uE769")
        {
            dc.DrawRoundedRectangle(Foreground, null, new Rect(10, 4, 4, 15), 1, 1);
            dc.DrawRoundedRectangle(Foreground, null, new Rect(19, 4, 4, 15), 1, 1);
            return;
        }
        double travel = _transitioning ? Math.Sin(Math.PI * p) * 5 : 0;
        Stroke(dc, [new(16, 3 + travel), new(16, 20 + travel)], 1);
        Stroke(dc, [new(10, 14 + travel), new(16, 20 + travel), new(22, 14 + travel)], 1);
    }

    private void DrawClipboard(DrawingContext dc, double p, bool approval)
    {
        dc.DrawRoundedRectangle(null, _pen, new Rect(6, 6, 20, 23), 3, 3);
        dc.DrawRoundedRectangle(Foreground, null, new Rect(11, 3, 10, 6), 2, 2);
        if (approval) Stroke(dc, [new(11, 18), new(15, 22), new(23, 13)], _transitioning ? Segment(p, .2, .9) : 1);
        else
        {
            Stroke(dc, [new(11, 14), new(21, 14)], _transitioning ? Segment(p, .1, .45) : 1);
            Stroke(dc, [new(11, 19), new(21, 19)], _transitioning ? Segment(p, .35, .7) : 1);
            Stroke(dc, [new(11, 24), new(17, 24)], _transitioning ? Segment(p, .6, .95) : 1);
        }
    }

    private void DrawTimer(DrawingContext dc, double p)
    {
        dc.DrawEllipse(null, _pen, new Point(16, 18), 11, 11);
        Stroke(dc, [new(12, 2), new(20, 2)], 1); Stroke(dc, [new(16, 2), new(16, 6)], 1);
        double angle = _transitioning ? Lerp(-Math.PI, -Math.PI / 2, p) : -Math.PI / 2;
        Stroke(dc, [new(16, 18), new(16 + Math.Cos(angle) * 7, 18 + Math.Sin(angle) * 7)], 1);
        Stroke(dc, [new(16, 18), new(21, 18)], 1);
        if (!Active) Stroke(dc, [new(4, 6), new(28, 29)], _transitioning ? Segment(p, .3, 1) : 1);
    }

    private void DrawGeneric(DrawingContext dc, double p)
    {
        if (Text is "\uE72E" or "\uE785") { DrawLock(dc, p); return; }
        // Host and plugin pages share the same rounded paths. Only unknown third-party glyphs
        // need the installed Windows font; semantic identity stays unchanged.
        if (_transitioning) dc.PushClip(new RectangleGeometry(new Rect(0, 0, 32 * Segment(p, 0, .85), 32)));
        bool drawn = GlyphArtwork.TryDraw(dc, Text, Foreground, _pen!);
        if (_transitioning) dc.Pop();
        if (drawn) return;
        if (Text.Length == 0) return;
        var text = new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), 27, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (_transitioning) dc.PushClip(new RectangleGeometry(new Rect(0, 0, 32 * Segment(p, 0, .85), 32)));
        dc.DrawText(text, new Point((32 - text.Width) / 2, (32 - text.Height) / 2));
        if (_transitioning) dc.Pop();
    }

    private void Stroke(DrawingContext dc, Point[] points, double portion)
    {
        portion = Math.Clamp(portion, 0, 1);
        if (portion <= 0 || points.Length < 2) return;
        double total = 0;
        for (int i = 1; i < points.Length; i++) total += (points[i] - points[i - 1]).Length;
        double remaining = total * portion;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], false, false);
            for (int i = 1; i < points.Length; i++)
            {
                double length = (points[i] - points[i - 1]).Length;
                if (remaining < length) { context.LineTo(Lerp(points[i - 1], points[i], length == 0 ? 0 : remaining / length), true, false); break; }
                context.LineTo(points[i], true, false); remaining -= length;
            }
        }
        geometry.Freeze(); dc.DrawGeometry(null, _pen, geometry);
    }

    private void Polygon(DrawingContext dc, Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], true, true);
            for (int i = 1; i < points.Length; i++) context.LineTo(points[i], true, false);
        }
        geometry.Freeze(); dc.DrawGeometry(Foreground, null, geometry);
    }

    private static Point[] Interpolate(Point[] from, Point[] to, double p)
    {
        var result = new Point[from.Length];
        for (int i = 0; i < result.Length; i++) result[i] = Lerp(from[i], to[i], p);
        return result;
    }
    private static Point[] Arc(double x, double y, double radius, double start, double end)
    {
        var points = new Point[25];
        for (int i = 0; i < points.Length; i++)
        {
            double angle = Lerp(start, end, i / (double)(points.Length - 1)) * Math.PI / 180;
            points[i] = new Point(x + radius * Math.Cos(angle), y + radius * Math.Sin(angle));
        }
        return points;
    }
    private static double Segment(double value, double start, double end) => Math.Clamp((value - start) / (end - start), 0, 1);
    private static double Lerp(double from, double to, double p) => from + (to - from) * p;
    private static Point Lerp(Point from, Point to, double p) => new(Lerp(from.X, to.X, p), Lerp(from.Y, to.Y, p));
}
