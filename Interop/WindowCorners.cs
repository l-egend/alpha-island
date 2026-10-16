using System;
using System.Runtime.InteropServices;

namespace alpha_island.Interop;

internal static class WindowCorners
{
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);

    internal static void ApplyLegacyRegion(IntPtr window, double radius, double dpiScale)
    {
        if (radius <= 0) { SetWindowRgn(window, IntPtr.Zero, true); return; }
        if (!NativeMethods.GetWindowRect(window, out var bounds)) return;
        int diameter = Math.Max(1, (int)Math.Round(2 * radius * dpiScale));
        var region = CreateRoundRectRgn(0, 0, bounds.Right - bounds.Left + 1, bounds.Bottom - bounds.Top + 1, diameter, diameter);
        // 成功后区域归 Windows 管理；失败才由调用方释放。
        if (region != IntPtr.Zero && SetWindowRgn(window, region, true) == 0) DeleteObject(region);
    }
}
