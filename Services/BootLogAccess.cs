using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using alpha_island.Core;

namespace alpha_island.Services;

/// <summary>A one-time consent grants the launching user read access to one boot performance channel.</summary>
internal static class BootLogAccess
{
    internal const string Channel = "Microsoft-Windows-Diagnostics-Performance/Operational";

    internal static bool CanRead()
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(Channel, PathType.LogName, "*[System[EventID=100]]") { ReverseDirection = true });
            using var record = reader.ReadEvent(TimeSpan.FromSeconds(1));
            return true; // An empty, readable channel is valid; the current boot event may arrive later.
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    internal static async Task EnsureReadAccessAsync()
    {
        if (await Task.Run(CanRead)) return;
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前应用路径");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = "--grant-boot-log-read " + Environment.ProcessId
        };
        using var child = Process.Start(info) ?? throw new InvalidOperationException("未能启动 Windows 权限申请");
        await child.WaitForExitAsync();
        if (child.ExitCode != 0 || !await Task.Run(CanRead))
            throw new InvalidOperationException("未能取得开机性能日志的只读权限");
    }

    internal static string GrantReadDescriptor(string original, SecurityIdentifier user)
    {
        var descriptor = new CommonSecurityDescriptor(false, false, original);
        var acl = descriptor.DiscretionaryAcl ?? throw new InvalidOperationException("启动日志缺少明确的访问控制列表");
        foreach (GenericAce ace in acl)
            if (ace is CommonAce existing && existing.AceQualifier == AceQualifier.AccessAllowed && existing.SecurityIdentifier == user
                && (existing.AccessMask & 1) != 0) return original;
        acl.AddAccess(AccessControlType.Allow, user, 1, InheritanceFlags.None, PropagationFlags.None);
        return descriptor.GetSddlForm(AccessControlSections.All);
    }

    // The target SID is read from the verified parent process token, never accepted from a CLI argument.
    internal static int GrantReadToParent(int parentId)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
            using var parent = Process.GetProcessById(parentId);
            using var current = Process.GetCurrentProcess();
            if (parent.HasExited || parent.SessionId != current.SessionId || parentId == current.Id ||
                !string.Equals(Path.GetFullPath(parent.MainModule!.FileName), Path.GetFullPath(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase)) return 5;
            if (!OpenProcessToken(parent.Handle, 0x8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            using (var target = new WindowsIdentity(token.DangerousGetHandle()))
            using (var config = new EventLogConfiguration(Channel))
            {
                var user = target.User ?? throw new InvalidOperationException("无法识别发起授权的用户");
                string original = config.SecurityDescriptor;
                string granted = GrantReadDescriptor(original, user);
                if (granted != original) { config.SecurityDescriptor = granted; config.SaveChanges(); }
            }
            return 0;
        }
        catch (Exception ex) { IslandLog.Error("BootLogAccess", ex); return 1; }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
}
