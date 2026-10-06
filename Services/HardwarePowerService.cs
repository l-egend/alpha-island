using AlphaIsland.Hardware;

namespace alpha_island.Services;

/// <summary>Owns sampling lifetime and chooses a fresh privileged reading before the local fallback.</summary>
internal sealed class HardwarePowerService : IDisposable
{
    private readonly object _gate = new();
    private readonly IHardwarePowerSource _local;
    private readonly HardwarePowerWorkerClient _worker;
    private bool _disposed;
    private HardwarePowerReading _current = new(null, null, "正在读取硬件功耗传感器");
    internal HardwarePowerService(IHardwarePowerSource? local = null, HardwarePowerWorkerClient? worker = null)
    { _local = local ?? new LibreHardwarePowerSource(); _worker = worker ?? new HardwarePowerWorkerClient(); }
    internal HardwarePowerReading Current => Volatile.Read(ref _current).FreshAt(DateTime.UtcNow);
    internal bool HasElevatedReading => _worker.Current != null;
    internal Task<string> EnableElevatedAsync() => _worker.StartAsync();
    internal void DisableElevated() => _worker.Stop();
    internal void Sample()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var elevated = _worker.Current;
            // Never combine samples from different processes/timestamps into a fabricated total.
            Volatile.Write(ref _current, elevated ?? _local.Sample());
        }
    }
    public void Dispose()
    {
        _worker.Dispose();
        lock (_gate) { if (_disposed) return; _disposed = true; _local.Dispose(); }
    }
}
