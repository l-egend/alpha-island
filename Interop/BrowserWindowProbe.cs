using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using alpha_island.Core;

namespace alpha_island.Interop;

internal sealed class BrowserWindowProbe
{
    private uint _processId;
    private string _processName = "";
    private IntPtr _cachedWindow;
    private IntPtr _lastExternalWindow;
    internal IntPtr TrackedWindow => _lastExternalWindow;

    internal BrowserWindowSnapshot Read()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return default;
        NativeMethods.GetWindowThreadProcessId(foreground, out uint pid);
        // Editing a card may activate Alpha Island. Keep the browser rule until
        // another external app becomes foreground, or the browser is restored.
        if (pid == Environment.ProcessId) return ReadWindow(_lastExternalWindow);
        _lastExternalWindow = foreground;
        var snapshot = ReadWindow(foreground);
        return NativeMethods.GetForegroundWindow() == foreground ? snapshot : default;
    }

    internal BrowserWindowSnapshot ReadWindow(IntPtr window, Func<uint, string>? processName = null)
    {
        if (window == IntPtr.Zero || !NativeMethods.IsWindowVisible(window) || NativeMethods.IsIconic(window)) return default;
        NativeMethods.GetWindowThreadProcessId(window, out uint pid);
        if (pid != _processId || window != _cachedWindow || _processName.Length == 0 || processName != null)
        {
            _processId = pid; _cachedWindow = window; _processName = "";
            try
            {
                if (processName != null) _processName = processName(pid);
                else { using var process = Process.GetProcessById((int)pid); _processName = process.ProcessName; }
            }
            catch (Exception error) when (error is Win32Exception or ArgumentException or InvalidOperationException) { }
        }
        if (!BrowserWindowPolicy.IsBrowser(_processName) || !NativeMethods.GetWindowRect(window, out var bounds)) return default;
        var monitor = new NativeMethods.MONITORINFO { Size = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(window, 2), ref monitor)) return default;
        return new(window, _processName, true, false, IsZoomed(window), (monitor.Flags & 1) != 0,
            new Rect(bounds.Left, bounds.Top, Math.Max(0, bounds.Right - bounds.Left), Math.Max(0, bounds.Bottom - bounds.Top)),
            new Rect(monitor.Monitor.Left, monitor.Monitor.Top, monitor.Monitor.Right - monitor.Monitor.Left,
                monitor.Monitor.Bottom - monitor.Monitor.Top));
    }

    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
}
