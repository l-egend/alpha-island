using System.Windows;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace alpha_island.Core;

internal sealed record ComponentMenuLayout(Size Size, Point Anchor, IReadOnlyList<Point> Centers, bool Left, bool Up)
{
    public const double Diameter = 34;
    public const double CaptionSpace = 128;
    public const double HandleOffset = 4;
    public const double CancelMargin = 32;

    public bool ContainsGesturePoint(Point point)
    {
        var delta = point - Anchor;
        double radius = Centers.Count == 0 ? 0 : Centers.Max(center => (center - Anchor).Length);
        var bounds = new Rect(Anchor, Anchor);
        foreach (var center in Centers) bounds.Union(center);
        bounds.Inflate(Diameter / 2 + CancelMargin, Diameter / 2 + CancelMargin);
        return bounds.Contains(point) && (Left ? -delta.X : delta.X) >= -CancelMargin
            && delta.Length <= radius + Diameter / 2 + CancelMargin;
    }

    // Keep icons in nearby columns centered on the handle; rounded islands bend the columns along its contour.
    public static ComponentMenuLayout Create(int count, bool left = false, bool up = false,
        double contourHeight = 48, double cornerRadius = 24, double verticalBias = 0)
    {
        count = Math.Max(0, count);
        int rows = Math.Min(6, Math.Max(1, count));
        double roundness = Math.Clamp((2 * cornerRadius + 44 - contourHeight) / 44, 0, 1);
        const double padding = Diameter / 2 + 7;
        var offsets = new List<Point>(count);
        for (int i = 0; i < count; i++)
        {
            int column = i / rows, row = i % rows;
            int columnCount = Math.Min(rows, count - column * rows);
            row += (rows - columnCount) / 2;
            double y = (row - (rows - 1) / 2.0) * 40;
            double t = y / ((rows - 1) * 20 + 20);
            double bend = roundness * (24 * (1 - t * t) - 20);
            offsets.Add(new Point(40 + column * 44 + bend, (up ? -y : y) + verticalBias));
        }
        double extentX = offsets.Count > 0 ? offsets.Max(p => p.X) : 0;
        double minY = Math.Min(-padding, offsets.Count > 0 ? offsets.Min(p => p.Y) - padding : -padding);
        double maxY = Math.Max(padding, offsets.Count > 0 ? offsets.Max(p => p.Y) + padding : padding);
        var size = new Size(extentX + padding * 2 + CaptionSpace, maxY - minY);
        Point anchor = new(left ? extentX + padding : padding + CaptionSpace, -minY);
        var centers = offsets.Select(p => new Point(anchor.X + (left ? -p.X : p.X), anchor.Y + p.Y)).ToArray();
        return new(size, anchor, centers, left, up);
    }
}
