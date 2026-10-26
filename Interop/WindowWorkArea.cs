using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;
using alpha_island.Core;
using Size = System.Windows.Size;

namespace alpha_island.Interop;

/// <summary>Fits resizable tool windows to the current monitor in WPF device-independent units.</summary>
internal static class WindowWorkArea
{
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativeMethods.POINT point, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);

    internal static DpiScale Dpi(Window window)
    {
        uint dpi = GetDpiForWindow(new WindowInteropHelper(window).Handle);
        return dpi > 0 ? new DpiScale(dpi / 96d, dpi / 96d) : VisualTreeHelper.GetDpi(window);
    }

    internal static Rect PrimaryPixels()
    {
        var info = new NativeMethods.MONITORINFO { Size = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (NativeMethods.GetMonitorInfo(MonitorFromPoint(default, 1), ref info))
            return new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        var area = SystemParameters.WorkArea;
        var primary = System.Windows.Forms.Screen.PrimaryScreen;
        double scale = primary != null && SystemParameters.PrimaryScreenWidth > 0
            ? primary.Bounds.Width / SystemParameters.PrimaryScreenWidth : 1;
        return new Rect(area.X * scale, area.Y * scale, area.Width * scale, area.Height * scale);
    }

    internal static void PlacePrimaryTop(Window window, double maximumWidth)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        // Moving to the new primary can change the HWND's DPI synchronously.
        for (int pass = 0; pass < 2; pass++)
        {
            var dpi = Dpi(window);
            var bounds = DisplayPlacement.TopCentered(PrimaryPixels(), dpi,
                new Size(maximumWidth, window.Height));
            window.Width = bounds.Width / dpi.DpiScaleX;
            ApplyPixels(window, bounds, dpi);
            if (Dpi(window).Equals(dpi)) break;
        }
    }

    internal static void PlacePrimaryCorner(Window window, double marginX, double marginY)
    {
        var dpi = Dpi(window);
        var area = PrimaryPixels();
        double width = Math.Round(window.Width * dpi.DpiScaleX), height = Math.Round(window.Height * dpi.DpiScaleY);
        ApplyPixels(window, new Rect(Math.Max(area.Left, area.Right - width - marginX * dpi.DpiScaleX),
            area.Top + marginY * dpi.DpiScaleY, width, height), dpi);
    }

    private static void ApplyPixels(Window window, Rect bounds, DpiScale dpi)
    {
        window.Left = bounds.Left / dpi.DpiScaleX;
        window.Top = bounds.Top / dpi.DpiScaleY;
        NativeMethods.SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero,
            (int)Math.Round(bounds.Left), (int)Math.Round(bounds.Top), (int)bounds.Width, (int)bounds.Height, 0x0014);
    }

    internal static Rect Current(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return SystemParameters.WorkArea;
        var info = new NativeMethods.MONITORINFO { Size = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(handle, 2), ref info)) return SystemParameters.WorkArea;
        var dpi = Dpi(window);
        return new Rect(info.Work.Left / dpi.DpiScaleX, info.Work.Top / dpi.DpiScaleY,
            (info.Work.Right - info.Work.Left) / dpi.DpiScaleX, (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY);
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
