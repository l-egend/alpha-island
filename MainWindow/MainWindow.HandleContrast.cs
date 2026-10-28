using System.Windows;
using Point = System.Windows.Point;

namespace alpha_island;

public partial class MainWindow
{
    private bool HandleBackdropNeeded => ComponentHandle.IsVisible && ComponentHandle.ActualHeight >= 26;

    private Rect? ComponentHandleProbe()
    {
        if (!HandleBackdropNeeded) return null;
        try
        {
            double center = ComponentHandle.ActualHeight / 2;
            // Beside the moving stroke, outside the island: sampling the line itself would cause colour feedback.
            return new Rect(ComponentHandle.PointToScreen(new Point(20, center - 3)),
                ComponentHandle.PointToScreen(new Point(24, center + 3)));
        }
        catch (InvalidOperationException) { return null; }
    }
}
