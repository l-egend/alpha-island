using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using alpha_island.Animation;
using AlphaIsland.PluginSdk;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Pen = System.Windows.Media.Pen;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace alpha_island.Controls;

public sealed class IslandContourHandle : FrameworkElement
{
    internal const double TrackWidth = 28;
    internal const double ContourGap = 8;
    private const double VerticalHitPadding = 8;
    private static readonly Brush InputSurface = CreateInputSurface();
    private static readonly DependencyProperty HintOffsetProperty = DependencyProperty.Register("HintOffset", typeof(double), typeof(IslandContourHandle),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ContourCornerRadiusProperty = DependencyProperty.Register(
        nameof(ContourCornerRadius), typeof(CornerRadius), typeof(IslandContourHandle),
        new FrameworkPropertyMetadata(new CornerRadius(24), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(IslandContourHandle),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty EngagedProperty = DependencyProperty.Register(
        nameof(Engaged), typeof(bool), typeof(IslandContourHandle),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender,
            (owner, change) => { if ((bool)change.NewValue) ((IslandContourHandle)owner).StopHint(); }));
    private bool _hovered;
    internal static readonly DependencyProperty BackdropColorProperty = DependencyProperty.Register(
        "BackdropColor", typeof(Color), typeof(IslandContourHandle),
        new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender));
    internal static readonly Color LightBackdropBlue = Color.FromRgb(0, 70, 140);
    private Color? _backdropTarget;
    private SolidColorBrush? _backdropBrush;
    private Geometry? _contour;
    private Size _contourSize;
    private CornerRadius _contourCorner;
    private Brush? _lineBrush;
    private Pen? _normalPen, _highlightPen;
    private long _lastHintTick = -2000;
    private int _hintGeneration;

    public CornerRadius ContourCornerRadius { get => (CornerRadius)GetValue(ContourCornerRadiusProperty); set => SetValue(ContourCornerRadiusProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public bool Engaged { get => (bool)GetValue(EngagedProperty); set => SetValue(EngagedProperty, value); }
    internal double HintOffset => (double)GetValue(HintOffsetProperty);
    internal bool IsHintAnimating { get; private set; }

    public IslandContourHandle()
    {
        Cursor = Cursors.Hand;
        Focusable = true;
        System.Windows.Automation.AutomationProperties.SetName(this, "切换组件");
        ToolTip = "单击下一卡片 · 滚轮切换 · 长按移向图标，松手确认 · 移远或 Esc 取消";
        Loaded += (_, _) => { MotionPolicy.Changed += OnMotionPolicyChanged; PlayHint(); };
        Unloaded += (_, _) => { MotionPolicy.Changed -= OnMotionPolicyChanged; StopHint(); FinishBackdropTransition(); };
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) PlayHint(); else { StopHint(); FinishBackdropTransition(); } };
        SizeChanged += (_, e) => { if (e.PreviousSize.Height < 26 && e.NewSize.Height >= 26) PlayHint(); };
    }

    internal void AdaptToBackdrop(double brightness, bool animate = true)
    {
        if (!double.IsFinite(brightness) || brightness is < 0 or > 1) return;
        bool wasBlue = _backdropTarget == LightBackdropBlue;
        var color = brightness > (wasBlue ? .28 : .31) ? LightBackdropBlue : Colors.White;
        if (_backdropTarget == color) return;
        var previous = (Color)GetValue(BackdropColorProperty);
        _backdropTarget = color;
        AnimationClockLifetime.Begin(this, BackdropColorProperty, null);
        bool transition = animate && IsVisible && !MotionPolicy.InstantMorph && !MotionPolicy.PowerSave;
        // This private property has no binding. A local base value survives replacement of its animation clock.
        SetValue(BackdropColorProperty, transition ? previous : color);
        if (transition)
            AnimationClockLifetime.Begin(this, BackdropColorProperty,
                new ColorAnimation(previous, color, TimeSpan.FromMilliseconds(480))
                { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
    }

    private void FinishBackdropTransition()
    {
        AnimationClockLifetime.Begin(this, BackdropColorProperty, null);
        if (_backdropTarget is Color color) SetValue(BackdropColorProperty, color);
    }

    internal static Geometry BuildContour(Size size, CornerRadius corner)
    {
        if (size.Width < TrackWidth || size.Height < 26) return Geometry.Empty;
        // Local x=0 is the island's right edge. Offset the rounded contour outward by a constant gap.
        double radiusTop = Math.Clamp(corner.TopRight, 0, size.Height / 2);
        double radiusBottom = Math.Clamp(corner.BottomRight, 0, size.Height / 2);
        double length = Math.Clamp(size.Height * .70, 18, 44);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (int i = 0; i <= 32; i++)
            {
                double y = size.Height / 2 - length / 2 + length * i / 32;
                double x = ContourGap;
                if (y < radiusTop && radiusTop > 0)
                {
                    double dy = radiusTop - y;
                    double outerRadius = radiusTop + ContourGap;
                    x = Math.Sqrt(Math.Max(0, outerRadius * outerRadius - dy * dy)) - radiusTop;
                }
                else if (y > size.Height - radiusBottom && radiusBottom > 0)
                {
                    double dy = y - (size.Height - radiusBottom);
                    double outerRadius = radiusBottom + ContourGap;
                    x = Math.Sqrt(Math.Max(0, outerRadius * outerRadius - dy * dy)) - radiusBottom;
                }
                if (i == 0) context.BeginFigure(new Point(x, y), false, false);
                else context.LineTo(new Point(x, y), true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private Geometry CurrentContour
    {
        get
        {
            if (_contour == null || _contourSize != RenderSize || _contourCorner != ContourCornerRadius)
            {
                _contourSize = RenderSize; _contourCorner = ContourCornerRadius;
                _contour = BuildContour(_contourSize, _contourCorner);
            }
            return _contour;
        }
    }

    internal Rect HitBounds
    {
        get
        {
            // Keep the target around the visible contour rather than the entire card edge.
            // It follows island geometry, while the separate hint offset never moves it.
            var bounds = CurrentContour.Bounds;
            return bounds.IsEmpty ? Rect.Empty : new Rect(0, bounds.Top - VerticalHitPadding,
                TrackWidth, bounds.Height + VerticalHitPadding * 2);
        }
    }

    internal bool ContainsHandlePoint(Point point) => HitBounds.Contains(point);

    private static Brush CreateInputSurface()
    {
        // Layered windows skip alpha-zero pixels before WM_NCHITTEST reaches WPF.
        // One alpha step preserves real mouse delivery without a perceptible background.
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0));
        brush.Freeze(); return brush;
    }

    private static Pen MakePen(Brush brush, double width)
    {
        var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (pen.CanFreeze) pen.Freeze(); return pen;
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters parameters) => ContainsHandlePoint(parameters.HitPoint)
        ? new PointHitTestResult(this, parameters.HitPoint) : null;

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        Brush stroke = Stroke;
        if (_backdropTarget.HasValue)
        {
            var color = (Color)GetValue(BackdropColorProperty);
            if (_backdropBrush == null || _backdropBrush.Color != color) _backdropBrush = new SolidColorBrush(color);
            stroke = _backdropBrush;
        }
        if (!ReferenceEquals(_lineBrush, stroke))
        {
            _lineBrush = stroke; _normalPen = MakePen(stroke, 2.2); _highlightPen = MakePen(stroke, 2.8);
        }
        bool highlight = Engaged || _hovered || IsKeyboardFocused;
        var contour = CurrentContour;
        if (!HitBounds.IsEmpty) drawingContext.DrawRectangle(InputSurface, null, HitBounds);
        // Only the visible stroke moves. Native and WPF input keep the same generous target.
        drawingContext.PushTransform(new TranslateTransform(HintOffset, 0));
        drawingContext.DrawGeometry(null, highlight ? _highlightPen : _normalPen, contour);
        drawingContext.Pop();
    }

    internal void PlayHint()
    {
        if (!IsLoaded || !IsVisible || ActualHeight < 26 || Engaged || IsHintAnimating
            || MotionPolicy.InstantMorph || MotionPolicy.PowerSave || Environment.TickCount64 - _lastHintTick < 1400) return;
        StopHint(); _lastHintTick = Environment.TickCount64;
        int generation = _hintGeneration;
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1100), FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(350)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100)), new QuadraticEase { EasingMode = EasingMode.EaseInOut }));
        animation.Completed += (_, _) => { if (generation == _hintGeneration) StopHint(); };
        IsHintAnimating = true;
        AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(this,HintOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    internal void StopHint()
    {
        ++_hintGeneration; IsHintAnimating = false;
        AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(this,HintOffsetProperty, null); SetValue(HintOffsetProperty, 0d);
    }

    private void OnMotionPolicyChanged() { if (MotionPolicy.InstantMorph || MotionPolicy.PowerSave) { StopHint(); FinishBackdropTransition(); } }
    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); _hovered = true; PlayHint(); InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); _hovered = false; InvalidateVisual(); }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); StopHint(); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
}
