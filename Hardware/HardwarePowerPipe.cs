using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AlphaIsland.Hardware;

/// <summary>Authorizes the user SID across UAC, rather than comparing elevated token owners.</summary>
public static class HardwarePowerPipe
{
    public static NamedPipeServerStream CreateReader(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("无法识别当前用户");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
    }
    public static NamedPipeClientStream CreateWriter(string name) => new(".", name, PipeDirection.Out,
        PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
    public static void ValidateReader(NamedPipeClientStream pipe, int expectedParent)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        if (!Equals(owner, identity.User) || !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint process) || process != expectedParent)
            throw new UnauthorizedAccessException("硬件读取管道的用户或父进程身份不符");
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
