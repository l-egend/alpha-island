using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace alpha_island.Animation;

internal enum LiquidMotionKind { Split, Merge, Reflow }

/// <summary>Local membrane strain in DIPs. The contact is bounded to one edge, leaving text and the far edge still.</summary>
internal readonly record struct LiquidContact(double Pull, double Squash, double CenterY, double Radius)
{
    public bool Active => Math.Abs(Pull) > .001 || Math.Abs(Squash) > .0001;
    public LiquidContact InLocal(Rect scene, double width, double height)
        => new(Pull * width / scene.Width, Squash, (CenterY - scene.Top) * height / scene.Height, Radius * height / scene.Height);
}

internal readonly record struct LiquidCellFrame(LiquidContact Left, LiquidContact Right, PathGeometry? Neck);

/// <summary>
/// Coupled budding/engulfing shape, sampled from the same clock as the movement. Distance gates contact;
/// the final damped lobe recoil continues after the neck pinches. No timers, pixels, or persistent state.
/// </summary>
internal static class LiquidCellMotion
{
    public static readonly TimeSpan SplitDuration = TimeSpan.FromMilliseconds(480);
    public static readonly TimeSpan MergeDuration = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan ReflowDuration = TimeSpan.FromMilliseconds(280);

    public static LiquidCellFrame Evaluate(Rect left, Rect right, double phase, LiquidMotionKind kind)
    {
        if (!Valid(left) || !Valid(right) || !double.IsFinite(phase) || phase <= 0 || phase >= 1) return default;
        double h = Math.Min(left.Height, right.Height);
        double cyLeft = left.Top + left.Height / 2, cyRight = right.Top + right.Height / 2;
        // A small island beside an expanded card joins the card's upper shoulder, not its distant centre.
        double cy = left.Height <= right.Height ? cyLeft : cyRight;
        if (cy < Math.Max(left.Top, right.Top) - h * .2 || cy > Math.Min(left.Bottom, right.Bottom) + h * .2) return default;
        double gap = right.Left - left.Right;
        if (gap < -h * 1.35 || gap > h * .95 + 12) return default;
        double start = Smooth(phase / .14);
        double end = 1 - Smooth((phase - .78) / .22);
        double proximity = 1 - Smooth((gap - h * .05) / (h * .85));
        double overlap = Smooth((gap + h * .95) / (h * .8));
        double tension = start * end * proximity * (.30 + .70 * overlap);
        double release = 1 - Smooth((phase - (kind == LiquidMotionKind.Merge ? .60 : .42)) / (kind == LiquidMotionKind.Merge ? .34 : .28));
        tension *= release;
        double recoilStart = kind == LiquidMotionKind.Merge ? .65 : .53;
        double recoilPhase = Math.Clamp((phase - recoilStart) / (1 - recoilStart), 0, 1);
        double recoil = -Math.Sin(recoilPhase * Math.PI * 2) * Math.Exp(-recoilPhase * 2.5) * start * end;
        double pull = h * (.28 * tension + .10 * recoil);
        double squash = .26 * tension + .10 * recoil;
        double radius = h * .66;
        var a = new LiquidContact(pull, squash, cy, radius);
        var b = new LiquidContact(pull * 1.13, squash * 1.05, cy, radius);

        // A short, broad membrane rooted inside both deformed caps. There is no long-lived thread.
        double pinch = kind == LiquidMotionKind.Merge ? Smooth((phase - .06) / .28) : 1 - Smooth((phase - .47) / .23);
        double reach = Math.Clamp(h * .46, 12, 24);
        double join = start * end * pinch * (1 - Smooth(Math.Max(0, gap) / reach));
        PathGeometry? neck = null;
        if (join > .018 && gap > -h * .72 && gap < reach)
        {
            double half = Math.Min(h * .39, 17) * join;
            double waist = half * Math.Clamp(.74 - Math.Max(0, gap) / (reach * 1.35), .16, .74);
            double x0 = left.Right - Math.Min(h * .28, 11), x1 = right.Left + Math.Min(h * .28, 11);
            if (x1 > x0 && half > .18)
            {
                double span = x1 - x0, mid = (x0 + x1) / 2;
                var figure = new PathFigure { StartPoint = new Point(x0, cy - half), IsClosed = true, IsFilled = true };
                figure.Segments.Add(new BezierSegment(new(x0 + span * .24, cy - half), new(mid - span * .14, cy - waist), new(mid, cy - waist), true));
                figure.Segments.Add(new BezierSegment(new(mid + span * .14, cy - waist), new(x1 - span * .24, cy - half), new(x1, cy - half), true));
                figure.Segments.Add(new LineSegment(new(x1, cy + half), true));
                figure.Segments.Add(new BezierSegment(new(x1 - span * .24, cy + half), new(mid + span * .14, cy + waist), new(mid, cy + waist), true));
                figure.Segments.Add(new BezierSegment(new(mid - span * .14, cy + waist), new(x0 + span * .24, cy + half), new(x0, cy + half), true));
                figure.Segments.Add(new LineSegment(figure.StartPoint, true));
                neck = new PathGeometry(new[] { figure }) { FillRule = FillRule.Nonzero };
            }
        }
        return new(a, b, neck);
    }

    private static bool Valid(Rect r) => !r.IsEmpty && double.IsFinite(r.X) && double.IsFinite(r.Y)
        && double.IsFinite(r.Width) && double.IsFinite(r.Height) && r.Width > 1 && r.Height > 1;
    internal static double Smooth(double x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }
}