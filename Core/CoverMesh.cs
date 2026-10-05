using System;
using System.Windows;
using System.Windows.Media.Media3D;
using Point = System.Windows.Point;

namespace alpha_island.Core;

internal static class CoverMesh
{
    internal static MeshGeometry3D RoundedSquare(double halfSize, double radius)
    {
        if (!double.IsFinite(halfSize) || halfSize <= 0 || !double.IsFinite(radius) || radius <= 0 || radius > halfSize)
            throw new ArgumentOutOfRangeException(nameof(radius));
        var mesh = new MeshGeometry3D();
        mesh.Positions.Add(new Point3D(0, 0, 0)); mesh.TextureCoordinates.Add(new Point(0.5, 0.5));
        const int segments = 8;
        var centers = new[] { (halfSize - radius, -halfSize + radius, 270),
            (halfSize - radius, halfSize - radius, 0), (-halfSize + radius, halfSize - radius, 90),
            (-halfSize + radius, -halfSize + radius, 180) };
        foreach (var (x, y, start) in centers)
            for (int i = 0; i <= segments; i++)
            {
                double angle = (start + 90d * i / segments) * Math.PI / 180;
                double px = x + radius * Math.Cos(angle), py = y + radius * Math.Sin(angle);
                mesh.Positions.Add(new Point3D(px, py, 0));
                mesh.TextureCoordinates.Add(new Point((px + halfSize) / (2 * halfSize), (halfSize - py) / (2 * halfSize)));
            }
        int count = mesh.Positions.Count - 1;
        for (int i = 1; i <= count; i++)
        { mesh.TriangleIndices.Add(0); mesh.TriangleIndices.Add(i); mesh.TriangleIndices.Add(i == count ? 1 : i + 1); }
        mesh.Freeze(); return mesh;
    }
}
