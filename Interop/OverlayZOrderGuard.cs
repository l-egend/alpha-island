using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace alpha_island.Interop;

/// <summary>Keep a visible overlay above ordinary app windows after shell z-order changes, without activation or idle polling.</summary>
internal sealed class OverlayZOrderGuard : IDisposable
{
    private readonly IntPtr _overlay;
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _enabled;
    private readonly Func<Rect> _bounds;
    private readonly Action _raised;
    private readonly HookThread _thread = new("AlphaIsland.OverlayZOrder");
    private readonly NativeMethods.WinEventProc _callback;
    private readonly List<IntPtr> _hooks = new();
    private readonly HashSet<IntPtr> _candidates = new();
    private readonly object _gate = new();
    private readonly DispatcherTimer _settle;
    private int _queued;
    private volatile bool _disposed;

    internal OverlayZOrderGuard(IntPtr overlay, Dispatcher dispatcher, Func<bool> enabled, Func<Rect> bounds, Action raised)
    {
        _overlay = overlay; _dispatcher = dispatcher; _enabled = enabled; _bounds = bounds; _raised = raised;
        _callback = (_, kind, window, obj, child, _, _) => {
            if (kind == 3 || kind == 0x17 || kind is 0x8002 or 0x8004 or 0x800B && obj == 0 && child == 0) RequestCheck(window);
        };
        _settle = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(80) };
        _settle.Tick += (_, _) => { _settle.Stop(); CheckPending(clear: true); };
    }
    internal void Start() => _thread.Start(() => {
        foreach (var range in new[] { (3u, 3u), (0x17u, 0x17u), (0x8002u, 0x800Bu) })
        {
            var hook = NativeMethods.SetWinEventHook(range.Item1, range.Item2, IntPtr.Zero, _callback, 0, 0, 2); // skip own process
            if (hook != IntPtr.Zero) _hooks.Add(hook);
        }
        return _hooks.FirstOrDefault();
    }, () => { foreach (var hook in _hooks) NativeMethods.UnhookWinEvent(hook); _hooks.Clear(); });

    internal void RequestCheck(IntPtr candidate = default)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        if (candidate == IntPtr.Zero) candidate = NativeMethods.GetForegroundWindow();
        if (candidate == IntPtr.Zero || candidate == _overlay) return;
        NativeMethods.GetWindowThreadProcessId(candidate, out uint pid);
        if (pid == Environment.ProcessId || (NativeMethods.GetWindowLong(candidate, -16) & 0x40000000) != 0) return;
        lock (_gate) { if (_candidates.Count < 16) _candidates.Add(candidate); }
        if (Interlocked.CompareExchange(ref _queued, 1, 0) != 0) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => {
            Volatile.Write(ref _queued, 0);
            if (_disposed) return;
            CheckPending(clear: false); _settle.Stop(); _settle.Start();
        }));
    }
    private void CheckPending(bool clear)
    {
        IntPtr[] candidates;
        lock (_gate) { candidates = _candidates.ToArray(); if (clear) _candidates.Clear(); }
        if (_disposed || !_enabled() || !NativeMethods.IsWindowVisible(_overlay) || NativeMethods.IsIconic(_overlay)) return;
        Rect surface = _bounds(); if (surface.IsEmpty) return;
        foreach (IntPtr candidate in candidates)
        {
            if (!NativeMethods.IsWindowVisible(candidate) || NativeMethods.IsIconic(candidate)
                || (NativeMethods.GetWindowLong(candidate, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOOLWINDOW) != 0
                || !NativeMethods.GetWindowRect(candidate, out var rect)) continue;
            Rect cover = new(rect.Left, rect.Top, Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));
            if (!surface.IntersectsWith(cover)) continue;
            bool covered = false;
            // Only an occluding window higher in z-order warrants a native update.
            for (IntPtr above = NativeMethods.GetWindow(_overlay, 3); above != IntPtr.Zero; above = NativeMethods.GetWindow(above, 3))
                if (above == candidate) { covered = true; break; }
            if (!covered) continue;
            NativeMethods.SetWindowPos(_overlay, new IntPtr(-1), 0, 0, 0, 0, 0x13); // TOPMOST, NOMOVE | NOSIZE | NOACTIVATE
            _raised(); break;
        }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _settle.Stop(); _thread.Dispose();
        lock (_gate) _candidates.Clear();
    }
}