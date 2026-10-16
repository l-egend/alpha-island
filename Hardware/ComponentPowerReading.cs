namespace AlphaIsland.Hardware;

/// <summary>A CPU package or discrete GPU provider can be replaced without changing the island.</summary>
public sealed record ComponentPowerReading(double? Watts, string Reason, string Source);

public interface IComponentPowerSource : IDisposable
{
    ComponentPowerReading Sample();
}

/// <summary>Samples both components in one background cycle; never substitutes estimates or old values.</summary>
public sealed class WindowsHardwarePowerSource : IHardwarePowerSource
{
    private readonly IComponentPowerSource _cpu;
    private readonly IComponentPowerSource _gpu;
    private bool _disposed;

    public WindowsHardwarePowerSource(IComponentPowerSource? cpu = null, IComponentPowerSource? gpu = null)
    {
        _cpu = cpu ?? new EmiCpuPowerSource();
        _gpu = gpu ?? new NvidiaGpuPowerSource();
    }

    public HardwarePowerReading Sample()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cpu = _cpu.Sample();
        var gpu = _gpu.Sample();
        return new(cpu.Watts, gpu.Watts, string.Join("；", new[] { cpu.Reason, gpu.Reason }.Where(x => !string.IsNullOrWhiteSpace(x))))
        { Source = $"{cpu.Source} + {gpu.Source}" };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cpu.Dispose(); } finally { _gpu.Dispose(); }
    }
}
