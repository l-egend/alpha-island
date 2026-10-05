using System.Diagnostics;
using System.Runtime.InteropServices;

namespace alpha_island.Interop;

internal static class NotificationAppLauncher
{
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
        [PreserveSig] int ActivateForFile(IntPtr appId, IntPtr items, IntPtr verb, out uint processId);
        [PreserveSig] int ActivateForProtocol(IntPtr appId, IntPtr items, out uint processId);
    }
    public static bool TryOpen(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || appId.IndexOfAny(new[] { '\r', '\n', '"', '\\', '/' }) >= 0) return false;
        object? manager = null;
        try
        {
            manager = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"))!);
            if (((IApplicationActivationManager)manager!).ActivateApplication(appId, "", 0, out _) >= 0) return true;
        }
        catch { }
        finally { if (manager != null && Marshal.IsComObject(manager)) Marshal.ReleaseComObject(manager); }
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add("shell:AppsFolder\\" + appId); Process.Start(start); return true;
        }
        catch { return false; }
    }
}
