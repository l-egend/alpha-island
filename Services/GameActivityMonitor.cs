using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using alpha_island.Core;
using alpha_island.Interop;
using System.Windows.Threading;

namespace alpha_island.Services;

internal interface IGameProcessLease : IDisposable { bool IsAlive { get; } }
internal interface IGameActivityProbe
{
    GameForeground? ReadForeground();
    IGameProcessLease? Track(GameIdentity identity);
}
internal sealed record GameActivityState(long Revision, bool Active, string Name, string Source, int Count)
{
    internal static GameActivityState Inactive { get; } = new(0, false, "", "", 0);
}

/// <summary>Foreground events identify a game once; retained process handles keep quiet mode through Alt+Tab.</summary>
internal sealed class GameActivityMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly IGameActivityProbe _probe;
    private readonly Func<string[]> _catalog;
    private readonly Dictionary<uint, (GameIdentity Identity, IGameProcessLease Lease)> _playing = new();
    private readonly NativeMethods.WinEventProc _callback;
    private HookThread? _events;
    private IntPtr _hook;
    private IslandTimer? _timer;
    private readonly Dispatcher _dispatcher;
    private GameIdentityPolicy _policy;
    private string _custom;
    private long _nextCatalog;
    private int _queued;
    private bool _disposed;
    private GameActivityState _current = GameActivityState.Inactive;
    internal GameActivityState Current => Volatile.Read(ref _current);
    internal bool EventsRunning => _events?.IsRunning == true;
    internal event Action<GameActivityState>? Changed;
    internal GameActivityMonitor(string customProcesses = "", IGameActivityProbe? probe = null, Func<string[]>? catalog = null, Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _custom = customProcesses; _probe = probe ?? new GameActivityProbe(); _catalog = catalog ?? GameActivityProbe.ReadWindowsGames;
        _policy = new GameIdentityPolicy([], customProcesses);
        _callback = (_, _, _, _, _, _, _) => QueueRefresh(); // Never perform registry/process I/O on the event thread.
    }
    internal void Start()
    {
        lock (_gate)
        {
            if (_disposed || _timer != null) return;
            _events = new HookThread("AlphaIsland.GameForeground");
            _events.Start(() => _hook = NativeMethods.SetWinEventHook(3, 3, IntPtr.Zero, _callback, 0, 0, 0),
                () => { if (_hook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_hook); _hook = IntPtr.Zero; });
            // Also covers process exit and fullscreen transitions without a foreground change.
            _timer = new IslandTimer(_dispatcher, "game-activity") { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => QueueRefresh(); _timer.Start(); QueueRefresh();
        }
    }
    internal void SetCustomProcesses(string custom)
    {
        lock (_gate) { if (_disposed || _custom == custom) return; _custom = custom; _nextCatalog = 0; }
        QueueRefresh();
    }
    private void QueueRefresh()
    {
        if (Interlocked.CompareExchange(ref _queued, 1, 0) != 0) return;
        ThreadPool.QueueUserWorkItem(_ => { try { Refresh(); } finally { Volatile.Write(ref _queued, 0); } });
    }
    internal void Refresh()
    {
        GameActivityState? changed = null;
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                if (Environment.TickCount64 >= _nextCatalog)
                { _policy = new(_catalog(), _custom); _nextCatalog = Environment.TickCount64 + 60_000; }
                // Read/classify the new foreground before removing exited games, avoiding a quiet-mode gap between two games.
                var foreground = _probe.ReadForeground();
                if (foreground != null && (!_playing.TryGetValue(foreground.ProcessId, out var tracked)
                    || !tracked.Lease.IsAlive))
                {
                    var game = _policy.Identify(foreground);
                    if (game != null && _probe.Track(game) is { } lease)
                    {
                        if (_playing.Remove(game.ProcessId, out var old)) old.Lease.Dispose();
                        _playing[game.ProcessId] = (game, lease);
                    }
                }
                foreach (uint pid in _playing.Where(x => !x.Value.Lease.IsAlive).Select(x => x.Key).ToArray())
                { _playing[pid].Lease.Dispose(); _playing.Remove(pid); }
                var identities = _playing.Values.Select(x => x.Identity).OrderBy(x => x.ProcessId).ToArray();
                string name = string.Join("、", identities.Take(2).Select(x => x.Name));
                string source = string.Join("、", identities.Select(x => x.Source).Distinct());
                if (_current.Active != (identities.Length > 0) || _current.Name != name || _current.Count != identities.Length)
                { changed = new(_current.Revision + 1, identities.Length > 0, name, source, identities.Length); Volatile.Write(ref _current, changed); }
            }
            catch (Exception ex) { IslandLog.Debug("GameQuiet", "游戏状态读取暂时失败，保留已确认状态：" + ex.GetType().Name); }
        }
        if (changed != null) Changed?.Invoke(changed);
    }
    public void Dispose()
    {
        HookThread? events;
        lock (_gate)
        {
            if (_disposed) return; _disposed = true; _timer?.Dispose(); _timer = null;
            events = _events; _events = null;
            foreach (var tracked in _playing.Values) tracked.Lease.Dispose();
            _playing.Clear(); Volatile.Write(ref _current, GameActivityState.Inactive);
        }
        events?.Dispose();
    }
}