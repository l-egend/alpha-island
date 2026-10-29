using System.Windows.Threading;

namespace AlphaIsland.PluginSdk;

public interface IPluginScheduledTask : IDisposable
{
    bool IsPaused { get; }
    void SetPaused(bool paused);
    void SetInterval(TimeSpan interval);
}

/// <summary>Compatibility with API 1 hosts; current hosts substitute their shared scheduler.</summary>
internal sealed class LegacyPluginScheduledTask : IPluginScheduledTask
{
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenRegistration _registration;
    private bool _disposed;
    internal LegacyPluginScheduledTask(IPluginContext context, TimeSpan interval, Action callback, bool initiallyPaused)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _timer = new DispatcherTimer(DispatcherPriority.Background, context.Dispatcher) { Interval = interval };
        _timer.Tick += (_, _) => { if (!context.Lifetime.IsCancellationRequested && !_disposed) callback(); };
        _registration = context.Lifetime.Register(() => context.Dispatcher.BeginInvoke(Dispose));
        if (!initiallyPaused && !context.Lifetime.IsCancellationRequested) _timer.Start();
    }
    public bool IsPaused => !_timer.IsEnabled;
    public void SetPaused(bool paused) { if (_disposed) return; if (paused) _timer.Stop(); else _timer.Start(); }
    public void SetInterval(TimeSpan interval) { if (!_disposed) _timer.Interval = interval; }
    public void Dispose() { if (_disposed) return; _disposed = true; _timer.Stop(); _registration.Dispose(); }
}
