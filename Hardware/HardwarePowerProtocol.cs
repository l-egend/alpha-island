using System.Buffers.Binary;
using System.Text.Json;

namespace AlphaIsland.Hardware;

/// <summary>Bounded, versioned snapshots; the reader process accepts no control commands.</summary>
public static class HardwarePowerProtocol
{
    public const int MaximumBytes = 8192;
    private static readonly JsonSerializerOptions JsonOptions = new() { IgnoreReadOnlyProperties = true };
    private sealed record Envelope(int Version, HardwarePowerReading Reading);
    public static byte[] Encode(HardwarePowerReading reading) => JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, reading), JsonOptions);
    public static HardwarePowerReading Decode(ReadOnlySpan<byte> bytes, DateTime now)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumBytes) throw new InvalidDataException("功耗消息长度不正确");
        var envelope = JsonSerializer.Deserialize<Envelope>(bytes) ?? throw new InvalidDataException("功耗消息为空");
        var reading = envelope.Reading;
        if (envelope.Version != 1 || reading == null || !reading.IsFresh(now) || reading.Reason == null || reading.Source == null ||
            reading.Reason.Length > 2000 || reading.Source.Length > 200 ||
            (reading.CpuWatts.HasValue && (HardwarePowerPolicy.ValidWatts(reading.CpuWatts) == null || reading.CpuWatts == 0)) ||
            (reading.GpuWatts.HasValue && HardwarePowerPolicy.ValidWatts(reading.GpuWatts) == null))
            throw new InvalidDataException("功耗消息无效或已过期");
        return reading;
    }
    public static async Task WriteAsync(Stream stream, HardwarePowerReading reading, CancellationToken token)
    {
        byte[] body = Encode(reading);
        if (body.Length > MaximumBytes) throw new InvalidDataException("功耗消息过长");
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(body, token); await stream.FlushAsync(token);
    }
    public static async Task<HardwarePowerReading> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumBytes) throw new InvalidDataException("功耗消息长度不正确");
        byte[] body = new byte[length]; await stream.ReadExactlyAsync(body, token);
        return Decode(body, DateTime.UtcNow);
    }
}
