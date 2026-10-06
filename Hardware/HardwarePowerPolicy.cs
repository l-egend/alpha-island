using LibreHardwareMonitor.Hardware;
using System.Text.RegularExpressions;

namespace AlphaIsland.Hardware;

/// <summary>Power aggregation rules are independent of process privileges and the UI.</summary>
public static class HardwarePowerPolicy
{
    public static bool? DiscreteGpu(HardwareType type, string implementation, string name, bool amdCpu) => type switch
    {
        HardwareType.GpuNvidia => true,
        HardwareType.GpuIntel when implementation == "IntelIntegratedGpu" => false,
        HardwareType.GpuIntel when implementation == "IntelDiscreteGpu" => true,
        HardwareType.GpuAmd when !amdCpu || Regex.IsMatch(name, @"Radeon(?:\(TM\))?\s+RX\s+\d{3,4}\b", RegexOptions.IgnoreCase) => true,
        _ => null
    };
    public static double? ValidWatts(double? value) => value is >= 0 && double.IsFinite(value.Value) ? value : null;
    public static double? SumComplete(IEnumerable<double?> values)
    {
        double total = 0;
        foreach (double? value in values) { if (ValidWatts(value) is not double watts) return null; total += watts; }
        return ValidWatts(total);
    }
    public static double? Package(IHardware hardware, params string[] names)
    {
        foreach (string name in names)
        {
            var sensor = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            // A named but unavailable sensor must not hide another valid whole-package sensor.
            if (sensor != null && ValidWatts(sensor.Value) is double watts) return watts;
        }
        return null;
    }
}
