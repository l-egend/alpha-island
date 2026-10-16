using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;

internal static partial class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint extended, string cls, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);

    private static void NativeToastBannerUiChecks(alpha_island.MainWindow window, Grid root)
    {
        IntPtr popup = IntPtr.Zero, other = IntPtr.Zero;
        using var shellThread = new HookThread("Notification shell regression fixture");
        Check(shellThread.Start(() =>
        {
            other = CreateWindowEx(0x08000080, "STATIC", "Volume", 0x80000000, -32000, -32000, 400, 180, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            return popup = CreateWindowEx(0x08000080, "STATIC", "New notification", 0x80000000,
                -32000, -32000, 400, 180, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }, () => { DestroyWindow(popup); DestroyWindow(other); }),
            "独立 GUI 线程创建真实 Win32 通知弹窗窗口");
        // The test uses a real foreign GUI thread and real visibility messages. A fixture
        // matcher substitutes only the Windows shell identity, never the hide operation.
        using var guard = new ToastBannerGuard(handle => handle == popup);
        guard.SetEnabled(true);
        NativeMethods.ShowWindowAsync(other, 4);
        NativeMethods.ShowWindowAsync(popup, 4);
        System.Threading.Thread.Sleep(100); // UI is deliberately not pumping; shell and guard must work independently.
        // MainWindow(options) is the isolated test constructor; real services are disabled.
        // Refuse this reset for a production window. Only this fixture's in-memory inbox
        // and synthetic layers are reset, never Windows notification history or files.
        if (AgentField<bool>(window, "_runtimeEnabled"))
            throw new InvalidOperationException("Isolation required for synthetic notification regression.");
        PrivateCall(window, "ClearDesktopInbox");
        PrivateCall(window, "DismissActiveNotifications");
        PrivateCall(window, "SetState", IslandState.Bar, true);
        int removed = 0;
        SetAgentField(window, "_suppressSystemPopup", new Action<DesktopNotification>(_ => removed++));
        var message = new DesktopNotification("native-race", "test.native", "测试通知", "原系统横幅", "重叠回归", DateTimeOffset.Now, WindowsId: 51001);
        PrivateCall(window, "OnSystemToastsReceived", new[] { message }, false);
        PumpAnimations(25); root.UpdateLayout();
        Check(removed == 1 && AgentField<DesktopNotification?>(window, "_desktopCurrent")?.Key == message.Key,
            "真实岛提示路径已接受通知并调用原有删除接口");
        Check(!NativeMethods.IsWindowVisible(popup), "岛已显示通知时，原生弹窗不能同时保持可见");
        Check(NativeMethods.IsWindowVisible(other), "横幅接管不隐藏其他类型窗口");
        for (int i = 0; i < 10; i++)
        {
            long before = guard.HiddenCount;
            NativeMethods.ShowWindowAsync(popup, 4);
            var limit = System.Diagnostics.Stopwatch.StartNew();
            while (guard.HiddenCount == before && limit.ElapsedMilliseconds < 1000) System.Threading.Thread.Sleep(10);
            System.Threading.Thread.Sleep(20);
            Check(!NativeMethods.IsWindowVisible(popup), "重复展示同一系统横幅时仍被接管 " + i);
        }
        guard.SetEnabled(false);
        System.Threading.Thread.Sleep(75);
        Check(NativeMethods.IsWindowVisible(popup), "关闭通知接管后自动恢复被隐藏的系统弹窗窗口");
        Check(ToastBannerGuard.IsToastTitle("新通知") && ToastBannerGuard.IsToastTitle("New notification")
            && !ToastBannerGuard.IsToastTitle("通知中心") && !ToastBannerGuard.IsToastTitle("Volume"), "仅识别通知横幅标题，排除通知中心和音量窗口");
        Check(ToastBannerGuard.IsShellProcessPath(@"C:\Windows\SystemApps\Microsoft.Windows.ShellExperienceHost_cw5n1h2txyewy\ShellExperienceHost.exe", @"C:\Windows")
            && !ToastBannerGuard.IsShellProcessPath(@"C:\Users\test\ShellExperienceHost.exe", @"C:\Windows")
            && !ToastBannerGuard.IsShellProcessPath(@"C:\Windows\SystemApps-malicious\ShellExperienceHost.exe", @"C:\Windows"),
            "只信任 Windows 系统应用目录中的通知宿主，拒绝同名应用");
        using (var service = new SystemToastService(System.Windows.Threading.Dispatcher.CurrentDispatcher, guard))
        {
            service.SetBannerReplacementEnabled(true);
            Check(!service.BannerTakeoverActive, "没有通知读取权限时不隐藏系统横幅");
            typeof(SystemToastService).GetMethod("SetStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, new object[] { "fixture access granted", true });
            System.Threading.Thread.Sleep(75);
            Check(service.BannerTakeoverActive && !NativeMethods.IsWindowVisible(popup), "获得读取权限后接管已有系统横幅");
            var previous = AgentField<SystemToastService?>(window, "_systemToasts");
            SetAgentField(window, "_systemToasts", service);
            try
            {
                var options = AgentField<IslandOptions>(window, "_options");
                SettingsRegistry.Write(options, "NotificationReplaceSystemPopup", false);
                PrivateCall(window, "OnDesktopNotificationOptionsChanged");
                NativeMethods.ShowWindowAsync(popup, 4); System.Threading.Thread.Sleep(75);
                Check(!service.BannerTakeoverActive && NativeMethods.IsWindowVisible(popup), "真实设置变更关闭原生横幅接管");
                SettingsRegistry.Write(options, "NotificationReplaceSystemPopup", true);
                PrivateCall(window, "OnDesktopNotificationOptionsChanged"); System.Threading.Thread.Sleep(75);
                Check(service.BannerTakeoverActive && !NativeMethods.IsWindowVisible(popup), "真实设置变更恢复原生横幅接管");
                typeof(SystemToastService).GetMethod("SetStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, new object[] { "fixture access revoked", false });
                System.Threading.Thread.Sleep(75);
                Check(!service.BannerTakeoverActive, "撤销读取权限立即释放横幅接管");
                Check(NativeMethods.IsWindowVisible(popup), "撤销读取权限后自动恢复系统弹窗窗口");
                typeof(SystemToastService).GetMethod("SetStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, new object[] { "fixture access granted", true });
                System.Threading.Thread.Sleep(75);
            }
            finally { SetAgentField(window, "_systemToasts", previous); }
        }
        System.Threading.Thread.Sleep(75);
        Check(NativeMethods.IsWindowVisible(popup) && !guard.Running, "退出监听后释放事件钩子，恢复系统弹窗");
        PrivateCall(window, "ClearDesktopInbox");
    }
}
