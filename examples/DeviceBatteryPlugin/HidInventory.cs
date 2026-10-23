using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AlphaIsland.Plugins.DeviceBattery;

internal sealed record HidInterface(string Path, string PhysicalId, string Name, ushort Vid, ushort Pid, HidNative.Caps Caps);
internal static class HidInventory
{
    internal static IReadOnlyList<HidInterface> Enumerate()
    {
        Guid hid = new("4D1E55B2-F16F-11CF-88CB-001111000030");
        IntPtr list = HidNative.SetupDiGetClassDevsW(ref hid, null, IntPtr.Zero, 0x12);
        if (list == new IntPtr(-1)) return [];
        var devices = new List<HidInterface>();
        try
        {
            for (uint index = 0; index < 512; index++)
            {
                var data = new HidNative.InterfaceData { Size = Marshal.SizeOf<HidNative.InterfaceData>() };
                if (!HidNative.SetupDiEnumDeviceInterfaces(list, IntPtr.Zero, ref hid, index, ref data)) break;
                HidNative.SetupDiGetDeviceInterfaceDetailW(list, ref data, IntPtr.Zero, 0, out uint length, IntPtr.Zero);
                if (length < 8 || length > 32768) continue;
                IntPtr detail = Marshal.AllocHGlobal((int)length), devInfo = Marshal.AllocHGlobal(Marshal.SizeOf<HidNative.DeviceInfo>());
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    Marshal.StructureToPtr(new HidNative.DeviceInfo { Size = Marshal.SizeOf<HidNative.DeviceInfo>() }, devInfo, false);
                    if (!HidNative.SetupDiGetDeviceInterfaceDetailW(list, ref data, detail, length, out _, devInfo)) continue;
                    string path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    using var handle = HidNative.Open(path, 0, false);
                    if (handle.IsInvalid || !HidNative.GetAttributes(handle, out var attrs)) continue;
                    if (!HidNative.HidD_GetPreparsedData(handle, out IntPtr parsed)) continue;
                    try
                    {
                        if (HidNative.HidP_GetCaps(parsed, out var caps) != HidNative.Success) continue;
                        var name = new StringBuilder(256); HidNative.HidD_GetProductString(handle, name, 512);
                        byte[] id = new byte[16]; var key = new HidNative.PropertyKey { Format = new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), Id = 2 };
                        string identity = HidNative.SetupDiGetDevicePropertyW(list, devInfo, ref key, out uint type, id, 16, out uint size, 0)
                            && type == 0xD && size == 16 && new Guid(id) != Guid.Empty ? new Guid(id).ToString("D") : path;
                        devices.Add(new(path, identity, name.ToString().Trim(), attrs.Vid, attrs.Pid, caps));
                    }
                    finally { HidNative.HidD_FreePreparsedData(parsed); }
                }
                finally { Marshal.FreeHGlobal(detail); Marshal.FreeHGlobal(devInfo); }
            }
        }
        finally { HidNative.SetupDiDestroyDeviceInfoList(list); }
        return devices;
    }
    internal static int? ReadStandardBattery(HidInterface device)
    {
        if (device.Caps.FeatureBytes is 0 or > 4096 || device.Caps.FeatureValues is 0 or > 256) return null;
        using var handle = HidNative.Open(device.Path, 0, false);
        if (handle.IsInvalid || !HidNative.HidD_GetPreparsedData(handle, out IntPtr parsed)) return null;
        IntPtr values = Marshal.AllocHGlobal(device.Caps.FeatureValues * 72);
        try
        {
            ushort count = device.Caps.FeatureValues;
            if (HidNative.HidP_GetValueCaps(2, values, ref count, parsed) != HidNative.Success) return null;
            for (int i = 0; i < Math.Min(count, device.Caps.FeatureValues); i++)
            {
                IntPtr cap = values + i * 72;
                ushort page = (ushort)Marshal.ReadInt16(cap), usage = (ushort)Marshal.ReadInt16(cap, 56);
                bool range = Marshal.ReadByte(cap, 12) != 0;
                if (page != 6 || (range ? !(usage <= 0x20 && Marshal.ReadInt16(cap, 58) >= 0x20) : usage != 0x20)) continue;
                byte[] report = new byte[device.Caps.FeatureBytes]; report[0] = Marshal.ReadByte(cap, 2);
                if (!HidNative.HidD_GetFeature(handle, report, report.Length)) continue;
                if (HidNative.HidP_GetUsageValue(2, page, (ushort)Marshal.ReadInt16(cap, 6), 0x20, out uint raw, parsed, report, report.Length) != HidNative.Success) continue;
                int min = Marshal.ReadInt32(cap, 40), max = Marshal.ReadInt32(cap, 44);
                if (min < 0 || max <= min || raw < min || raw > max) continue;
                return (int)Math.Round((raw - min) * 100d / (max - min));
            }
        }
        finally { Marshal.FreeHGlobal(values); HidNative.HidD_FreePreparsedData(parsed); }
        return null;
    }
}

internal static class HidNative
{
    internal const int Success = 0x110000;
    [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData { internal int Size; internal Guid Class; internal int Flags; internal IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct DeviceInfo { internal int Size; internal Guid Class; internal uint Instance; internal IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct PropertyKey { internal Guid Format; internal uint Id; }
    [StructLayout(LayoutKind.Sequential)] internal struct Attributes { internal int Size; internal ushort Vid, Pid, Version; }
    [StructLayout(LayoutKind.Sequential)] internal struct Caps
    {
        internal ushort Usage, UsagePage, InputBytes, OutputBytes, FeatureBytes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] internal ushort[] Reserved;
        internal ushort Links, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
    }
    internal static SafeFileHandle Open(string path, uint access, bool async) => CreateFileW(path, access, 3, IntPtr.Zero, 3, async ? 0x40000000u : 0, IntPtr.Zero);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr list, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr list, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);
    [DllImport("setupapi.dll")] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr list);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool SetupDiGetDevicePropertyW(IntPtr list, IntPtr device, ref PropertyKey key, out uint type, byte[] buffer, uint size, out uint required, uint flags);
    [DllImport("hid.dll", EntryPoint = "HidD_GetAttributes")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool AttributesNative(SafeFileHandle handle, ref Attributes value);
    internal static bool GetAttributes(SafeFileHandle handle, out Attributes value) { value = new() { Size = Marshal.SizeOf<Attributes>() }; return AttributesNative(handle, ref value); }
    [DllImport("hid.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetProductString(SafeFileHandle handle, [Out] StringBuilder text, int bytes);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll")] internal static extern int HidP_GetValueCaps(int type, IntPtr caps, ref ushort count, IntPtr data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetFeature(SafeFileHandle handle, [In, Out] byte[] buffer, int length);
    [DllImport("hid.dll")] internal static extern int HidP_GetUsageValue(int type, ushort page, ushort collection, ushort usage, out uint value, IntPtr data, byte[] report, int length);
}
