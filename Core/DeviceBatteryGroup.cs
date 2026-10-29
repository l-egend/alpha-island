using L = AlphaIsland.PluginSdk.Localization.UiText;
using AlphaIsland.PluginSdk;

namespace alpha_island.Core;

internal enum DeviceBatteryPart { Whole, Left, Right, Case }
internal sealed record DeviceBatteryReading(DeviceBatterySample Sample, DeviceBatteryPart Part);
internal sealed record DeviceBatteryGroup(string Key, string Name, IReadOnlyList<DeviceBatteryReading> Readings)
{
    internal IReadOnlyList<DeviceBatteryReading> Components => Readings
        .Where(r => r.Part != DeviceBatteryPart.Whole || !Readings.Any(other => other.Part is DeviceBatteryPart.Left or DeviceBatteryPart.Right))
        .GroupBy(r => r.Part).Select(g => g.OrderByDescending(r => r.Sample.Device.Percent.HasValue)
            .ThenByDescending(r => r.Sample.Source != "bluetooth").ThenByDescending(r => r.Sample.UpdatedAt).First()).OrderBy(r => r.Part).ToArray();

    internal DeviceBatterySample Display
    {
        get
        {
            var components = Components;
            var ears = components.Where(r => r.Part is DeviceBatteryPart.Left or DeviceBatteryPart.Right && r.Sample.Device.Percent.HasValue).ToArray();
            var chosen = (ears.Length > 0 ? ears : components.ToArray())
                .OrderByDescending(r => r.Sample.Device.Percent.HasValue).ThenBy(r => r.Part == DeviceBatteryPart.Case).ThenBy(r => r.Sample.Device.Percent ?? 101).First().Sample;
            return chosen with { Device = chosen.Device with { Name = Name, Charging = components.Any(r => r.Sample.Device.Charging) } };
        }
    }
    internal static string PartName(DeviceBatteryPart part) => part switch
    { DeviceBatteryPart.Left => L.T("左耳"), DeviceBatteryPart.Right => L.T("右耳"), DeviceBatteryPart.Case => L.T("耳机盒"), _ => L.T("电量") };
}
