using System.Windows;
using System.Windows.Interop;
using alpha_island.Animation;
using alpha_island.Interop;
using Point = System.Windows.Point;

namespace alpha_island;

public partial class MainWindow
{
    private OverlayZOrderGuard? _overlayZOrder;
    private void StartOverlayZOrderGuard(IntPtr hwnd)
    {
        _overlayZOrder = new(hwnd, Dispatcher, () => !_exiting && IsVisible && Topmost,
            () => IslandBody.ActualWidth > 0 && IslandBody.ActualHeight > 0
                ? new Rect(IslandBody.PointToScreen(new Point()), IslandBody.PointToScreen(new Point(IslandBody.ActualWidth, IslandBody.ActualHeight)))
                : Rect.Empty,
            () => _backdrop?.PlaceBelow(hwnd));
        _overlayZOrder.Start();
        IsVisibleChanged += (_, _) => _overlayZOrder?.RequestCheck();
    }
    // Reserve the transparent host once before a morph, then shrink only after it settles.
    // Tall pages and drawers use the same rule; native HWNDs never resize on each frame.
    private static double IslandHostHeight(IslandGeometry geometry) => Math.Max(340, Math.Ceiling(geometry.Top + geometry.Height + 40));
    private void ReserveIslandHost(IslandGeometry geometry)
    {
        double height = IslandHostHeight(geometry);
        if (Height < height) Height = height;
    }
    private void FitIslandHost(IslandGeometry geometry)
    {
        double height = IslandHostHeight(geometry);
        if (Math.Abs(Height - height) > .5) Height = height;
    }
}