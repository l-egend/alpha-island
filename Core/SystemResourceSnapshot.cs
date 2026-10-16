namespace alpha_island.Core;

/// <summary>One resource sample shared by the system card and both island sizes.</summary>
internal readonly record struct SystemResourceSnapshot(
    double? CpuPercent, double? MemoryPercent, double? CpuWatts, double? GpuWatts,
    double? TotalWatts, string MemoryDetail, string PowerDetail);
