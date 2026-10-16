using System.Text.RegularExpressions;

namespace AlphaIsland.Hardware;

/// <summary>Aggregation and energy conversion are independent of native handles and the UI.</summary>
public static class HardwarePowerPolicy
{
    private static readonly Regex PackageChannel = new(@"\ARAPL_Package\d+_PKG\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static bool IsCpuPackageChannel(string name) => PackageChannel.IsMatch(name);
    public static double? ValidWatts(double? value) => value is >= 0 && double.IsFinite(value.Value) ? value : null;
    public static double? SumComplete(IEnumerable<double?> values)
    {
        double total = 0;
        foreach (double? value in values) { if (ValidWatts(value) is not double watts) return null; total += watts; }
        return ValidWatts(total);
    }
    // EMI picowatt-hours / 100 ns = 0.036 W. Reject resets and gaps after sleep instead of inventing spikes.
    public static double? EnergyWatts(ulong energy0, ulong time0, ulong energy1, ulong time1)
    {
        if (energy1 <= energy0 || time1 <= time0 || time1 - time0 > (ulong)HardwarePowerReading.MaximumAge.Ticks) return null;
        return ValidWatts((energy1 - energy0) * 0.036 / (time1 - time0));
    }
}
