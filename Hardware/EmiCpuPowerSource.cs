using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AlphaIsland.Hardware;

/// <summary>Uses the existing Windows energy meter interface with read-only, ordinary access.</summary>
internal sealed class EmiCpuPowerSource : IComponentPowerSource
{
    private sealed record Package(int Index, string Name)
    {
        internal (ulong Energy, ulong Time)? Previous;
    }
    private sealed record Meter(SafeFileHandle Handle, int Count, Package[] Packages) : IDisposable
    { public void Dispose() => Handle.Dispose(); }
    private readonly List<Meter> _meters = [];
    private DateTime _retryAfter;
    private string _unavailable = "设备未提供可读取的 CPU 封装能量计";
    private bool _disposed;
    private const string Source = "Windows EMI";

    public ComponentPowerReading Sample()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            if (_meters.Count == 0)
            {
                if (DateTime.UtcNow < _retryAfter) return new(null, _unavailable, Source);
                Discover();
                if (_meters.Count == 0)
                {
                    _retryAfter = DateTime.UtcNow.AddSeconds(30);
                    return new(null, _unavailable, Source);
                }
            }
            List<double?> values = [];
            string reason = "";
            foreach (var meter in _meters)
            {
                var bytes = Query(meter.Handle, 0x22400c, meter.Count * 16);
                if (bytes.Length != meter.Count * 16) throw new InvalidDataException("EMI 采样长度无效");
                foreach (var package in meter.Packages)
                {
                    int offset = package.Index * 16;
                    ulong energy = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
                    ulong time = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + 8));
                    double? watts = package.Previous is { } previous
                        ? HardwarePowerPolicy.EnergyWatts(previous.Energy, previous.Time, energy, time) : null;
                    reason = watts.HasValue ? reason : package.Previous == null
                        ? "正在建立 CPU 能量计基线，等待下一次采样" : "CPU 能量计已重置或采样间隔无效，等待新读数";
                    package.Previous = (energy, time);
                    values.Add(watts);
                }
            }
            return new(HardwarePowerPolicy.SumComplete(values), reason, Source);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidDataException or OverflowException)
        {
            CloseMeters();
            _retryAfter = DateTime.UtcNow.AddSeconds(30);
            _unavailable = "CPU 能量计无法读取：" + ex.Message;
            return new(null, _unavailable, Source);
        }
    }

    private void Discover()
    {
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in DevicePaths())
        {
            SafeFileHandle? handle = null;
            try
            {
                handle = CreateFileW(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                byte[] versionBytes = Query(handle, 0x224000, 2), sizeBytes = Query(handle, 0x224004, 4);
                if (versionBytes.Length != 2 || sizeBytes.Length != 4) throw new InvalidDataException("EMI 信息长度无效");
                ushort version = BinaryPrimitives.ReadUInt16LittleEndian(versionBytes);
                var metadata = EmiMetadata.Parse(version, Query(handle, 0x224008, checked((int)BinaryPrimitives.ReadUInt32LittleEndian(sizeBytes))));
                var selected = metadata.Channels.Select((channel, index) => (channel, index))
                    .Where(x => x.channel.Unit == 0 && HardwarePowerPolicy.IsCpuPackageChannel(x.channel.Name) && packages.Add(x.channel.Name))
                    .Select(x => new Package(x.index, x.channel.Name)).ToArray();
                if (selected.Length == 0) continue;
                _meters.Add(new(handle, metadata.Channels.Length, selected));
                handle = null;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidDataException or OverflowException)
            { _unavailable = "CPU 能量计无法读取：" + ex.Message; }
            finally { handle?.Dispose(); }
        }
    }

    private static byte[] Query(SafeFileHandle handle, uint code, int size)
    {
        if (size is < 1 or > 65536) throw new InvalidDataException("EMI 缓冲区大小无效");
        byte[] bytes = new byte[size];
        if (!DeviceIoControl(handle, code, IntPtr.Zero, 0, bytes, (uint)size, out uint returned, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (returned > size) throw new InvalidDataException("EMI 返回长度无效");
        if (returned != size) Array.Resize(ref bytes, (int)returned);
        return bytes;
    }

    private static IEnumerable<string> DevicePaths()
    {
        var guid = new Guid("45BD8344-7ED6-49cf-A440-C276C933B053");
        IntPtr devices = SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, 0x12);
        if (devices == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for (uint index = 0; index < 32; index++)
            {
                var data = new InterfaceData { Size = Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref guid, index, ref data))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259) yield break;
                    throw new Win32Exception(error);
                }
                SetupDiGetDeviceInterfaceDetailW(devices, ref data, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                if (required is < 8 or > 65536) throw new InvalidDataException("EMI 设备路径长度无效");
                IntPtr detail = Marshal.AllocHGlobal((int)required);
                string path;
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(devices, ref data, detail, required, out _, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4)) ?? throw new InvalidDataException("EMI 设备路径为空");
                }
                finally { Marshal.FreeHGlobal(detail); }
                yield return path;
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }
    }

    private void CloseMeters() { foreach (var meter in _meters) meter.Dispose(); _meters.Clear(); }
    public void Dispose() { if (_disposed) return; _disposed = true; CloseMeters(); }

    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceData { public int Size; public Guid ClassGuid; public int Flags; public IntPtr Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devices, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr devices, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr info);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputSize, byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
}
