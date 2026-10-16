using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace alpha_island.Controls;

/// <summary>Clips full bleed material and payloads to the inside of their rounded border.</summary>
public sealed class RoundedClipGrid : Grid
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(RoundedClipGrid),
        new FrameworkPropertyMetadata(new CornerRadius(), OnOutlineChanged));
    public static readonly DependencyProperty BorderInsetProperty = DependencyProperty.Register(
        nameof(BorderInset), typeof(Thickness), typeof(RoundedClipGrid),
        new FrameworkPropertyMetadata(new Thickness(), OnOutlineChanged));

    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public Thickness BorderInset { get => (Thickness)GetValue(BorderInsetProperty); set => SetValue(BorderInsetProperty, value); }

    private Size _clipSize;
    private CornerRadius _clipCorner;
    private Thickness _clipInset;

    private static void OnOutlineChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
        => ((RoundedClipGrid)owner).UpdateClip();

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateClip();
    }

    private void UpdateClip()
    {
        var size = RenderSize;
        var corner = CornerRadius;
        var inset = BorderInset;
        if (Clip != null && size == _clipSize && corner == _clipCorner && inset == _clipInset) return;
        _clipSize = size; _clipCorner = corner; _clipInset = inset;
        if (size.Width <= 0 || size.Height <= 0) { Clip = Geometry.Empty; return; }

        // The grid occupies the border's inner box. Its inner arcs can be elliptical
        // when the border thickness differs between the horizontal and vertical sides.
        double tlx = Math.Max(0, corner.TopLeft - inset.Left), tly = Math.Max(0, corner.TopLeft - inset.Top);
        double trx = Math.Max(0, corner.TopRight - inset.Right), tryy = Math.Max(0, corner.TopRight - inset.Top);
        double brx = Math.Max(0, corner.BottomRight - inset.Right), bry = Math.Max(0, corner.BottomRight - inset.Bottom);
        double blx = Math.Max(0, corner.BottomLeft - inset.Left), bly = Math.Max(0, corner.BottomLeft - inset.Bottom);
        double scale = 1;
        void Fit(double edge, double sum) { if (sum > edge) scale = Math.Min(scale, edge / sum); }
        Fit(size.Width, tlx + trx); Fit(size.Width, blx + brx);
        Fit(size.Height, tly + bly); Fit(size.Height, tryy + bry);
        tlx *= scale; tly *= scale; trx *= scale; tryy *= scale;
        brx *= scale; bry *= scale; blx *= scale; bly *= scale;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            void Arc(Point end, double rx, double ry)
            {
                if (rx <= 0 || ry <= 0) context.LineTo(end, true, false);
                else context.ArcTo(end, new Size(rx, ry), 0, false, SweepDirection.Clockwise, true, false);
            }
            context.BeginFigure(new Point(tlx, 0), true, true);
            context.LineTo(new Point(size.Width - trx, 0), true, false);
            Arc(new Point(size.Width, tryy), trx, tryy);
            context.LineTo(new Point(size.Width, size.Height - bry), true, false);
            Arc(new Point(size.Width - brx, size.Height), brx, bry);
            context.LineTo(new Point(blx, size.Height), true, false);
            Arc(new Point(0, size.Height - bly), blx, bly);
            context.LineTo(new Point(0, tly), true, false);
            Arc(new Point(tlx, 0), tlx, tly);
        }
        geometry.Freeze();
        Clip = geometry;
    }
}
