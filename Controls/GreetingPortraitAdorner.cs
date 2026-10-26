using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Image = System.Windows.Controls.Image;
using Border = System.Windows.Controls.Border;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace alpha_island.Controls;

internal sealed class GreetingPortraitAdorner(Image image, Border contour, Func<double> opacity, Rect viewbox) : Adorner(image)
{
    internal const double PortraitSize = 64;
    private Geometry? _lowerClip;
    private Rect _clipBounds = Rect.Empty;
    private CornerRadius _clipRadius;
    private readonly ImageBrush _portrait = new()
    {
        ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
        Viewbox = viewbox,
        Stretch = Stretch.Uniform
    };

    protected override void OnRender(DrawingContext context)
    {
        if (image.Source == null || !image.IsVisible) return;
        Geometry clip;
        try { clip = LowerContourClip(); }
        catch (InvalidOperationException) { return; }
        _portrait.ImageSource = image.Source;
        context.PushOpacity(Math.Clamp(opacity(), 0, 1));
        context.PushClip(clip);
        context.DrawRectangle(_portrait, null, new Rect((image.ActualWidth - PortraitSize) / 2, -16, PortraitSize, 56));
        context.Pop();
        context.Pop();
    }

    private Geometry LowerContourClip()
    {
        Rect bounds = contour.TransformToVisual(image).TransformBounds(new Rect(contour.RenderSize));
        if (_lowerClip != null && _clipBounds == bounds && _clipRadius == contour.CornerRadius) return _lowerClip;
        _clipBounds = bounds; _clipRadius = contour.CornerRadius;
        double scaleX = bounds.Width / Math.Max(1, contour.ActualWidth), scaleY = bounds.Height / Math.Max(1, contour.ActualHeight);
        double left = Math.Min(_clipRadius.BottomLeft, contour.ActualHeight / 2), right = Math.Min(_clipRadius.BottomRight, contour.ActualHeight / 2);
        double middle = bounds.Top + bounds.Height / 2, top = Math.Min(bounds.Top - PortraitSize, -PortraitSize);
        var clip = new StreamGeometry();
        using (var path = clip.Open())
        {
            path.BeginFigure(new Point(bounds.Left - PortraitSize, top), isFilled: true, isClosed: true);
            path.LineTo(new Point(bounds.Right + PortraitSize, top), true, false);
            path.LineTo(new Point(bounds.Right + PortraitSize, middle), true, false);
            path.LineTo(new Point(bounds.Right, middle), true, false);
            path.LineTo(new Point(bounds.Right, bounds.Bottom - right * scaleY), true, false);
            path.ArcTo(new Point(bounds.Right - right * scaleX, bounds.Bottom), new Size(right * scaleX, right * scaleY),
                0, false, SweepDirection.Clockwise, true, false);
            path.LineTo(new Point(bounds.Left + left * scaleX, bounds.Bottom), true, false);
            path.ArcTo(new Point(bounds.Left, bounds.Bottom - left * scaleY), new Size(left * scaleX, left * scaleY),
                0, false, SweepDirection.Clockwise, true, false);
            path.LineTo(new Point(bounds.Left, middle), true, false);
            path.LineTo(new Point(bounds.Left - PortraitSize, middle), true, false);
        }
        clip.Freeze(); return _lowerClip = clip;
    }
}
