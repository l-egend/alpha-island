namespace AlphaIsland.PluginSdk;

public enum DeviceBatteryTransport { Bluetooth, WirelessReceiver, UsbOrReceiver }
public enum DeviceBatteryKind { Other, Mouse, Keyboard, Audio, Gamepad }

/// <summary>A complete device snapshot. Null percent means unknown, never zero.</summary>
public sealed record PluginDeviceBattery(string Id, string Name, DeviceBatteryTransport Transport,
    DeviceBatteryKind Kind, int? Percent, bool Connected, bool Charging = false,
    string Detail = "", string PhysicalId = "", string Level = "", bool Estimated = false, int? Millivolts = null);

public sealed record DeviceBatterySample(string Source, PluginDeviceBattery Device, DateTimeOffset UpdatedAt, bool Expires = true);
