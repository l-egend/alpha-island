using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace alpha_island.Animation;

/// <summary>A stable mutable contour shared by content clipping, membrane fill, rim and the native backdrop.</summary>
public sealed class LiquidIslandShape
{
    private readonly PathFigure _figure = new() { IsClosed = true, IsFilled = true };
    private readonly BezierSegment[] _edges = Enumerable.Range(0, 8).Select(_ => new BezierSegment()).ToArray();
    private readonly ArcSegment[] _corners = Enumerable.Range(0, 4).Select(_ => new ArcSegment { SweepDirection = SweepDirection.Clockwise }).ToArray();
    private readonly PathFigure _contactFigure = new() { IsClosed = true, IsFilled = true };
    private readonly BezierSegment[] _contactEdges = Enumerable.Range(0, 12).Select(_ => new BezierSegment()).ToArray();
    private LiquidContact _left, _right;
    private IslandGeometry _last;
    public PathGeometry Geometry { get; } = new() { FillRule = FillRule.Nonzero };
    public double NeckX { get; private set; }
    public double NeckY { get; private set; }
    public long Revision { get; private set; }
    public bool IsDeformed => NeckX > .001 || NeckY > .001 || HasContact;
    internal bool HasContact => _left.Active || _right.Active;

    public LiquidIslandShape()
    {
        for (int i = 0; i < 4; i++) { _figure.Segments.Add(_edges[i * 2]); _figure.Segments.Add(_edges[i * 2 + 1]); _figure.Segments.Add(_corners[i]); }
        foreach (var segment in _contactEdges) _contactFigure.Segments.Add(segment);
        Geometry.Figures.Add(_figure);
    }

    internal void SetContact(LiquidContact left = default, LiquidContact right = default)
    {
        if (_left == left && _right == right) return;
        _left = left; _right = right;
        if (Revision > 0) { Revision++; ApplyContact(); }
    }

    public void Update(IslandGeometry frame, double neckX = 0, double neckY = 0)
    {
        double w = Math.Max(1, frame.Width), h = Math.Max(1, frame.Height);
        neckX = double.IsFinite(neckX) ? Math.Clamp(neckX, 0, 1) : 0;
        neckY = double.IsFinite(neckY) ? Math.Clamp(neckY, 0, 1) : 0;
        if (Revision > 0 && _last.Width == w && _last.Height == h && _last.Corner == frame.Corner && NeckX == neckX && NeckY == neckY) return;
        _last = new(w, h, frame.Top, frame.Corner); NeckX = neckX; NeckY = neckY; Revision++;
        double max = Math.Min(w, h) / 2;
        double tl = Math.Clamp(frame.Corner.TopLeft, 0, max), tr = Math.Clamp(frame.Corner.TopRight, 0, max);
        double br = Math.Clamp(frame.Corner.BottomRight, 0, max), bl = Math.Clamp(frame.Corner.BottomLeft, 0, max);
        double horizontalDip = Math.Min(9, h * .15) * neckX;
        double verticalDip = Math.Min(18, w * .08) * neckY;
        _figure.StartPoint = new Point(tl, 0);
        Edge(0, new(tl, 0), new(w - tr, 0), new(0, horizontalDip)); Corner(0, new(w, tr), tr);
        Edge(1, new(w, tr), new(w, h - br), new(-verticalDip, 0)); Corner(1, new(w - br, h), br);
        Edge(2, new(w - br, h), new(bl, h), new(0, -horizontalDip)); Corner(2, new(0, h - bl), bl);
        Edge(3, new(0, h - bl), new(0, tl), new(verticalDip, 0)); Corner(3, new(tl, 0), tl);
        ApplyContact();
    }

    private void ApplyContact()
    {
        if (!HasContact)
        {
            if (!ReferenceEquals(Geometry.Figures[0], _figure)) Geometry.Figures[0] = _figure;
            return;
        }
        const double k = .5522847498307936;
        double w = _last.Width, h = _last.Height;
        _contactFigure.StartPoint = Warp(_figure.StartPoint);
        for (int side = 0; side < 4; side++)
        {
            for (int j = 0; j < 2; j++)
            {
                var from = _edges[side * 2 + j]; var to = _contactEdges[side * 3 + j];
                to.Point1 = Warp(from.Point1); to.Point2 = Warp(from.Point2); to.Point3 = Warp(from.Point3);
            }
            double r = _corners[side].Size.Width;
            Point end = _corners[side].Point;
            (Point p1, Point p2) = side switch
            {
                0 => (new Point(w - r + k * r, 0), new Point(w, r - k * r)),
                1 => (new Point(w, h - r + k * r), new Point(w - r + k * r, h)),
                2 => (new Point(r - k * r, h), new Point(0, h - r + k * r)),
                _ => (new Point(0, r - k * r), new Point(r - k * r, 0))
            };
            var corner = _contactEdges[side * 3 + 2];
            corner.Point1 = Warp(p1); corner.Point2 = Warp(p2); corner.Point3 = Warp(end);
        }
        if (!ReferenceEquals(Geometry.Figures[0], _contactFigure)) Geometry.Figures[0] = _contactFigure;
    }

    private Point Warp(Point point)
    {
        Point result = point;
        Bend(_left, point.X, -1); Bend(_right, _last.Width - point.X, 1);
        return result;
        void Bend(LiquidContact contact, double edgeDistance, int direction)
        {
            if (!contact.Active) return;
            double reach = Math.Max(1, Math.Min(contact.Radius * 1.6, _last.Width * .82));
            double support = 1 - LiquidCellMotion.Smooth(edgeDistance / reach);
            double dy = point.Y - contact.CenterY;
            double bell = Math.Exp(-dy * dy / Math.Max(1, contact.Radius * contact.Radius * .65));
            result.X += direction * contact.Pull * support * bell;
            result.Y -= dy * contact.Squash * support * bell;
        }
    }

    private void Edge(int index, Point a, Point b, Vector dip)
    {
        var span = b - a; if (span.Length < 1) dip = default;
        var first = _edges[index * 2]; var second = _edges[index * 2 + 1];
        first.Point1 = a + span * .22; first.Point2 = a + span * .34 + dip; first.Point3 = a + span * .5 + dip;
        second.Point1 = a + span * .66 + dip; second.Point2 = a + span * .78; second.Point3 = b;
    }
    private void Corner(int index, Point end, double radius)
    { _corners[index].Point = end; _corners[index].Size = new Size(radius, radius); }
    public static double Pulse(double phase) => phase <= 0 || phase >= 1 ? 0 : Math.Pow(Math.Sin(Math.PI * phase), 2);
}