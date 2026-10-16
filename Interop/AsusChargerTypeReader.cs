using System.Buffers.Binary;
using System.Runtime.InteropServices;
using alpha_island.Services;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace alpha_island.Interop;

/// <summary>Original fixed-purpose binding to the installed ASUS status interface. No hardware settings are exposed.</summary>
internal static class AsusChargerTypeReader
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputSize,
        byte[] output, uint outputSize, out uint returned, IntPtr overlapped);

    internal static byte[] StatusRequest()
    {
        var buffer = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0x53545344); // DSTS only
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), 0x0012006C);
        return buffer;
    }
    internal static BatteryChargeType Decode(bool success, uint returned, ReadOnlySpan<byte> output)
    {
        if (!success || returned < 4 || output.Length < 4) return BatteryChargeType.Unknown;
        uint raw = BinaryPrimitives.ReadUInt32LittleEndian(output);
        if ((raw & 0x10000) == 0 || (raw & ~0x700FFu) != 0) return BatteryChargeType.Unknown;
        return (raw & 0xFF) switch { 1 => BatteryChargeType.StandardAC, 2 => BatteryChargeType.PD, _ => BatteryChargeType.Unknown };
    }
    internal static bool IsAsusManufacturer(string? manufacturer)
        => manufacturer?.Contains("ASUS", StringComparison.OrdinalIgnoreCase) == true
            || manufacturer?.Contains("ASUSTeK", StringComparison.OrdinalIgnoreCase) == true;

    internal static BatteryChargeType Read()
    {
        try
        {
            string? manufacturer = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS", "SystemManufacturer", null) as string;
            if (!IsAsusManufacturer(manufacturer)) return BatteryChargeType.Unknown;
            // FILE_ANY_ACCESS query; sharing writes does not request write permission.
            using var handle = CreateFile(@"\\.\ATKACPI", 0, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (handle.IsInvalid) return BatteryChargeType.Unknown;
            var input = StatusRequest(); var output = new byte[16];
            bool success = DeviceIoControl(handle, 0x0022240C, input, (uint)input.Length, output, (uint)output.Length, out uint returned, IntPtr.Zero);
            return Decode(success, returned, output);
        }
        catch { return BatteryChargeType.Unknown; }
    }
}
