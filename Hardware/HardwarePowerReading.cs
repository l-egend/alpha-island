namespace AlphaIsland.Hardware;

public sealed record HardwarePowerReading(double? CpuWatts, double? GpuWatts, string Reason)
{
    public static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(10);
    public DateTime SampledUtc { get; init; } = DateTime.UtcNow;
    public string Source { get; init; } = "Windows EMI + NVIDIA NVML";
    public bool IsFresh(DateTime now) => SampledUtc.Kind == DateTimeKind.Utc &&
        SampledUtc <= now.AddSeconds(1) && now - SampledUtc <= MaximumAge;
    public HardwarePowerReading FreshAt(DateTime now) => IsFresh(now) ? this :
        new(null, null, "功耗采样已过期，正在等待新读数") { Source = Source };
    // Package includes integrated graphics; only discrete GPU board power is added.
    public double? TotalWatts => HardwarePowerPolicy.ValidWatts(CpuWatts) is double cpu && cpu > 0 &&
        HardwarePowerPolicy.ValidWatts(GpuWatts) is double gpu ? cpu + gpu : null;
    public string Description => $"CPU 封装 {Format(CpuWatts)} · 独立 GPU {Format(GpuWatts)}\n" +
        (TotalWatts.HasValue ? "CPU＋GPU 总功耗；接电与电池供电均可读取。核显已包含在 CPU 封装中。" : Reason) + $"\n来源：{Source}";
    private static string Format(double? watts) => HardwarePowerPolicy.ValidWatts(watts) is double value ? $"{value:0.0} W" : "无法读取";
}

public interface IHardwarePowerSource : IDisposable
{
    HardwarePowerReading Sample();
}
