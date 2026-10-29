using System.Threading;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Interop;

namespace alpha_island.Services;

internal sealed class BrowserWindowMonitor : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly BrowserWindowProbe _probe = new();
    private readonly Func<BrowserWindowSnapshot> _read;
    private readonly HookThread _events = new("AlphaIsland.BrowserWindow");
    private readonly NativeMethods.WinEventProc _callback;
    private readonly List<IntPtr> _hooks = new();
    private readonly object _lifecycleGate = new();
    private readonly DispatcherTimer _settle;
    private int _queued;
    private volatile bool _disposed;
    internal bool Current { get; private set; }
    internal bool EventsRunning => _events.IsRunning;
    internal event Action<bool>? Changed;

    internal BrowserWindowMonitor(Dispatcher dispatcher, Func<BrowserWindowSnapshot>? read = null)
    {
        _dispatcher = dispatcher; _read = read ?? _probe.Read;
        _settle = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
        _settle.Tick += (_, _) => { _settle.Stop(); Refresh(); };
        _callback = (_, kind, window, obj, child, _, _) =>
        {
            if (kind == 3 || kind is 0x16 or 0x17
                || window != IntPtr.Zero && obj == 0 && child == 0 && window == _probe.TrackedWindow)
                RequestRefresh();
        };
    }

    internal void Start()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _events.Start(() =>
            {
                foreach (var range in new[] { (3u, 3u), (0x16u, 0x17u), (0x8001u, 0x800Bu) })
                {
                    var hook = NativeMethods.SetWinEventHook(range.Item1, range.Item2, IntPtr.Zero, _callback, 0, 0, 0);
                    if (hook != IntPtr.Zero) _hooks.Add(hook);
                }
                return _hooks.FirstOrDefault();
            }, () => { foreach (var hook in _hooks) NativeMethods.UnhookWinEvent(hook); _hooks.Clear(); });
        }
        RequestRefresh();
    }

    internal void RequestRefresh()
    {
        if (_disposed || _dispatcher.HasShutdownStarted || Interlocked.CompareExchange(ref _queued, 1, 0) != 0) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Volatile.Write(ref _queued, 0);
            if (_disposed) return;
            Refresh();
            // Maximizing can emit its first event before the final bounds exist.
            if (!_settle.IsEnabled) _settle.Start();
        }));
    }

    internal void Refresh()
    {
        if (_disposed) return;
        bool active = BrowserWindowPolicy.ShouldCompact(_read());
        if (Current == active) return;
        Current = active; Changed?.Invoke(active);
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true; _events.Dispose();
        }
        _settle.Stop();
        Changed = null;
    }
}
