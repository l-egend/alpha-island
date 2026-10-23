using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Timer = System.Threading.Timer;
using alpha_island.Core;

namespace alpha_island.Interop;

/// <summary>
/// The shell reuses its above-lock OSD for volume, brightness and other feedback.
/// Hide it only during feedback accepted by our volume UI, never just by class name.
/// No shell styles, preferences, audio commands or keyboard events are changed.
/// </summary>
internal sealed class VolumeFlyoutGuard : IDisposable
{
    private const uint ObjectShow = 0x8002, ObjectLocationChange = 0x800B;
    private const int FeedbackWindowMs = 700, NativeFlyoutLifetimeMs = 3000;
    private readonly HookThread _thread = new("Windows volume flyout takeover");
    private readonly NativeMethods.WinEventProc _callback;
    private readonly Func<IntPtr, bool> _matches;
    private readonly bool _fixture;
    private readonly object _gate = new();
    private readonly Timer _retry;
    private readonly Dictionary<IntPtr, (uint Process, long HiddenAt, bool OwnsCloak)> _hiddenWindows = new();
    private IntPtr _hook;
    private volatile bool _enabled;
    private bool _disposed;
    private long _feedbackUntil, _hidden;
    public event Action? FlyoutHidden;
    public bool Running => _enabled && _thread.IsRunning;
    public long HiddenCount => Interlocked.Read(ref _hidden);

    internal VolumeFlyoutGuard(Func<IntPtr, bool>? fixtureMatcher = null)
    {
        _fixture = fixtureMatcher != null;
        _matches = fixtureMatcher ?? IsShellFlyout;
        _callback = OnWindowEvent;
        _retry = new Timer(_ => ScanFeedbackWindows(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool SetEnabled(bool enabled)
    {
        if (_disposed) return false;
        _enabled = enabled;
        if (!enabled)
        {
            lock (_gate) { _feedbackUntil = 0; _retry.Change(Timeout.Infinite, Timeout.Infinite); }
            _thread.Stop();
            return false;
        }
        if (_thread.IsRunning) return true;
        return _thread.Start(() => _hook = NativeMethods.SetWinEventHook(ObjectShow, ObjectLocationChange,
            IntPtr.Zero, _callback, 0, 0, _fixture ? 0u : 2u), () =>
        {
            if (_hook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
            lock (_gate) RestoreRecentWindows();
        });
    }

    public void TakeOverFeedback()
    {
        lock (_gate)
        {
            if (_disposed || !Running) return;
            _feedbackUntil = Environment.TickCount64 + FeedbackWindowMs;
            // CoreAudio and EVENT_OBJECT_SHOW can arrive in either order. Retry only
            // briefly after volume feedback; there is no idle window polling or UIA.
            _retry.Change(0, 20);
        }
    }

    private void ScanFeedbackWindows()
    {
        lock (_gate)
        {
            if (!Running || Environment.TickCount64 > _feedbackUntil)
            {
                ReleaseCloaks();
                if (!_disposed) _retry.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }
            foreach (var cls in _fixture ? new[] { "STATIC" } : new[] { "XamlExplorerHostIslandWindow", "NativeHWNDHost" })
            {
                IntPtr window = IntPtr.Zero;
                while ((window = FindWindowEx(IntPtr.Zero, window, cls, null)) != IntPtr.Zero) HideIfFeedback(window);
            }
            long now = Environment.TickCount64;
            foreach (var window in new List<IntPtr>(_hiddenWindows.Keys))
                if (now - _hiddenWindows[window].HiddenAt > NativeFlyoutLifetimeMs) _hiddenWindows.Remove(window);
        }
    }

    private void OnWindowEvent(IntPtr hook, uint kind, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (kind is not (ObjectShow or ObjectLocationChange) || childId != 0 || objectId is not (0 or -4)) return;
        lock (_gate) HideIfFeedback(window);
    }

    private void HideIfFeedback(IntPtr window)
    {
        if (!Running || Environment.TickCount64 > _feedbackUntil || window == IntPtr.Zero
            || !NativeMethods.IsWindowVisible(window)) return;
        try
        {
            if (!_matches(window) || !_enabled) return;
            NativeMethods.GetWindowThreadProcessId(window, out uint process);
            bool ours = _hiddenWindows.TryGetValue(window, out var previous) && previous.Process == process && previous.OwnsCloak;
            // Keep the compositor from presenting a frame if the shell calls ShowWindow
            // again before its queued hide is processed. Never clear a pre-existing cloak.
            if (!ours && DwmGetWindowAttribute(window, 14, out int cloak, 4) == 0 && (cloak & 1) == 0)
            {
                int value = 1;
                ours = NativeMethods.DwmSetWindowAttribute(window, 13, ref value, 4) == 0;
            }
            _hiddenWindows[window] = (process, Environment.TickCount64, ours);
            NativeMethods.ShowWindowAsync(window, 0);
            if (Interlocked.Increment(ref _hidden) == 1) IslandLog.Info("Volume", "已接管 Windows 原生音量浮层");
            FlyoutHidden?.Invoke();
        }
        catch { /* A transient shell window can disappear at any point. */ }
    }

    private void RestoreRecentWindows()
    {
        ReleaseCloaks();
        foreach (var (window, previous) in _hiddenWindows)
        {
            try
            {
                NativeMethods.GetWindowThreadProcessId(window, out uint process);
                // Do not resurrect an expired OSD or touch a reused HWND. Subsequent
                // keys show the shell window normally because the hook is now gone.
                if (process == previous.Process && Environment.TickCount64 - previous.HiddenAt <= NativeFlyoutLifetimeMs)
                    NativeMethods.ShowWindowAsync(window, 4);
            }
            catch { }
        }
        _hiddenWindows.Clear();
    }

    private void ReleaseCloaks()
    {
        foreach (var (window, previous) in _hiddenWindows.ToArray())
        {
            if (!previous.OwnsCloak) continue;
            NativeMethods.GetWindowThreadProcessId(window, out uint process);
            // Undo only our recorded change. Classification can become unavailable
            // during shell restart or shutdown and must not prevent restoration.
            if (process == previous.Process)
            {
                int value = 0;
                NativeMethods.DwmSetWindowAttribute(window, 13, ref value, 4);
            }
            _hiddenWindows[window] = (previous.Process, previous.HiddenAt, false);
        }
    }

    internal static bool IsShellFlyout(IntPtr window)
    {
        try { return IsShellFlyoutCore(window); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    private static bool IsShellFlyoutCore(IntPtr window)
    {
        var cls = new StringBuilder(256); GetClassName(window, cls, cls.Capacity);
        var title = new StringBuilder(256); GetWindowText(window, title, title.Capacity);
        if (!IsFlyoutHostClass(cls.ToString()) || title.Length != 0) return false;
        // AboveLockUX is the dedicated feedback band. Task switching, virtual
        // desktops, Quick Settings and notifications use different bands/titles.
        if (!GetWindowBand(window, out uint band) || band != 18) return false;
        if (cls.ToString() == "XamlExplorerHostIslandWindow")
        {
            if (FindWindowEx(window, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge", "DesktopWindowXamlSource") == IntPtr.Zero) return false;
        }
        else if (FindWindowEx(window, IntPtr.Zero, "DirectUIHWND", null) == IntPtr.Zero) return false;
        NativeMethods.GetWindowThreadProcessId(window, out uint process);
        using var handle = new Microsoft.Win32.SafeHandles.SafeProcessHandle(OpenProcess(0x1000, false, process), true);
        if (handle.IsInvalid) return false;
        var path = new StringBuilder(1024); int length = path.Capacity;
        return QueryFullProcessImageName(handle, 0, path, ref length)
            && IsShellProcessPath(path.ToString(), Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    }

    internal static bool IsFlyoutHostClass(string cls) => cls is "XamlExplorerHostIslandWindow" or "NativeHWNDHost";
    internal static bool IsShellProcessPath(string path, string windowsDirectory)
        => path.Equals(Path.Combine(windowsDirectory, "explorer.exe"), StringComparison.OrdinalIgnoreCase)
            || ToastBannerGuard.IsShellProcessPath(path, windowsDirectory);

    public void Dispose()
    {
        if (_disposed) return;
        SetEnabled(false);
        lock (_gate) { _disposed = true; _retry.Dispose(); FlyoutHidden = null; }
        _thread.Dispose();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowBand(IntPtr window, out uint band);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(Microsoft.Win32.SafeHandles.SafeProcessHandle handle, uint flags, StringBuilder name, ref int length);
}
