using alpha_island.Services;

namespace alpha_island.Core;

/// <summary>One reminder at 20% and another at 10%; reset above 25% or on disconnect.
/// Unknown/stale disconnected battery readings must never generate a warning.</summary>
internal sealed class BluetoothLowBatteryPolicy
{
    private readonly Dictionary<string, int> _warned = new(StringComparer.OrdinalIgnoreCase);
    internal bool ShouldNotify(BluetoothDeviceInfo device)
    {
        if (!device.IsConnected) { _warned.Remove(device.Id); return false; }
        if (device.BatteryPercent is not (>= 0 and <= 100)) return false;
        int percent = device.BatteryPercent.Value;
        if (percent > 25) { _warned.Remove(device.Id); return false; }
        int band = percent <= 10 ? 2 : percent <= 20 ? 1 : 0;
        if (band == 0 || _warned.GetValueOrDefault(device.Id) >= band) return false;
        if (_warned.Count >= 128) _warned.Clear();
        _warned[device.Id] = band;
        return true;
    }
}
