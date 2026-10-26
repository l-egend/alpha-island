using System.Windows;
using Size = System.Windows.Size;

namespace alpha_island.Core;

internal static class DisplayPlacement
{
    internal static Rect TopCentered(Rect workAreaPixels, DpiScale dpi, Size desiredDips)
    {
        double width = Math.Min(workAreaPixels.Width, Math.Max(1, Math.Round(desiredDips.Width * dpi.DpiScaleX)));
        double height = Math.Min(workAreaPixels.Height, Math.Max(1, Math.Round(desiredDips.Height * dpi.DpiScaleY)));
        return new Rect(Math.Round(workAreaPixels.Left + (workAreaPixels.Width - width) / 2),
            workAreaPixels.Top, width, height);
    }
}
