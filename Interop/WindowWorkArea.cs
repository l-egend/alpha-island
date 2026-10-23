using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace alpha_island.Interop;

/// <summary>Fits resizable tool windows to the current monitor in WPF device-independent units.</summary>
internal static class WindowWorkArea
{
    internal static Rect Current(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return SystemParameters.WorkArea;
        var screen = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(window);
        return new Rect(screen.Left / dpi.DpiScaleX, screen.Top / dpi.DpiScaleY,
            screen.Width / dpi.DpiScaleX, screen.Height / dpi.DpiScaleY);
    }

    internal static void Fit(Window window, Rect? workArea = null, double? minimumWidth = null, double? minimumHeight = null)
    {
        if (window.WindowState != WindowState.Normal) return;
        var area = workArea ?? Current(window);
        if (area.IsEmpty || !double.IsFinite(area.Width) || !double.IsFinite(area.Height) || area.Width <= 0 || area.Height <= 0) return;
        double width = Math.Min(area.Width, Math.Max(1, minimumWidth ?? window.MinWidth));
        double height = Math.Min(area.Height, Math.Max(1, minimumHeight ?? window.MinHeight));
        window.MinWidth = width; window.MinHeight = height;
        window.Width = Math.Clamp(double.IsFinite(window.Width) ? window.Width : window.ActualWidth, width, area.Width);
        window.Height = Math.Clamp(double.IsFinite(window.Height) ? window.Height : window.ActualHeight, height, area.Height);
        window.Left = Math.Clamp(double.IsFinite(window.Left) ? window.Left : area.Left + (area.Width - window.Width) / 2,
            area.Left, area.Right - window.Width);
        window.Top = Math.Clamp(double.IsFinite(window.Top) ? window.Top : area.Top + (area.Height - window.Height) / 2,
            area.Top, area.Bottom - window.Height);
    }
}
