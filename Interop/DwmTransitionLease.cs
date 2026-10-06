using System;
using alpha_island.Core;

namespace alpha_island.Interop;

/// <summary>Temporarily disables native transitions for one tracked player window.</summary>
internal sealed class DwmTransitionLease : IDisposable
{
    // The marker also survives an island process restart, so a new instance can restore the same default.
    // Windows frees the property list when the player destroys its window; the value is not an allocation.
    internal const string OriginalProperty = "AlphaIsland.MediaHandoff.DwmTransitionsOriginal.v1";
    private readonly IntPtr _window, _marker;
    private readonly uint _process;
    private readonly int _original;
    private bool _disposed;
    internal bool Applied => !_disposed;

    private DwmTransitionLease(IntPtr window, uint process, int original, IntPtr marker)
    { _window = window; _process = process; _original = original; _marker = marker; }

    internal static DwmTransitionLease? TryAcquire(IntPtr window)
    {
        if (window == IntPtr.Zero || !NativeMethods.IsWindow(window)) return null;
        NativeMethods.GetWindowThreadProcessId(window, out uint process);
        if (process == 0) return null;
        IntPtr existing = NativeMethods.GetProp(window, OriginalProperty);
        if (existing != IntPtr.Zero && existing != new IntPtr(1) && existing != new IntPtr(2)) return null;
        int original;
        if (existing != IntPtr.Zero) original = existing.ToInt64() == 2 ? 1 : 0;
        else
        {
            // DWMWA_TRANSITIONS_FORCEDISABLED is setter-only on current Windows (Get returns E_INVALIDARG).
            // CloudMusic has a native minimize transition before this feature: restore the documented enabled state.
            int read = NativeMethods.DwmGetWindowAttribute(window, NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED, out original, sizeof(int));
            original = read >= 0 && original != 0 ? 1 : 0;
        }
        var marker = new IntPtr(original + 1);
        if (existing == IntPtr.Zero && !NativeMethods.SetProp(window, OriginalProperty, marker)) return null;
        int disabled = 1;
        int result = NativeMethods.DwmSetWindowAttribute(window, NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED, ref disabled, sizeof(int));
        if (result < 0)
        {
            if (existing == IntPtr.Zero) NativeMethods.RemoveProp(window, OriginalProperty);
            return null;
        }
        return new(window, process, original, marker);
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (!NativeMethods.IsWindow(_window) || NativeMethods.GetProp(_window, OriginalProperty) != _marker) return;
        NativeMethods.GetWindowThreadProcessId(_window, out uint process);
        if (process != _process) return; // Do not touch a reused handle.
        int original = _original;
        int result = NativeMethods.DwmSetWindowAttribute(_window, NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED, ref original, sizeof(int));
        if (result >= 0) NativeMethods.RemoveProp(_window, OriginalProperty);
        else IslandLog.Warn("MediaWindow", $"恢复播放器系统过渡失败: 0x{result:X8}");
    }
}
