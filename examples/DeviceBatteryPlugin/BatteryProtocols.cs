using System.Buffers.Binary;
using System.Text;
using AlphaIsland.PluginSdk;

namespace AlphaIsland.Plugins.DeviceBattery;

internal static class BatteryProtocols
{
    internal static byte[] DarkrimQuery(byte sequence)
    {
        byte[] frame = new byte[65]; frame[1] = 0xFE; frame[2] = sequence; frame[3] = 0x10; frame[5] = 2;
        frame[8] = 2; frame[10] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), Crc(frame.AsSpan(1, 11))); return frame;
    }
    internal static ushort Crc(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0xffff;
        foreach (byte b in bytes) { crc ^= (ushort)(b << 8); for (int bit = 0; bit < 8; bit++) crc = (ushort)((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x1021 : 0)); }
        return crc;
    }
    internal static int? ParseDarkrim(ReadOnlySpan<byte> frame, byte sequence)
    {
        if (frame.Length < 14 || frame[0] != 0 || frame[1] != 0xFE || frame[2] != sequence || frame[3] is not (0x10 or 0x11)) return null;
        int length = BinaryPrimitives.ReadUInt16LittleEndian(frame[8..]);
        if (length > frame.Length - 12 || BinaryPrimitives.ReadUInt16BigEndian(frame[(10 + length)..]) != Crc(frame.Slice(1, 9 + length))) return null;
        int? percent = null;
        for (int i = 10; i < 10 + length;)
        {
            if (i + 2 > 10 + length || i + 2 + frame[i + 1] > 10 + length) return null;
            int count = frame[i + 1]; if (frame[i] == 1 && count >= 1 && frame[i + 2] <= 100) percent = frame[i + 2];
            i += 2 + count;
        }
        return percent;
    }
    internal static int? ParseAtk(ReadOnlySpan<byte> frame) => frame.Length >= 8 && frame[0] == 8 && frame[1] == 0x72 && frame[5] == 7 && frame[7] <= 100 ? frame[7] : null;

    internal static int? EstimateLogitechVoltage(int mv)
    {
        if (mv is < 3500 or > 4500) return null;
        // Sparse measured voltage/SOC anchors documented by the Linux HID++ driver.
        (int V, int P)[] curve = [(4186,100),(4067,90),(3989,80),(3922,70),(3859,60),(3811,50),(3778,40),(3751,30),(3717,20),(3671,10),(3537,1)];
        if (mv >= curve[0].V) return 100;
        for (int i = 1; i < curve.Length; i++) if (mv >= curve[i].V)
            return (int)Math.Round(curve[i].P + (mv - curve[i].V) * (curve[i-1].P - curve[i].P) / (double)(curve[i-1].V - curve[i].V));
        return 0;
    }

    internal static async Task<IReadOnlyList<PluginDeviceBattery>> ReadLogitech(HidInterface device, CancellationToken token)
    {
        using var session = new HidSession(device); var results = new List<PluginDeviceBattery>();
        int slots = device.Pid is 0xC539 or 0xC547 or 0xC53F ? 1 : 6;
        for (byte slot = 1; slot <= slots; slot++)
        {
            async Task<byte[]?> Call(byte feature, byte function, params byte[] args)
            {
                byte[] request = new byte[Math.Max(20, (int)device.Caps.OutputBytes)];
                request[0] = 0x11; request[1] = slot; request[2] = feature; request[3] = (byte)((function << 4) | 0xB);
                args.AsSpan(0, Math.Min(args.Length, 16)).CopyTo(request.AsSpan(4));
                return await session.Query(request, r => r.Length >= 7 && r[0] is 0x10 or 0x11 && r[1] == slot && r[2] == feature && r[3] == request[3]
                    && (feature != 0 || function != 1 || r[6] == request[6]), token, 350);
            }
            async Task<byte> Feature(ushort id) => (await Call(0, 0, (byte)(id >> 8), (byte)id))?[4] ?? 0;
            // The receiver can be present before the peripheral's HID++ session is ready.
            // Negotiate the protocol first; this is also the only wake-up query we issue.
            bool ready = false;
            for (int attempt = 0; attempt < 2 && !ready; attempt++)
            {
                byte nonce = (byte)Random.Shared.Next(1, 256);
                var ping = await Call(0, 1, 0, 0, nonce);
                ready = ping is { Length: >= 7 } && ping[4] >= 2 && ping[6] == nonce;
                if (!ready && attempt == 0) await Task.Delay(100, token);
            }
            if (!ready) continue;
            byte batteryFeature = 0; ushort kind = 0;
            foreach (ushort id in new ushort[] {0x1004,0x1000,0x1001}) { batteryFeature = await Feature(id); if (batteryFeature != 0) { kind = id; break; } }
            if (batteryFeature == 0) continue;
            string name = "罗技无线设备";
            byte nameFeature = await Feature(5);
            if (nameFeature != 0 && await Call(nameFeature, 0) is { } nameCount)
            {
                int length = Math.Min((int)nameCount[4], 128); var bytes = new List<byte>();
                for (int offset = 0; offset < length; offset += 16)
                {
                    var chunk = await Call(nameFeature, 1, (byte)offset); if (chunk == null) break;
                    bytes.AddRange(chunk.Skip(4).Take(Math.Min(length - offset, chunk.Length - 4)));
                }
                if (bytes.Count > 0) name = Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\0');
            }
            int? percent = null, mv = null; bool charging = false, estimated = false; string level = "", detail = "Logitech HID++";
            if (kind == 0x1004)
            {
                var caps = await Call(batteryFeature, 0); var data = await Call(batteryFeature, 1);
                if (data != null)
                {
                    if (caps != null && (caps[5] & 2) != 0 && data[4] <= 100) percent = data[4];
                    else level = (data[5] & 8) != 0 ? "充足" : (data[5] & 4) != 0 ? "中" : (data[5] & 2) != 0 ? "低" : (data[5] & 1) != 0 ? "极低" : "";
                    charging = data[6] is 1 or 2;
                }
            }
            else if (await Call(batteryFeature, 0) is { } data)
            {
                if (kind == 0x1000) { percent = data[4] <= 100 ? data[4] : null; charging = data[6] is 1 or 2; }
                else { mv = (data[4] << 8) | data[5]; percent = EstimateLogitechVoltage(mv.Value); estimated = percent.HasValue;
                    charging = (data[6] & 0x80) != 0 && (data[6] & 7) == 0; detail = "HID++ 电压估算，非设备上报的精确百分比"; }
            }
            results.Add(new(device.PhysicalId + "/" + slot, name, DeviceBatteryTransport.WirelessReceiver, DeviceBatteryKind.Mouse,
                percent, true, charging, detail, device.PhysicalId + "/" + slot, level, estimated, mv));
        }
        return results;
    }
}
