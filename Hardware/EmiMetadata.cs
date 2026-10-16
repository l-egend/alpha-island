using System.Buffers.Binary;
using System.Text;

namespace AlphaIsland.Hardware;

internal sealed record EmiChannel(string Name, uint Unit);
internal sealed record EmiMetadata(string Oem, string Model, EmiChannel[] Channels)
{
    // Windows SDK shared/emi.h: variable-length UTF-16 channel names have no padding.
    internal static EmiMetadata Parse(ushort version, ReadOnlySpan<byte> data)
    {
        static void Require(ReadOnlySpan<byte> bytes, int offset, int count)
        {
            if (offset < 0 || count < 0 || offset > bytes.Length - count)
                throw new InvalidDataException("EMI 元数据长度无效");
        }
        static string Name(ReadOnlySpan<byte> bytes, int offset, int count)
        {
            Require(bytes, offset, count);
            if ((count & 1) != 0) throw new InvalidDataException("EMI 名称长度无效");
            return Encoding.Unicode.GetString(bytes.Slice(offset, count)).TrimEnd('\0');
        }
        if (data.Length > 65536) throw new InvalidDataException("EMI 元数据过大");
        if (version == 1)
        {
            Require(data, 0, 72);
            return new(Name(data, 4, 32), Name(data, 36, 32),
                [new(Name(data, 72, BinaryPrimitives.ReadUInt16LittleEndian(data[70..])), BinaryPrimitives.ReadUInt32LittleEndian(data))]);
        }
        if (version != 2) throw new InvalidDataException($"不支持的 EMI 版本 {version}");
        Require(data, 0, 68);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data[66..]);
        if (count is < 1 or > 256) throw new InvalidDataException("EMI 通道数量无效");
        var channels = new EmiChannel[count];
        int offset = 68;
        for (int i = 0; i < count; i++)
        {
            Require(data, offset, 6);
            int size = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 4)..]);
            channels[i] = new(Name(data, offset + 6, size), BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]));
            offset += 6 + size;
        }
        return new(Name(data, 0, 32), Name(data, 32, 32), channels);
    }
}
