using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Pen = System.Windows.Media.Pen;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace alpha_island.Controls;

public sealed class IslandContourHandle : FrameworkElement
{
    internal const double TrackWidth = 34;
    internal const double ContourGap = 8;
    private const double HitStrokeWidth = 30;
    public static readonly DependencyProperty ContourCornerRadiusProperty = DependencyProperty.Register(
        nameof(ContourCornerRadius), typeof(CornerRadius), typeof(IslandContourHandle),
        new FrameworkPropertyMetadata(new CornerRadius(24), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(IslandContourHandle),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty EngagedProperty = DependencyProperty.Register(
        nameof(Engaged), typeof(bool), typeof(IslandContourHandle),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    private bool _hovered;

    public CornerRadius ContourCornerRadius { get => (CornerRadius)GetValue(ContourCornerRadiusProperty); set => SetValue(ContourCornerRadiusProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public bool Engaged { get => (bool)GetValue(EngagedProperty); set => SetValue(EngagedProperty, value); }

    public IslandContourHandle()
    {
        Cursor = Cursors.Hand;
        Focusable = true;
        System.Windows.Automation.AutomationProperties.SetName(this, "切换组件");
        ToolTip = "单击下一卡片 · 滚轮切换 · 长按移向图标，松手确认 · 移远或 Esc 取消";
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

    internal bool ContainsHandlePoint(Point point) => new Rect(RenderSize).Contains(point)
        && BuildContour(RenderSize, ContourCornerRadius).StrokeContains(HitPen(Brushes.White), point);

    private static Pen HitPen(Brush brush) => new(brush, HitStrokeWidth)
    { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

    protected override HitTestResult? HitTestCore(PointHitTestParameters parameters) => ContainsHandlePoint(parameters.HitPoint)
        ? new PointHitTestResult(this, parameters.HitPoint) : null;

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var pen = new Pen(Stroke, Engaged || _hovered || IsKeyboardFocused ? 2.8 : 2.2)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var contour = BuildContour(RenderSize, ContourCornerRadius);
        drawingContext.DrawGeometry(null, HitPen(Brushes.Transparent), contour);
        drawingContext.PushOpacity(Engaged || _hovered || IsKeyboardFocused ? 1 : .72);
        drawingContext.DrawGeometry(null, pen, contour);
        drawingContext.Pop();
    }

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); _hovered = true; InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); _hovered = false; InvalidateVisual(); }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
}
