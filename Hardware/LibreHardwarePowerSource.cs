using LibreHardwareMonitor.Hardware;

namespace AlphaIsland.Hardware;

/// <summary>Reads actual CPU package and discrete GPU power. Never estimates watts from TDP or battery drain.</summary>
public sealed class LibreHardwarePowerSource : IHardwarePowerSource
{
    private Computer? _computer;
    private bool _disposed, _cpuAccess;
    private DateTime _retryAfter;
    public HardwarePowerReading Sample()
    {
        if (_disposed) return new(null, null, "硬件读取已停止");
        try
        {
            var driver = HardwareDriverAccess.Probe();
            bool cpuAccess = driver == HardwareDriverState.Ready;
            if (_computer != null && cpuAccess != _cpuAccess) Close();
            if (_computer == null)
            {
                if (DateTime.UtcNow < _retryAfter) return new(null, null, "硬件读取失败后正在等待重试");
                _cpuAccess = cpuAccess;
                _computer = new Computer { IsCpuEnabled = cpuAccess, IsGpuEnabled = true };
                _computer.Open();
            }
            foreach (IHardware hardware in _computer.Hardware) Update(hardware);
            var cpus = _computer.Hardware.Where(h => h.HardwareType == HardwareType.Cpu).ToArray();
            var gpus = _computer.Hardware.Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel).ToArray();
            double? cpu = cpus.Length == 0 ? null : HardwarePowerPolicy.SumComplete(cpus.Select(h => HardwarePowerPolicy.Package(h, "CPU Package", "Package", "Package Power")));
            // Failed MSR module access can leave an initialized zero. A running whole CPU package cannot be zero watts.
            if (cpu == 0) cpu = null;
            bool amdCpu = cpus.Any(h => h.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase)) ||
                System.Runtime.Intrinsics.X86.X86Base.IsSupported && System.Runtime.Intrinsics.X86.X86Base.CpuId(0, 0).Ebx == 0x68747541;
            double? gpu = gpus.Length == 0 ? null : HardwarePowerPolicy.SumComplete(gpus.Select(h => HardwarePowerPolicy.DiscreteGpu(h.HardwareType, h.GetType().Name, h.Name, amdCpu) switch
            {
                false => (double?)0,
                true => HardwarePowerPolicy.Package(h, "GPU Total", "GPU Board Power", "GPU Package", "GPU Power"),
                _ => null
            }));
            return new(cpu, gpu, cpu == null ? HardwareDriverAccess.Description(driver) :
                gpu == null ? "独立 GPU 未提供完整功耗，或无法确认是否为核显，暂不能计算总功耗。" : "");
        }
        catch (Exception ex)
        {
            Close(); _retryAfter = DateTime.UtcNow.AddSeconds(30);
            return new(null, null, "硬件功耗读取失败：" + ex.Message);
        }
    }
    private static void Update(IHardware hardware) { hardware.Update(); foreach (var child in hardware.SubHardware) Update(child); }
    private void Close() { var computer = _computer; _computer = null; try { computer?.Close(); } catch { } }
    public void Dispose() { _disposed = true; Close(); }
}
