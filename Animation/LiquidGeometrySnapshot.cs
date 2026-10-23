using System.Windows.Media;

namespace alpha_island.Animation;

/// <summary>Resolve boolean operations once; WPF can then reuse the immutable path and its bounds.</summary>
internal static class LiquidGeometrySnapshot
{
    internal static PathGeometry Combine(Geometry first, Geometry second, GeometryCombineMode mode)
    {
        var result = Geometry.Combine(first, second, mode, null);
        result.Freeze();
        return result;
    }
}
