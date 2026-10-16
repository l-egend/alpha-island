using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using alpha_island.Core;
using alpha_island.Services;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace alpha_island.Interop;

internal sealed class GameActivityProbe : IGameActivityProbe
{
    internal static string[] ReadWindowsGames()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children", writable: false);
            if (root == null) return [];
            foreach (string name in root.GetSubKeyNames())
            {
                try
                {
                    using var child = root.OpenSubKey(name, writable: false);
                    if (child?.GetValue("Type") is int type && type == 1
                        && child.GetValue("MatchedExeFullPath") is string path && GameIdentityPolicy.IsAbsoluteExe(path)) paths.Add(path);
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { }
        return [.. paths];
    }
    public GameForeground? ReadForeground()
    {
        IntPtr window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero || NativeMethods.IsIconic(window)) return null;
        NativeMethods.GetWindowThreadProcessId(window, out uint pid);
        using var process = OpenProcess(0x1000, false, pid); // Query limited information, without modifying the process.
        string? path = ReadPath(process);
        if (path == null) return null;
        var title = new StringBuilder(256);
        GetWindowText(window, title, title.Capacity);
        bool direct3D = false;
        try { direct3D = SHQueryUserNotificationState(out int state) >= 0 && state == 3 && CoversMonitor(window); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        if (NativeMethods.GetForegroundWindow() != window) return null;
        return new(pid, path, title.ToString(), direct3D);
    }
    private static bool CoversMonitor(IntPtr window)
    {
        if (!NativeMethods.GetWindowRect(window, out var bounds)) return false;
        var info = new NativeMethods.MONITORINFO { Size = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        return NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(window, 2), ref info)
            && Math.Abs(bounds.Left - info.Monitor.Left) <= 2 && Math.Abs(bounds.Top - info.Monitor.Top) <= 2
            && Math.Abs(bounds.Right - info.Monitor.Right) <= 2 && Math.Abs(bounds.Bottom - info.Monitor.Bottom) <= 2;
    }
    public IGameProcessLease? Track(GameIdentity identity)
    {
        var handle = OpenProcess(0x101000, false, identity.ProcessId); // Synchronize + query limited information.
        if (handle.IsInvalid || !identity.Path.Equals(ReadPath(handle), StringComparison.OrdinalIgnoreCase))
        { handle.Dispose(); return null; }
        return new ProcessLease(handle);
    }
    private static string? ReadPath(SafeProcessHandle handle)
    {
        if (handle.IsInvalid) return null;
        var path = new StringBuilder(32768); int size = path.Capacity;
        return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : null;
    }
    private sealed class ProcessLease(SafeProcessHandle handle) : IGameProcessLease
    {
        // A retained kernel handle cannot silently begin referring to a reused PID.
        public bool IsAlive => !handle.IsClosed && !handle.IsInvalid && WaitForSingleObject(handle, 0) != 0;
        public void Dispose() => handle.Dispose();
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
}