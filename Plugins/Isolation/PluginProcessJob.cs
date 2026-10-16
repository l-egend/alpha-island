using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace alpha_island.Plugins.Isolation;

internal sealed class PluginProcessJob : SafeHandleZeroOrMinusOneIsInvalid
{
    [StructLayout(LayoutKind.Sequential)] private struct Basic
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Io { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct Limits
    {
        public Basic BasicLimitInformation; public Io IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(PluginProcessJob job, int type, ref Limits value, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(PluginProcessJob job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    internal PluginProcessJob() : base(true)
    {
        SetHandle(CreateJobObject(IntPtr.Zero, null));
        if (IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new Limits { BasicLimitInformation = new Basic { LimitFlags = 0x2000 } };
        if (!SetInformationJobObject(this, 9, ref limits, Marshal.SizeOf<Limits>()))
        { int error = Marshal.GetLastWin32Error(); Dispose(); throw new Win32Exception(error); }
    }
    internal void Assign(System.Diagnostics.Process process)
    {
        if (!AssignProcessToJobObject(this, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    protected override bool ReleaseHandle() => CloseHandle(handle);
}
