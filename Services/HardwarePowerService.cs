using AlphaIsland.Hardware;

namespace alpha_island.Services;

/// <summary>Serializes in-process sampling and disposal. No privileged process is used.</summary>
internal sealed class HardwarePowerService : IDisposable
{
    private readonly object _gate = new();
    private readonly IHardwarePowerSource _source;
    private bool _disposed;
    private HardwarePowerReading _current = new(null, null, "正在读取硬件功耗传感器");
    internal HardwarePowerService(IHardwarePowerSource? source = null) => _source = source ?? new WindowsHardwarePowerSource();
    internal HardwarePowerReading Current => Volatile.Read(ref _current).FreshAt(DateTime.UtcNow);
    internal void Sample()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Volatile.Write(ref _current, _source.Sample());
        }
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _source.Dispose(); }
    }
}
