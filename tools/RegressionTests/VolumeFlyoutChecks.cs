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
    {
        var inputs = new[] {
            new NativeMethods.INPUT { type = NativeMethods.INPUT_KEYBOARD, u = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = key, dwFlags = 1 } } },
            new NativeMethods.INPUT { type = NativeMethods.INPUT_KEYBOARD, u = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = key, dwFlags = 1 | NativeMethods.KEYEVENTF_KEYUP } } }
        };
        uint accepted = NativeMethods.SendInput(2, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (accepted != 2) throw new InvalidOperationException("Volume test input was not accepted: " + accepted + "; Win32=" + Marshal.GetLastWin32Error());
    }

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
            var layers = AgentField<IslandLayerHost>(window, "_layers");
            layers.Hide("volume");
            PrivateCall(window, "ApplyBrowserCompact", true);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar, "浏览器最大化或全屏时先正常收起");
            NativeMethods.ShowWindowAsync(popup, 4);
            SetAgentField(service, "_lastPercent", 42);
            PrivateCall(window, "OnVolumeChanged", null, new VolumeInfo(42, false)); Thread.Sleep(80);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill && layers.IsActive("volume")
                && AgentField<string>(window, "_activeMainIslandKey") == "volume" && window.VolumeText.Text == "42%",
                "浏览器收起时音量变化仍展开可见的音量提示");
            Check(!NativeMethods.IsWindowVisible(popup) && guard.Running, "岛内音量提示可见时继续正确接管系统浮层");
            for (int i = 0; i < 8; i++) PrivateCall(window, "HandleBrowserCompactHover", false);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill, "鼠标移开不提前打断短暂音量提示");
            PrivateCall(window, "ShowPillNotification", IslandNotification.Seconds("battery", 10));
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill
                && AgentField<string>(window, "_activeMainIslandKey") == "volume", "普通通知不会抢走正在显示的音量提示");
            layers.Hide("volume"); PrivateCall(window, "HandleBrowserCompactHover", false);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar, "音量提示结束后回到浏览器细条");
            PrivateCall(window, "ShowPillNotification", IslandNotification.Seconds("battery", 10));
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar, "浏览器收起仍屏蔽其他通知的自动展开");
            PrivateCall(window, "IslandBody_PreviewMouseWheel", window.IslandBody,
                new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, 120)
                { RoutedEvent = System.Windows.UIElement.PreviewMouseWheelEvent });
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill && layers.IsActive("volume"),
                "岛上滚轮调节音量也能显示短暂反馈");
            PrivateCall(window, "IslandBody_MouseLeftButtonDown", window.IslandBody,
                new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                { RoutedEvent = System.Windows.UIElement.MouseLeftButtonDownEvent });
            Check(AgentField<IslandState>(window, "_state") == IslandState.Card
                && AgentField<bool>(window, "_browserManualReveal"), "点击音量提示可临时打开音量卡片");
            PrivateCall(window, "HandleOutsideIslandClick");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar, "操作音量卡片后点击岛外继续收起");
            PrivateCall(window, "ShowPillNotification", IslandNotification.Seconds("battery", 10));
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "关闭音量卡片后其他通知不能借用剩余音量提示时间重新展开");
            layers.SetPinned("volume", true);
            SetAgentField(window, "_lastVolumeFeedbackAt", Environment.TickCount64 - (long)(options.VolumeNotifySeconds * 1000) - 1000);
            PrivateCall(window, "SetState", IslandState.Pill, true);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar && layers.IsPinned("volume"),
                "音量置顶不延长临时提示，也不丢失置顶偏好");
            layers.SetPinned("volume", false);
            layers.Hide("battery"); layers.Hide("volume"); PrivateCall(window, "ApplyBrowserCompact", false);
            PrivateCall(window, "OnVolumeChanged", null, new VolumeInfo(44, false)); Thread.Sleep(80);
            Check(!NativeMethods.IsWindowVisible(popup) && layers.IsActive("volume"), "退出浏览器收起状态后原音量提示仍正常");
            PrivateCall(window, "ApplyBrowserCompact", true);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill && layers.IsActive("volume"),
                "提示期间最大化浏览器不会突然掐掉音量反馈");
            PrivateCall(window, "ApplyBrowserCompact", false);
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
            PrivateCall(window, "ApplyBrowserCompact", false);
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
