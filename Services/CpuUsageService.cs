using System;
using System.Runtime.InteropServices;

namespace alpha_island.Services;

public sealed class CpuUsageService
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    private long _idle, _kernel, _user;
    private bool _primed;
    public double? CurrentPercent { get; private set; }
    public void Sample()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) { _primed = false; CurrentPercent = null; return; }
        CurrentPercent = _primed ? Calculate(idle - _idle, kernel - _kernel, user - _user) : null;
        _idle = idle; _kernel = kernel; _user = user; _primed = true;
    }
    internal static double? Calculate(long idle, long kernel, long user)
    {
        double total = (double)kernel + user;
        if (idle < 0 || kernel < 0 || user < 0 || total <= 0 || idle > total) return null;
        return Math.Clamp((total - idle) * 100 / total, 0, 100);
    }
}
