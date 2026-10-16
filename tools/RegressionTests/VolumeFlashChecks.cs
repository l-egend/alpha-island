using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;

internal static partial class Program
{
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int FlashCloak(IntPtr window, int attribute, out int value, int length);
    private static void VolumeFlashLiveChecks(alpha_island.MainWindow window, Grid root, bool intercept = true, bool gameQuiet = false)
    {
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Console);
        float original = device.AudioEndpointVolume.MasterVolumeLevelScalar; bool muted = device.AudioEndpointVolume.Mute;
        using var service = new VolumeService(window.Dispatcher);
        using var keys = new HotkeyService(window.Dispatcher);
        using var guard = new VolumeFlyoutGuard();
        var options = AgentField<IslandOptions>(window, "_options");
        options.VolumeNotifyEnabled = true;
        SettingsRegistry.Write(options, "NotificationQuietMode", false);
        SetAgentField(window, "_volumeService", service); SetAgentField(window, "_volumeFlyoutGuard", guard);
        if (intercept) keys.PreviewKeyPressed += (_, e) => PrivateCall(window, "OnVolumeKeyPreview", null, e);
        keys.KeyPressed += (_, e) => PrivateCall(window, "OnVolumeKeyPressed", null, e);
        try
        {
            service.Start(); keys.Start();
            for (int i = 0; i < 30 && !service.IsAvailable; i++) PumpAnimations(30);
            Check(service.IsAvailable, "闪现回归使用真实音频端点和正式键盘钩子");
            PrivateCall(window, "SyncVolumeFlyoutTakeover");
            if (gameQuiet)
            {
                SettingsRegistry.Write(options, "NotificationGameQuietEnabled", true);
                PrivateCall(window, "ApplyGameActivity", new GameActivityState(1, true, "音量回归游戏", "用户指定", 1));
                PrivateCall(window, "OnGameQuietNoticeExpired", null, EventArgs.Empty);
                PrivateCall(window, "OnVolumeChanged", null, new VolumeInfo(service.CurrentPercent, service.IsMuted));
                Check(AgentField<bool>(window, "_gameQuietActive") && !AgentField<IslandLayerHost>(window, "_layers").IsActive("volume"),
                    "游戏免打扰仍屏蔽非用户操作产生的音量通知");
            }
            for (int i = 0; i < 8; i++)
            {
                byte key = (i % 2 == 0) == (original < .96) ? (byte)0xAF : (byte)0xAE;
                int expected = Math.Clamp(service.CurrentPercent + (key == 0xAF ? 2 : -2), 0, 100);
                PressVolumeTestKey(key);
                int visible = 0;
                var clock = Stopwatch.StartNew();
                // Deliberately block the UI. Native prevention and audio control must not depend on it.
                while (clock.ElapsedMilliseconds < 260)
                {
                    IntPtr native = FindRealVolumeFlyout(); int cloak;
                    if (native != IntPtr.Zero && NativeMethods.IsWindowVisible(native)
                        && FlashCloak(native, 14, out cloak, 4) == 0 && cloak == 0) visible++;
                    Thread.Sleep(1);
                }
                Console.WriteLine($"key={i + 1}, uncloaked visible samples={visible}, expected={expected}, actual={device.AudioEndpointVolume.MasterVolumeLevelScalar * 100:0.00}, cache={service.CurrentPercent}");
                Check(visible == 0, "音量键全程不显示系统浮层，即使界面线程忙碌 " + i);
                Check(Math.Abs(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100 - expected) < .6,
                    "音量仍按 2% 步长在后台调节 " + i);
                PumpAnimations(40);
                Check(AgentField<IslandLayerHost>(window, "_layers").IsActive("volume")
                    && window.VolumeText.Text == expected + "%", "界面恢复后岛显示当前真实音量 " + i);
            }
            if (gameQuiet)
            {
                Check(AgentField<bool>(window, "_gameQuietActive"), "调节音量不会取消游戏免打扰");
                var layers = AgentField<IslandLayerHost>(window, "_layers");
                Check(PrivateCall(window, "ShowPillNotification", IslandNotification.Seconds("system", 3)) is false && !layers.IsActive("system"),
                    "音量反馈期间系统告警仍不弹出");
                var approval = new ApprovalRequest("volume-game-approval", "read", "{}", "隔离请求", 60, DateTime.UtcNow);
                SetAgentField(window, "_approvalCurrent", approval);
                PrivateCall(window, "PresentCurrentApproval");
                PressVolumeTestKey(0xAF); Thread.Sleep(180); PumpAnimations(60);
                Check(layers.IsActive("volume") && !layers.IsActive("approval") && AgentField<string>(window, "_activeMainIslandKey") == "volume",
                    "后台审批等待期间仍显示主动音量反馈，审批继续保持免打扰");
                SetAgentField(window, "_approvalCurrent", null); layers.Hide("approval"); AgentStatus.Reset(owner: "approval");
                PumpAnimations((int)(Math.Max(1, options.VolumeNotifySeconds) * 1000) + 100);
                PrivateCall(window, "HoverTimer_Tick", null, EventArgs.Empty);
                PrivateCall(window, "OnVolumeChanged", null, new VolumeInfo(service.CurrentPercent, service.IsMuted));
                Check(!layers.IsActive("volume"), "主动反馈到期收回，后来非用户音量通知继续静默");
                PrivateCall(window, "ApplyGameActivity", GameActivityState.Inactive);
            }
            int beforeBurst = service.CurrentPercent;
            for (int i = 0; i < 12; i++) { PressVolumeTestKey(i % 2 == 0 ? (byte)0xAF : (byte)0xAE); Thread.Sleep(20); }
            Thread.Sleep(140);
            Check(service.CurrentPercent == beforeBurst && Math.Abs(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100 - beforeBurst) < .6,
                "快速连续音量键不丢步，后台旧写入不覆盖新目标");            service.ToggleMute(); service.ToggleMute(); Thread.Sleep(180);
            Check(device.AudioEndpointVolume.Mute == service.IsMuted, "快速两次静音切换应用最终目标，不能被合并成一次反转");
            options.VolumeNotifyEnabled = false; PrivateCall(window, "OnDesktopNotificationOptionsChanged");
            var passThrough = new KeyboardHookEventArgs(System.Windows.Input.Key.VolumeUp, 0xAF, System.Windows.Input.ModifierKeys.None, true);
            PrivateCall(window, "OnVolumeKeyPreview", null, passThrough);
            Check(!passThrough.Handled, "关闭音量接管后原按键交回系统");
            options.VolumeNotifyEnabled = true; SettingsRegistry.Write(options, "NotificationQuietMode", true);
            PrivateCall(window, "OnDesktopNotificationOptionsChanged"); PrivateCall(window, "OnVolumeKeyPreview", null, passThrough);
            Check(!passThrough.Handled, "静默模式不拦截音量按键");
            SettingsRegistry.Write(options, "NotificationQuietMode", false); PrivateCall(window, "OnDesktopNotificationOptionsChanged");
            PrivateCall(window, "ApplySessionNotification", true); PrivateCall(window, "OnVolumeKeyPreview", null, passThrough);
            Check(!passThrough.Handled, "锁屏不拦截音量按键");
        }
        finally
        {
            keys.Stop(); PrivateCall(window, "StopVolumeFlyoutTakeover");
            service.Dispose(); SetAgentField(window, "_volumeService", null);
            device.AudioEndpointVolume.MasterVolumeLevelScalar = original; device.AudioEndpointVolume.Mute = muted;
            SetAgentField(window, "_sessionLocked", false);
        }
    }
}