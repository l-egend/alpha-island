using AlphaIsland.PluginSdk;

namespace alpha_island.Services;

public sealed partial class BluetoothService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DeviceBatterySample> _batterySnapshots = new();
    internal IReadOnlyList<DeviceBatterySample> BatterySnapshots => _batterySnapshots.Values.Where(s => s.Device.Connected).ToArray();
    internal IReadOnlyList<DeviceBatterySample> DeviceBatterySamples => _batterySnapshots.Values.ToArray();
    private void StoreBatterySnapshot(string key, BluetoothDeviceInfo info)
    {
        // Retain a bounded disconnected tombstone so an earbud plugin's old
        // per-ear readings cannot resurrect a device Windows has disconnected.
        _batterySnapshots[key] = new("bluetooth", new(key, info.Name, DeviceBatteryTransport.Bluetooth,
            info.Category switch { BluetoothDeviceCategory.Audio => DeviceBatteryKind.Audio,
                BluetoothDeviceCategory.Keyboard => DeviceBatteryKind.Keyboard, BluetoothDeviceCategory.Mouse => DeviceBatteryKind.Mouse,
                BluetoothDeviceCategory.Gamepad => DeviceBatteryKind.Gamepad, _ => DeviceBatteryKind.Other },
            info.IsConnected ? info.BatteryPercent : null, info.IsConnected,
            Detail: !info.IsConnected ? "Windows 已确认断开" : info.BatteryPercent.HasValue ? "Windows 蓝牙电量" : "设备尚未上报电量",
            PhysicalId: key), DateTimeOffset.UtcNow);
        while (_batterySnapshots.Count > 256)
        {
            var oldest = _batterySnapshots.Where(pair => !pair.Value.Device.Connected).MinBy(pair => pair.Value.UpdatedAt);
            if (oldest.Key == null) break;
            _batterySnapshots.TryRemove(oldest.Key, out _);
        }
    }
}
