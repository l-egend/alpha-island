using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using alpha_island.Core;

namespace alpha_island.Interop;

/// <summary>
/// Hide only the Windows shell's transient toast window. Notification storage stays
/// available to UserNotificationListener; no Windows preferences are changed.
/// The out-of-context hook runs on its own message thread, independently of WPF.
/// </summary>
internal sealed class ToastBannerGuard : IDisposable
{
    private const uint ObjectShow = 0x8002, ObjectLocationChange = 0x800B, ObjectNameChange = 0x800C;
    private static readonly string[] ToastTitles =
    {
        "New notification", "Toast notification", "新通知", "新通知視窗", "新通知窗口",
        "Nouvelle notification", "Neue Benachrichtigung", "Nueva notificación", "Nova notificação",
        "新しい通知", "새 알림", "Новое уведомление"
    };
    private static readonly HashSet<string> ToastTitleLookup = new(ToastTitles, StringComparer.OrdinalIgnoreCase);
    private readonly HookThread _thread = new("Windows toast banner takeover");
    private readonly NativeMethods.WinEventProc _callback;
    private readonly Func<IntPtr, bool> _matches;
    private readonly bool _fixture;
    // Only the hook thread touches this set. Restore only windows we actually hid.
    private readonly HashSet<IntPtr> _hiddenWindows = new();
    private IntPtr _hook;
    private volatile bool _enabled;
    private bool _disposed;
    private long _hidden;
    public event Action? BannerHidden;
    public bool Running => _enabled && _thread.IsRunning;
    public long HiddenCount => Interlocked.Read(ref _hidden);

    internal ToastBannerGuard(Func<IntPtr, bool>? fixtureMatcher = null)
    {
        _fixture = fixtureMatcher != null;
        _matches = fixtureMatcher ?? IsShellToastWindow;
        _callback = OnWindowEvent;
    }
    public bool SetEnabled(bool enabled)
    {
        if (_disposed) return false;
        _enabled = enabled;
        if (!enabled) { _thread.Stop(); return false; }
        if (_thread.IsRunning) return true;
        return _thread.Start(() =>
        {
            _hook = NativeMethods.SetWinEventHook(ObjectShow, ObjectNameChange, IntPtr.Zero, _callback, 0, 0, _fixture ? 0u : 2u);
            if (_hook != IntPtr.Zero)
            {
                // EnumWindows omits UWP-hosted top-level windows. Find the dedicated
                // toast host directly too, in case takeover is enabled mid-notification.
                foreach (var title in ToastTitles)
                {
                    IntPtr existing = IntPtr.Zero;
                    while ((existing = FindWindowEx(IntPtr.Zero, existing, null, title)) != IntPtr.Zero) HideIfToast(existing);
                }
                NativeMethods.EnumWindows((window, _) => { HideIfToast(window); return true; }, IntPtr.Zero);
            }
            return _hook;
        }, () =>
        {
            if (_hook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
            RestoreHiddenWindows();
        });
    }
    private void OnWindowEvent(IntPtr hook, uint kind, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (kind is not (ObjectShow or ObjectLocationChange or ObjectNameChange) || childId != 0 || objectId is not (0 or -4)) return;
        var root = GetAncestor(window, 2);
        HideIfToast(root == IntPtr.Zero ? window : root);
    }
    private void HideIfToast(IntPtr window)
    {
        if (!_enabled || window == IntPtr.Zero || !NativeMethods.IsWindowVisible(window)) return;
        try
        {
            if (!_matches(window) || !_enabled) return;
            // Async delivery avoids waiting for or re-entering the shell's UI thread.
            if (NativeMethods.ShowWindowAsync(window, 0))
            {
                _hiddenWindows.Add(window);
                if (Interlocked.Increment(ref _hidden) == 1) IslandLog.Info("Toast", "已隐藏 Windows 原生通知横幅");
                BannerHidden?.Invoke();
            }
        }
        catch { /* A shell window may be destroyed while the event is being delivered. */ }
    }
    private void RestoreHiddenWindows()
    {
        foreach (var window in _hiddenWindows)
        {
            try
            {
                // A queued HIDE may not have run yet. Queue SHOW without activation even
                // if currently visible, so normal shutdown cannot leave the host hidden.
                if (_matches(window)) NativeMethods.ShowWindowAsync(window, 4);
            }
            catch { /* The shell may have already destroyed its transient toast window. */ }
        }
        _hiddenWindows.Clear();
    }
    private static bool IsShellToastWindow(IntPtr window)
    {
        var cls = new StringBuilder(256); GetClassName(window, cls, cls.Capacity);
        if (cls.ToString() is not ("Windows.UI.Core.CoreWindow" or "Xaml_WindowedPopupClass" or "Windows.UI.Composition.DesktopWindowContentBridge")) return false;
        var title = new StringBuilder(256); GetWindowText(window, title, title.Capacity);
        if (!IsToastTitle(title.ToString())) return false;
        if (!NativeMethods.GetWindowRect(window, out var bounds)) return false;
        // Some shell versions use a large transparent host for a stack of toasts.
        // Identity and the dedicated toast title, rather than a guessed size, limit scope.
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return false;
        NativeMethods.GetWindowThreadProcessId(window, out uint process);
        using var handle = new Microsoft.Win32.SafeHandles.SafeProcessHandle(OpenProcess(0x1000, false, process), true);
        if (handle.IsInvalid) return false;
        var path = new StringBuilder(1024); int length = path.Capacity;
        return QueryFullProcessImageName(handle, 0, path, ref length) && IsShellProcessPath(path.ToString(), Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    }
    internal static bool IsToastTitle(string title) => ToastTitleLookup.Contains(title.Trim());
    internal static bool IsShellProcessPath(string executable, string windowsDirectory)
    {
        string name = Path.GetFileName(executable);
        return (name.Equals("ShellExperienceHost.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("ShellHost.exe", StringComparison.OrdinalIgnoreCase))
            && executable.StartsWith(Path.Combine(windowsDirectory, "SystemApps") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    public void Dispose()
    {
        if (_disposed) return;
        SetEnabled(false); _disposed = true; BannerHidden = null; _thread.Dispose();
    }
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flag);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(Microsoft.Win32.SafeHandles.SafeProcessHandle handle, uint flags, StringBuilder name, ref int length);
}
