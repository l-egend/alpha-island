using System.IO;
using System.Windows;

namespace alpha_island.Core;

internal readonly record struct BrowserWindowSnapshot(IntPtr Window, string ProcessName, bool Visible,
    bool Minimized, bool Maximized, bool OnPrimaryMonitor, Rect Bounds, Rect MonitorBounds);

internal static class BrowserWindowPolicy
{
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "chromium", "arc",
        "zen", "floorp", "waterfox", "librewolf", "duckduckgo", "browser", "360chrome",
        "360se", "360chromeX", "qqbrowser", "sogouexplorer"
    };

    internal static bool IsBrowser(string processName) =>
        Browsers.Contains(Path.GetFileNameWithoutExtension(processName));

    internal static bool ShouldCompact(BrowserWindowSnapshot window)
    {
        if (window.Window == IntPtr.Zero || !window.Visible || window.Minimized || !window.OnPrimaryMonitor
            || !IsBrowser(window.ProcessName) || window.Bounds.IsEmpty || window.MonitorBounds.IsEmpty
            || window.Bounds.Width <= 0 || window.Bounds.Height <= 0) return false;
        if (window.Maximized) return true;
        var bounds = window.Bounds; var monitor = window.MonitorBounds;
        return Math.Abs(bounds.Left - monitor.Left) <= 2 && Math.Abs(bounds.Top - monitor.Top) <= 2
            && Math.Abs(bounds.Right - monitor.Right) <= 2 && Math.Abs(bounds.Bottom - monitor.Bottom) <= 2;
    }
}
