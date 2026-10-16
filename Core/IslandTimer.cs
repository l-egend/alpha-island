using System.Windows.Threading;

namespace alpha_island.Core;

/// <summary>A lifecycle handle over the shared scheduler, for event-based service integrations.</summary>
internal sealed class IslandTimer : IPausable, IDisposable
{
    private readonly string _key;
    private IslandTicker.Subscription? _subscription;
    private TimeSpan _interval = TimeSpan.FromSeconds(1);
    private volatile bool _enabled, _disposed;
    internal Dispatcher Dispatcher { get; }
    internal TimeSpan Interval
    {
        get => _interval;
        set { if (value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(value)); _interval = value; Synchronize(); }
    }
    internal bool IsEnabled => _enabled && !_disposed;
    public bool IsPaused => !IsEnabled;
    internal event EventHandler? Tick;
    internal IslandTimer(Dispatcher dispatcher, string purpose)
    { Dispatcher = dispatcher; _key = purpose + "/" + Guid.NewGuid().ToString("N"); }
    internal void Start() { if (_disposed) return; _enabled = true; Synchronize(); }
    internal void Stop() { _enabled = false; Synchronize(); }
    public void SetPaused(bool paused) { if (paused) Stop(); else Start(); }
    private void Synchronize()
    {
        if (Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(DispatcherPriority.Background, Synchronize); return; }
        if (!IsEnabled) { _subscription?.Dispose(); _subscription = null; return; }
        if (_subscription == null)
            _subscription = IslandTicker.Shared(Dispatcher).Subscribe(_key, _interval, () => { if (IsEnabled) Tick?.Invoke(this, EventArgs.Empty); });
        else _subscription.SetInterval(_interval);
    }
    public void Dispose() { _disposed = true; Stop(); Tick = null; }
}
