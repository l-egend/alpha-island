using System.Windows.Threading;
using AlphaIsland.Hardware;
using alpha_island.Core;
using alpha_island.ViewModels;

namespace alpha_island.Services;

/// <summary>Owns resource probes, background work and stale-result rejection.</summary>
internal sealed class SystemResourceMonitor : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IslandTicker _ticker;
    private readonly Func<CpuUsageService> _createCpu;
    private readonly Func<HardwarePowerService> _createPower;
    private CpuUsageService? _cpu;
    private HardwarePowerService? _power;
    private MemoryService? _memory;
    private IslandTicker.Subscription? _cpuPoll, _memoryPoll, _powerPoll;
    private bool _disposed, _cpuSampling, _powerSampling;
    private string? _powerReason;
    private ResourceSamplingProfile _profile = ResourceSamplingPolicy.For(false, false, false, false, false);
    internal SystemResourcesViewModel Values { get; }
    internal MemorySample Memory => _memory?.Current ?? default;
    internal HardwarePowerReading? Power => _power?.Current;
    internal bool CpuEnabled => _cpu != null;
    internal bool MemoryEnabled => _memory != null;
    internal bool CpuSampling => _cpuSampling;
    internal DateTimeOffset CpuSampledAt { get; private set; }
    internal DateTimeOffset MemorySampledAt { get; private set; }
    internal event Action<MemorySample>? MemorySampled;
    internal event Action? Changed;

    internal SystemResourceMonitor(Dispatcher dispatcher, IslandTicker ticker,
        Func<CpuUsageService>? createCpu = null, Func<HardwarePowerService>? createPower = null, SystemResourcesViewModel? values = null)
    {
        _dispatcher = dispatcher; _ticker = ticker; Values = values ?? new();
        _createCpu = createCpu ?? (() => new CpuUsageService());
        _createPower = createPower ?? (() => new HardwarePowerService());
    }

    internal void SetCpuEnabled(bool enabled)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || enabled == (_cpu != null)) return;
        if (enabled)
        {
            _cpu = _createCpu();
            _cpuPoll = _ticker.Subscribe("cpu", _profile.Cpu, SampleCpu, runImmediately: true);
        }
        else
        {
            _cpuPoll?.Dispose(); _cpuPoll = null;
            var old = _cpu; _cpu = null;
            if (old != null) _ = Task.Run(old.Dispose);
        }
        Publish();
    }
    internal void SetMemoryEnabled(bool enabled)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || enabled == (_memory != null)) return;
        if (enabled)
        {
            _memory = new MemoryService();
            _memoryPoll = _ticker.Subscribe("memory", _profile.Memory, SampleMemory, runImmediately: true);
        }
        else
        {
            _memoryPoll?.Dispose(); _memoryPoll = null;
            _memory?.Dispose(); _memory = null;
        }
        Publish();
    }
    internal void SetPowerEnabled(bool enabled)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || enabled == (_power != null)) return;
        if (enabled)
        {
            _power = _createPower();
            _powerPoll = _ticker.Subscribe("hardwarepower", _profile.HardwarePower, SamplePower);
            _powerPoll.SetPaused(_profile.PauseHardware);
            if (!_profile.PauseHardware) _powerPoll.TriggerNow();
        }
        else
        {
            _powerPoll?.Dispose(); _powerPoll = null;
            var old = _power; _power = null;
            if (old != null) _ = Task.Run(old.Dispose);
        }
        Publish();
    }
    internal void ApplyProfile(ResourceSamplingProfile profile)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || profile == _profile) return;
        var previous = _profile; _profile = profile;
        SetCadence(_cpuPoll, profile.Cpu, previous.Cpu);
        SetCadence(_memoryPoll, profile.Memory, previous.Memory);
        if (_powerPoll != null)
        {
            bool resume = _powerPoll.IsPaused && !profile.PauseHardware;
            _powerPoll.SetInterval(profile.HardwarePower);
            _powerPoll.SetPaused(profile.PauseHardware);
            if (resume || profile.HardwarePower < previous.HardwarePower) _powerPoll.TriggerNow();
        }
    }
    private static void SetCadence(IslandTicker.Subscription? poll, TimeSpan interval, TimeSpan previous)
    {
        poll?.SetInterval(interval);
        if (interval < previous) poll?.TriggerNow();
    }
    internal void SampleMemory()
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _memory == null) return;
        _memory.Sample(); MemorySampledAt = DateTimeOffset.UtcNow; Publish(); MemorySampled?.Invoke(_memory.Current);
    }
    internal async void SampleCpu()
    {
        var service = _cpu;
        if (_disposed || service == null || _cpuSampling) return;
        _cpuSampling = true;
        bool sampled = false;
        try { await Task.Run(service.Sample).ConfigureAwait(false); sampled = true; }
        catch (Exception ex) { IslandLog.Error("Cpu", ex); }
        finally
        {
            Post(() => { _cpuSampling = false; if (!_disposed && ReferenceEquals(_cpu, service)) { CpuSampledAt = sampled ? DateTimeOffset.UtcNow : default; Publish(); } });
        }
    }
    private async void SamplePower()
    {
        var service = _power;
        if (_disposed || service == null || _powerSampling) return;
        _powerSampling = true;
        try { await Task.Run(service.Sample).ConfigureAwait(false); }
        catch (Exception ex) { IslandLog.Error("HardwarePower", ex); }
        finally
        {
            Post(() =>
            {
                _powerSampling = false;
                if (_disposed || !ReferenceEquals(_power, service)) return;
                var reading = service.Current;
                string reason = reading.TotalWatts.HasValue ? "CPU＋GPU 功耗传感器已就绪" : reading.Reason;
                if (reason != _powerReason) { _powerReason = reason; IslandLog.Info("HardwarePower", reason); }
                Publish();
            });
        }
    }
    private void Post(Action update)
    {
        try
        {
            if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
                _dispatcher.BeginInvoke(DispatcherPriority.Background, update);
        }
        catch (InvalidOperationException) when (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) { }
    }
    private void Publish()
    {
        var memory = Memory; var power = Power;
        Values.Update(new(_cpu?.CurrentPercent, memory.Valid ? memory.UsedPercent : null,
            power?.CpuWatts, power?.GpuWatts, power?.TotalWatts,
            memory.Valid ? $"已用 {MemoryService.FormatSize(memory.UsedBytes)} / {MemoryService.FormatSize((long)memory.TotalBytes)}" : "等待内存采样",
            power?.Description ?? "硬件功耗监测未开启"));
        Changed?.Invoke();
    }
    internal void Refresh() { if (!_disposed) Publish(); }
    public void Dispose()
    {
        if (_disposed) return;
        SetCpuEnabled(false); SetMemoryEnabled(false); SetPowerEnabled(false);
        _disposed = true; Changed = null; MemorySampled = null;
    }
}
