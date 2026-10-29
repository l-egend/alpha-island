using L = AlphaIsland.PluginSdk.Localization.UiText;
using AlphaIsland.PluginSdk;

namespace AlphaIsland.Plugins.DeviceBattery;

/// <summary>Classic Vader 4 Pro identity query, without changing the controller's USB mode.</summary>
internal static class FlydigiBatteryReader
{
    internal static bool Supports(HidInterface device) => device.Vid == 0x04B4 && device.Pid == 0x2412
        && device.Caps.UsagePage == 0xFFA0 && device.Caps.InputBytes == 32 && device.Caps.OutputBytes is >= 12 and <= 64;

    internal sealed record Reading(string Level, bool Charging, bool Wireless, byte Raw);

    internal static Reading? Parse(ReadOnlySpan<byte> response)
    {
        if (response.Length != 32 || response[0] != 4 || response[1] != 0xFF || response[15] != 0xEC
            || response[3] is not (85 or 91 or 105) || response[13] > 1) return null;
        byte raw = response[11];
        int state = raw >> 4, level = raw & 15;
        if (state > 1 || level > 5) return null;
        // The f4 firmware uses a model-specific icon scale, not a linear percentage.
        // Space Station maps raw 3 to icon 2 and raw 5 to the charging icon 6.
        bool charging = state == 1 || level == 5;
        int icon = level == 3 ? 2 : level;
        return new(charging ? "" : icon == 0 ? "" : L.T("档位 ") + icon, charging, response[13] == 0, raw);
    }

    internal static async Task<PluginDeviceBattery?> ReadAsync(HidInterface device, CancellationToken token)
    {
        using var session = new HidSession(device);
        byte[] query = new byte[device.Caps.OutputBytes]; query[0] = 5; query[1] = 0xEC;
        // A receiver can miss the first info query while waking up.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var response = await session.Query(query, bytes => Parse(bytes) != null, token, 400);
            if (response == null || Parse(response) is not { } reading) continue;
            return new(device.PhysicalId, L.T("飞智 黑武士 4 Pro"),
                reading.Wireless ? DeviceBatteryTransport.WirelessReceiver : DeviceBatteryTransport.UsbOrReceiver,
                DeviceBatteryKind.Gamepad, null, true, reading.Charging,
                L.F($"飞智 f4 电量档位，非精确百分比（原始值 0x{reading.Raw:X2}）"), device.PhysicalId, reading.Level);
        }
        return null;
    }
}
