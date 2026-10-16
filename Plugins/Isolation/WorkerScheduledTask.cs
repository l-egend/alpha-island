using AlphaIsland.PluginSdk;
using alpha_island.Core;

namespace alpha_island.Plugins.Isolation;
internal sealed class WorkerScheduledTask : IPluginScheduledTask
{
    private readonly IslandTimer _timer;
    private readonly CancellationTokenRegistration _registration;
    internal WorkerScheduledTask(PluginWorkerContext context, string key, TimeSpan interval, Action callback, bool paused)
    {
        _timer = new IslandTimer(context.Dispatcher, "plugin/" + key) { Interval = interval };
        _timer.Tick += (_, _) => { if (!context.Lifetime.IsCancellationRequested) callback(); };
        _registration = context.Lifetime.Register(_timer.Dispose);
        if (!paused && !context.Lifetime.IsCancellationRequested) _timer.Start();
    }
    public bool IsPaused => _timer.IsPaused;
    public void SetPaused(bool paused) => _timer.SetPaused(paused);
    public void SetInterval(TimeSpan interval) => _timer.Interval = interval;
    public void Dispose() { _timer.Dispose(); _registration.Dispose(); }
}
