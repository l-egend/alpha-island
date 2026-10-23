using System.Buffers.Binary;
using AlphaIsland.PluginSdk;

namespace AlphaIsland.Plugins.DeviceBattery;

/// <summary>K5 Ultra read-device-info command from the official M HUB protocol.</summary>
internal static class MchoseBatteryReader
{
    internal static bool Supports(HidInterface device) => device.Vid == 0x3837 && device.Pid is 0x1018 or 0x4038
        && device.Name.Equals("MCHOSE K5 Ultra", StringComparison.OrdinalIgnoreCase)
        && device.Caps.UsagePage == 0xFF01 && device.Caps.InputBytes == 64 && device.Caps.OutputBytes == 64;

    internal static byte[] Query()
    {
        byte[] report = new byte[64];
        report[0] = 0x4D; report[1] = 1; report[2] = 1; report[5] = 9; report[8] = 8;
        return report;
    }

    internal sealed record Reading(int Percent, bool Charging);

    internal static Reading? Parse(ReadOnlySpan<byte> report)
    {
        if (report.Length < 23 || report[0] != 0x4D || report[1] != 1 || report[2] > 1
            || report[4] != 0 || report[5] != 9 || report[6] != 0 || report[7] != 0) return null;
        int size = report[3];
        if (size < 14 || size > report.Length - 9) return null;
        if (report[2] == 1)
        {
            byte checksum = 0; foreach (byte value in report.Slice(2, size + 6)) checksum ^= value;
            if (checksum != report[8 + size]) return null;
        }
        var body = report.Slice(8, size);
        if (BinaryPrimitives.ReadUInt16LittleEndian(body) != 0x3837
            || BinaryPrimitives.ReadUInt16LittleEndian(body[2..]) is not (0x4038 or 0x1018 or 0x1014)
            || body[12] > 100 || body[11] > 2) return null;
        return new(body[12], body[11] == 1);
    }

    internal static async Task<PluginDeviceBattery?> ReadAsync(HidInterface device, CancellationToken token)
    {
        using var session = new HidSession(device);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var response = await session.Query(Query(), bytes => bytes.Length >= 6 && bytes[0] == 0x4D && bytes[4] == 0 && bytes[5] == 9, token, 800);
            if (response != null)
            {
                HidSession.Trace?.Invoke("MCHOSE info: " + Convert.ToHexString(response.AsSpan(0, Math.Min(32, response.Length))));
                if (Parse(response) is { } reading)
                    return new(device.PhysicalId, "MCHOSE K5 Ultra",
                        device.Pid == 0x1018 ? DeviceBatteryTransport.WirelessReceiver : DeviceBatteryTransport.UsbOrReceiver,
                        DeviceBatteryKind.Mouse, reading.Percent, true, reading.Charging,
                        "迈从官方 M HUB 设备信息协议上报", device.PhysicalId);
            }
            if (attempt == 0) await Task.Delay(50, token);
        }
        return null;
    }
}
