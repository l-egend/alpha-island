using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;

internal static partial class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowExW")]
    private static extern IntPtr FindVolumeWindow(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void VolumeTestKey(byte key, byte scan, uint flags, UIntPtr extra);
    private static void PressVolumeTestKey(byte key)
    { VolumeTestKey(key, 0, 0, UIntPtr.Zero); VolumeTestKey(key, 0, 2, UIntPtr.Zero); }

    private static void VolumeFlyoutUiChecks(alpha_island.MainWindow window, Grid root)
    {
        IntPtr popup = IntPtr.Zero, other = IntPtr.Zero;
        using var shell = new HookThread("Volume flyout native regression fixture");
        Check(shell.Start(() =>
        {
            other = CreateWindowEx(0x08000080, "STATIC", "Quick Settings", 0x80000000,
                -32000, -32000, 260, 70, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            return popup = CreateWindowEx(0x08000080, "STATIC", "Volume feedback", 0x80000000,
                -32000, -32000, 260, 70, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }, () => { DestroyWindow(popup); DestroyWindow(other); }), "独立 GUI 线程创建原生音量与其他系统窗口");
        using var guard = new VolumeFlyoutGuard(handle => handle == popup);
        var options = AgentField<IslandOptions>(window, "_options");
        using var service = new VolumeService(window.Dispatcher);
        SetAgentField(window, "_volumeFlyoutGuard", guard);
        SetAgentField(window, "_volumeService", service);
        try
        {
            options.VolumeNotifyEnabled = true;
            SettingsRegistry.Write(options, "NotificationQuietMode", false);
            PrivateCall(window, "SyncVolumeFlyoutTakeover");
            Check(guard.Running, "音量提示开启时启用接管钩子");
            NativeMethods.ShowWindowAsync(popup, 4); NativeMethods.ShowWindowAsync(other, 4);
            Thread.Sleep(100);
            Check(NativeMethods.IsWindowVisible(popup), "没有音量反馈时保留共用宿主的亮度等提示");
            PrivateCall(window, "OnVolumeChanged", null, new VolumeInfo(24, false));
            Thread.Sleep(100); // WPF deliberately does not pump; the guard is independent.
            Check(!NativeMethods.IsWindowVisible(popup), "真实音量通知路径接管原生浮层，即使 WPF 没有抽消息");
            Check(AgentField<IslandLayerHost>(window, "_layers").IsActive("volume"), "系统浮层隐藏时岛的音量层已经显示");
            Check(NativeMethods.IsWindowVisible(other), "音量接管不隐藏快速设置或其他窗口");
            for (int i = 0; i < 6; i++)
            {
                if (i % 2 == 0) guard.TakeOverFeedback();
                NativeMethods.ShowWindowAsync(popup, 4);
                if (i % 2 != 0) guard.TakeOverFeedback();
                Thread.Sleep(80);
                Check(!NativeMethods.IsWindowVisible(popup), "按键连发及声音/窗口事件先后顺序回归 " + i);
            }
            Thread.Sleep(800);
            NativeMethods.ShowWindowAsync(popup, 4); Thread.Sleep(80);
            Check(NativeMethods.IsWindowVisible(popup), "反馈窗口到期后不持续隐藏同一宿主");
            guard.TakeOverFeedback(); Thread.Sleep(80);
            options.VolumeNotifyEnabled = false; PrivateCall(window, "SyncVolumeFlyoutTakeover"); Thread.Sleep(80);
            Check(!guard.Running && NativeMethods.IsWindowVisible(popup), "关闭现有音量提示开关立即恢复系统浮层");
            options.VolumeNotifyEnabled = true; PrivateCall(window, "SyncVolumeFlyoutTakeover");
            guard.TakeOverFeedback(); Thread.Sleep(80);
            SettingsRegistry.Write(options, "NotificationQuietMode", true);
            PrivateCall(window, "OnDesktopNotificationOptionsChanged"); Thread.Sleep(80);
            Check(!guard.Running && NativeMethods.IsWindowVisible(popup), "静默模式停止音量接管，系统仍可显示音量");
            SettingsRegistry.Write(options, "NotificationQuietMode", false);
            PrivateCall(window, "OnDesktopNotificationOptionsChanged");
            guard.TakeOverFeedback(); Thread.Sleep(80);
            PrivateCall(window, "ApplySessionNotification", true); Thread.Sleep(80);
            Check(!guard.Running && NativeMethods.IsWindowVisible(popup), "锁屏时释放系统音量浮层");
            PrivateCall(window, "ApplySessionNotification", false);
            Check(guard.Running, "解锁后恢复音量接管");
            guard.TakeOverFeedback(); Thread.Sleep(80);
            PrivateCall(window, "StopVolumeService"); Thread.Sleep(80);
            Check(!guard.Running && NativeMethods.IsWindowVisible(popup), "正常退出所用服务释放路径恢复系统浮层");
            guard.TakeOverFeedback(); NativeMethods.ShowWindowAsync(popup, 4); Thread.Sleep(80);
            Check(NativeMethods.IsWindowVisible(popup), "释放后迟到的音量事件不能继续隐藏窗口");
            Check(!VolumeFlyoutGuard.IsFlyoutHostClass("Shell_TrayWnd") && !VolumeFlyoutGuard.IsFlyoutHostClass("Windows.UI.Core.CoreWindow"),
                "原生匹配排除任务栏、快速设置和通知宿主");
            Check(VolumeFlyoutGuard.IsShellProcessPath(@"C:\Windows\explorer.exe", @"C:\Windows")
                && !VolumeFlyoutGuard.IsShellProcessPath(@"C:\Users\test\explorer.exe", @"C:\Windows")
                && !VolumeFlyoutGuard.IsShellProcessPath(@"C:\Windows\SystemApps-fake\ShellHost.exe", @"C:\Windows"),
                "拒绝系统目录以外的同名窗口进程");
        }
        finally
        {
            SetAgentField(window, "_volumeFlyoutGuard", null);
            SetAgentField(window, "_volumeService", null);
            guard.Dispose();
        }
    }

    private static IntPtr FindRealVolumeFlyout()
    {
        foreach (string cls in new[] { "XamlExplorerHostIslandWindow", "NativeHWNDHost" })
        {
            IntPtr handle = IntPtr.Zero;
            while ((handle = FindVolumeWindow(IntPtr.Zero, handle, cls, null)) != IntPtr.Zero)
                if (VolumeFlyoutGuard.IsShellFlyout(handle)) return handle;
        }
        return IntPtr.Zero;
    }

    private static void VolumeFlyoutLiveChecks(alpha_island.MainWindow window, Grid root)
    {
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Console);
        float original = device.AudioEndpointVolume.MasterVolumeLevelScalar;
        bool muted = device.AudioEndpointVolume.Mute;
        byte key = original < .96 ? (byte)0xAF : (byte)0xAE;
        using var service = new VolumeService(window.Dispatcher);
        using var guard = new VolumeFlyoutGuard();
        using var keys = new HotkeyService(window.Dispatcher);
        var options = AgentField<IslandOptions>(window, "_options");
        options.VolumeNotifyEnabled = true;
        SettingsRegistry.Write(options, "NotificationQuietMode", false);
        service.VolumeChanged += (_, info) => PrivateCall(window, "OnVolumeChanged", null, info);
        guard.FlyoutHidden += () => PrivateCall(window, "OnNativeVolumeFlyoutHidden");
        keys.KeyPressed += (_, e) => PrivateCall(window, "OnVolumeKeyPressed", null, e);
        SetAgentField(window, "_volumeService", service);
        SetAgentField(window, "_volumeFlyoutGuard", guard);
        try
        {
            service.Start(); keys.Start();
            for (int i = 0; i < 20 && !service.IsAvailable; i++) PumpAnimations(75);
            Check(service.IsAvailable, "本机真实音频端点就绪");
            PressVolumeTestKey(key); PumpAnimations(220);
            IntPtr native = FindRealVolumeFlyout();
            Check(native != IntPtr.Zero && NativeMethods.IsWindowVisible(native), "未接管对照组能显示截图中的 Windows 音量浮层");
            Console.WriteLine("Live volume OSD HWND: " + native.ToInt64().ToString("X"));
            PrivateCall(window, "SyncVolumeFlyoutTakeover");
            for (int i = 0; i < 4; i++)
            {
                PumpAnimations(800); // Each press starts a new feedback window.
                PressVolumeTestKey(key); PumpAnimations(180);
                Check(!NativeMethods.IsWindowVisible(native), "真实系统音量按键后原生浮层被隐藏 " + i);
                Check(AgentField<IslandLayerHost>(window, "_layers").IsActive("volume"), "真实 CoreAudio/按键路径仍显示岛的音量层 " + i);
            }
            Check(guard.HiddenCount > 0 && Math.Abs(device.AudioEndpointVolume.MasterVolumeLevelScalar - original) > .001,
                "接管实际发生且音量按键仍正常调节系统音量");
            options.VolumeNotifyEnabled = false;
            PrivateCall(window, "OnDesktopNotificationOptionsChanged");
            PressVolumeTestKey(key); PumpAnimations(180);
            Check(!guard.Running && NativeMethods.IsWindowVisible(native), "本机关闭音量提示后系统浮层恢复");
            options.VolumeNotifyEnabled = true;
            PrivateCall(window, "OnDesktopNotificationOptionsChanged");
            PressVolumeTestKey(key); PumpAnimations(180);
            Check(guard.Running && !NativeMethods.IsWindowVisible(native), "本机重新开启后接管继续生效");
            int cachedPercent = service.CurrentPercent;
            bool cachedMute = service.IsMuted;
            try
            {
                foreach (int percent in new[] { 0, 100 })
                {
                    // Change this isolated listener's cache, never the real device.
                    // No CoreAudio callback or native SHOW is generated for this key.
                    SetAgentField(service, "_lastPercent", percent);
                    SetAgentField(service, "_lastMuted", false);
                    SetAgentField(window, "_lastVolumeFeedbackAt", 0L);
                    PrivateCall(window, "DismissActiveNotifications");
                    PrivateCall(window, "SetState", IslandState.Bar, true);
                    PrivateCall(window, "OnVolumeKeyPressed", null,
                        new KeyboardHookEventArgs(System.Windows.Input.Key.VolumeUp, percent == 0 ? 0xAE : 0xAF,
                            System.Windows.Input.ModifierKeys.None, true));
                    PumpAnimations(180);
                    Check(AgentField<IslandLayerHost>(window, "_layers").IsActive("volume")
                        && window.VolumeText.Text == percent + "%",
                        "缓存音量 " + percent + "% 时按键无数值/窗口事件仍显示正确岛提示");
                }
            }
            finally
            {
                SetAgentField(service, "_lastPercent", cachedPercent);
                SetAgentField(service, "_lastMuted", cachedMute);
            }
            PrivateCall(window, "StopVolumeService");
            PressVolumeTestKey(key); PumpAnimations(180);
            Check(!guard.Running && NativeMethods.IsWindowVisible(native), "本机退出服务后再次按键恢复 Windows 音量浮层");
        }
        finally
        {
            SetAgentField(window, "_volumeFlyoutGuard", null);
            SetAgentField(window, "_volumeService", null);
            keys.Dispose(); guard.Dispose(); service.Dispose();
            device.AudioEndpointVolume.MasterVolumeLevelScalar = original;
            device.AudioEndpointVolume.Mute = muted;
        }
        Check(Math.Abs(device.AudioEndpointVolume.MasterVolumeLevelScalar - original) < .001 && device.AudioEndpointVolume.Mute == muted,
            "测试后恢复原始系统音量与静音状态");
    }
}