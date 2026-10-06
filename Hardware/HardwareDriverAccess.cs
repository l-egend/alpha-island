using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace AlphaIsland.Hardware;

public enum HardwareDriverState { Missing, Outdated, Denied, Ready, Unavailable }

public static class HardwareDriverAccess
{
    // LHM caches its installed version in a static constructor. Query live so setup works without restarting the UI.
    public static Version? InstalledVersion
    {
        get
        {
            using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return Version.TryParse(key?.GetValue("DisplayVersion") as string, out var version) ? version : null;
        }
    }
    public static bool IsInstalled => InstalledVersion != null;
    public static bool NeedsInstallation => InstalledVersion is not Version version || version < new Version(2, 2, 0);
    public static HardwareDriverState Probe()
    {
        if (!IsInstalled) return HardwareDriverState.Missing;
        if (NeedsInstallation) return HardwareDriverState.Outdated;
        using var handle = CreateFileW(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        return !handle.IsInvalid ? HardwareDriverState.Ready : Marshal.GetLastWin32Error() == 5 ? HardwareDriverState.Denied : HardwareDriverState.Unavailable;
    }
    public static string Description(HardwareDriverState state) => state switch
    {
        HardwareDriverState.Missing => "尚未安装硬件驱动。请在设置 → 系统状态 → 硬件功耗中启用读取。",
        HardwareDriverState.Outdated => "硬件驱动版本较旧，请在硬件功耗设置中更新至 2.2 或以上。",
        HardwareDriverState.Denied => "CPU 读取需要管理员权限。请在硬件功耗设置中启动独立读取进程。",
        HardwareDriverState.Unavailable => "硬件驱动已安装但设备不可用，请检查驱动状态或重新启动电脑。",
        _ => "CPU 封装功耗尚未就绪，或硬件未提供该传感器。"
    };
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
