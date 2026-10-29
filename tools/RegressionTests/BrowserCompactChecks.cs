using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;
using CheckBox = System.Windows.Controls.CheckBox;

internal static partial class Program
{
    private static void BrowserCompactChecks(alpha_island.MainWindow window)
    {
        var full = new BrowserWindowSnapshot(new IntPtr(42), "chrome", true, false, false, true,
            new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1080));
        foreach (string browser in new[] { "chrome", "MSEDGE.exe", "firefox", "brave", "opera", "vivaldi", "Arc", "zen", "360chrome", "QQBrowser" })
            Check(BrowserWindowPolicy.ShouldCompact(full with { ProcessName = browser }), browser + "全屏时收起");
        foreach (var sample in new[] { full with { Window = IntPtr.Zero }, full with { Visible = false },
            full with { Minimized = true }, full with { OnPrimaryMonitor = false },
            full with { ProcessName = "msedgewebview2" }, full with { ProcessName = "Code" },
            full with { ProcessName = "Codex" }, full with { ProcessName = "explorer" },
            full with { Bounds = new Rect(100, 100, 900, 700) } })
            Check(!BrowserWindowPolicy.ShouldCompact(sample), "普通窗口、副屏、最小化及非浏览器均不触发：" + sample.ProcessName + "/" + sample.Bounds);
        Check(BrowserWindowPolicy.ShouldCompact(full with { Maximized = true, Bounds = new Rect(-8, -8, 1936, 1048) }),
            "最大化采用窗口状态，不要求矩形等于完整屏幕");
        Check(BrowserWindowPolicy.ShouldCompact(full with { Bounds = new Rect(-2560, -200, 2560, 1440), MonitorBounds = new Rect(-2560, -200, 2560, 1440) }),
            "负坐标主屏的全屏判定使用物理像素");
        Check(!BrowserWindowPolicy.ShouldCompact(full with { Bounds = new Rect(0, 0, 1920, 1040) }),
            "手动铺满工作区但未最大化的普通窗口不误判为全屏");

        var options = AgentField<IslandOptions>(window, "_options");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        void State(IslandState state) => PrivateCall(window, "SetState", state, true, IslandPageIds.System);
        void Browser(bool active) => PrivateCall(window, "ApplyBrowserCompact", active);
        IslandState Current() => AgentField<IslandState>(window, "_state");
        try
        {
            SettingsRegistry.Write(options, "BrowserAutoCompactEnabled", true);
            layers.HideAll(); State(IslandState.Card);
            SetAgentField(window, "_pinnedCardPage", IslandPageIds.System);
            SetAgentField(window, "_isPinned", true);
            layers.SetPinned("status", true);
            Browser(true);
            Check(Current() == IslandState.Bar && AgentField<bool>(window, "_isPinned")
                && layers.IsPinned("status") && AgentField<string>(window, "_pinnedCardPage") == IslandPageIds.System,
                "进入浏览器后收起卡片，同时保留音乐、图层及卡片的置顶偏好");
            PrivateCall(window, "IslandBody_MouseEnter", window.IslandBody, new System.Windows.Input.MouseEventArgs(Mouse.PrimaryDevice, 0));
            State(IslandState.Pill); State(IslandState.Card);
            Check(Current() == IslandState.Bar, "经过标签栏及后台请求都不能重新撑开主岛");
            Check((bool)PrivateCall(window, "ShowPillNotification", IslandNotification.Seconds("battery", 20))!
                && Current() == IslandState.Bar && layers.IsActive("battery"), "通知正常登记但保持收起");
            Check(window.MusicOrbBubble.Visibility != Visibility.Visible
                && AgentField<Dictionary<string, Border>>(window, "_satelliteOrbViews").Values.All(view => view.Visibility != Visibility.Visible),
                "浏览器自动收起时侧岛也隐藏");
            PrivateCall(window, "IslandBody_MouseLeftButtonDown", window.IslandBody,
                new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            Check(Current() == IslandState.Card && AgentField<bool>(window, "_browserManualReveal"), "点击细条可临时打开完整卡片");
            for (int i = 0; i < 10; i++) PrivateCall(window, "HandleBrowserCompactHover", true);
            Check(Current() == IslandState.Card, "在卡片中操作时保持展开");
            for (int i = 0; i < 6; i++) PrivateCall(window, "HandleBrowserCompactHover", false);
            Check(Current() == IslandState.Bar && !AgentField<bool>(window, "_browserManualReveal"), "鼠标移开后再次收起，置顶不会挡住标签栏");
            PrivateCall(window, "RevealBrowserCompactIsland"); PrivateCall(window, "HandleOutsideIslandClick");
            Check(Current() == IslandState.Bar && layers.IsPinned("status"), "点击浏览器可立即收起，保留已有置顶");
            Browser(false);
            Check(Current() == IslandState.Card && AgentField<string>(window, "_pinnedCardPage") == IslandPageIds.System,
                "切换其他应用或还原浏览器时恢复原来的置顶卡片");
            Browser(true);
            SettingsRegistry.Write(options, "BrowserAutoCompactEnabled", false);
            PrivateCall(window, "StopBrowserWindowMonitor");
            Check(!AgentField<bool>(window, "_browserCompactActive") && Current() == IslandState.Card,
                "关闭设置立即解除自动收起，无需重启");
            SettingsRegistry.Write(options, "BrowserAutoCompactEnabled", true);
            Browser(true);
            SetAgentField(window, "_approvalCurrent", new ApprovalRequest("browser-approval", "read", "{}", "浏览器测试", 60, DateTime.UtcNow));
            PrivateCall(window, "PresentCurrentApproval");
            Check(Current() == IslandState.Pill && layers.IsActive("approval"), "工具审批仍可显示允许和拒绝按钮");
            SetAgentField(window, "_approvalCurrent", null); layers.Hide("approval");
            PrivateCall(window, "HandleBrowserCompactHover", false);
            Check(Current() == IslandState.Bar, "审批结束后重新收起");
            var previousVoice = AgentField<VoiceAssistantSession?>(window, "_voiceAssistant");
            using (var voice = new VoiceAssistantSession(window.Dispatcher, () => new FakeVoiceCapture(),
                (_, _) => throw new Exception("本检查不发送 API 请求"), _ => Task.CompletedTask, () => true, () => Task.CompletedTask))
            {
                try
                {
                    SetAgentField(window, "_voiceAssistant", voice);
                    var wake = voice.WakeAsync(); AwaitPlugin(wake.ContinueWith(_ => true)); wake.GetAwaiter().GetResult();
                    State(IslandState.Pill); PrivateCall(window, "HandleBrowserCompactHover", false);
                    Check(voice.Visible && Current() == IslandState.Pill, "主动语音唤醒仍可显示录音界面，不被浏览器规则压回");
                    var cancel = voice.CancelAsync(); AwaitPlugin(cancel.ContinueWith(_ => true)); cancel.GetAwaiter().GetResult();
                }
                finally { SetAgentField(window, "_voiceAssistant", previousVoice); }
            }
            PrivateCall(window, "HandleBrowserCompactHover", false);
            Check(Current() == IslandState.Bar, "取消语音后回到浏览器细条");
            SetAgentField(window, "_fileDragActive", true); State(IslandState.Tray);
            Check(Current() == IslandState.Tray, "主动拖入文件仍可展开中转站");
            SetAgentField(window, "_fileDragActive", false); PrivateCall(window, "HandleBrowserCompactHover", false);
            Check(Current() == IslandState.Bar, "文件拖入交互结束后再次收起");
            PrivateCall(window, "ApplyGameActivity", new GameActivityState(1, true, "浏览器兼容测试", "测试", 1));
            Check(Current() == IslandState.Pill, "浏览器规则不截断游戏模式的进入提示");
            PrivateCall(window, "FinishGameQuietIntro"); PrivateCall(window, "RevealBrowserCompactIsland");
            Check(Current() == IslandState.Bar && !AgentField<bool>(window, "_browserManualReveal"), "游戏模式仍优先锁定为细条");
            PrivateCall(window, "ApplyGameActivity", GameActivityState.Inactive);
            Check(Current() == IslandState.Bar, "游戏结束但浏览器仍最大化时继续收起");
        }
        finally
        {
            SetAgentField(window, "_approvalCurrent", null); AgentStatus.Reset(owner: "approval");
            SetAgentField(window, "_isPinned", false); SetAgentField(window, "_pinnedCardPage", null);
            layers.SetPinned("status", false); layers.HideAll(); Browser(false); State(IslandState.Bar);
        }

        int reads = 0, changes = 0; var snapshot = full;
        using (var monitor = new BrowserWindowMonitor(window.Dispatcher, () => { reads++; return snapshot; }))
        {
            monitor.Changed += _ => changes++;
            for (int i = 0; i < 100; i++) monitor.RequestRefresh();
            PumpAnimations(220);
            Check(monitor.Current && changes == 1 && reads <= 2, "窗口事件合并，最大化过渡只发布一次状态");
            snapshot = full with { Maximized = false, Bounds = new Rect(30, 30, 800, 600) };
            monitor.Refresh(); monitor.Refresh();
            Check(!monitor.Current && changes == 2, "还原窗口发布一次恢复状态");
            monitor.RequestRefresh(); monitor.Dispose(); PumpAnimations(160);
            Check(changes == 2, "停用后不执行排队的旧回调");
        }
        BrowserCompactNativeChecks(window);

        var settings = new alpha_island.Settings.SettingsWindow(false);
        try
        {
            settings.LoadFrom(new IslandOptions());
            var toggle = (CheckBox)settings.FindName("BrowserAutoCompactToggle");
            Check(toggle?.IsChecked == true && SettingsRegistry.Find("BrowserAutoCompactEnabled")?.PageId == SettingsPageIds.Behavior,
                "交互与工具中的开关默认开启并使用统一设置绑定");
        }
        finally { settings.ForceClose = true; settings.Close(); }
    }

    private static void BrowserCompactNativeChecks(alpha_island.MainWindow owner)
    {
        var fixture = new Window { Width = 640, Height = 360, ShowInTaskbar = false, ShowActivated = false,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Title = "Alpha browser state fixture" };
        try
        {
            fixture.Show(); WindowWorkArea.PlacePrimaryTop(fixture, 640); PumpAnimations(100);
            var probe = new BrowserWindowProbe(); var handle = new WindowInteropHelper(fixture).Handle;
            BrowserWindowSnapshot Read() => probe.ReadWindow(handle, _ => "chrome");
            Check(!BrowserWindowPolicy.ShouldCompact(Read()), "真实 HWND 普通窗口不收起");
            fixture.WindowState = WindowState.Maximized; PumpAnimations(150);
            Check(Read().Maximized && BrowserWindowPolicy.ShouldCompact(Read()), "真实 Win32 最大化状态可以识别");
            fixture.WindowState = WindowState.Normal;
            var info = new NativeMethods.MONITORINFO { Size = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(handle, 2), ref info);
            NativeMethods.SetWindowPos(handle, IntPtr.Zero, info.Monitor.Left, info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top, 0x0014);
            PumpAnimations(100);
            Check(!Read().Maximized && BrowserWindowPolicy.ShouldCompact(Read()), "真实无边框全屏可按物理屏幕识别");
            fixture.WindowState = WindowState.Minimized; PumpAnimations(80);
            Check(!BrowserWindowPolicy.ShouldCompact(Read()), "真实 HWND 最小化立即退出收起规则");
            using var monitor = new BrowserWindowMonitor(owner.Dispatcher);
            monitor.Start(); Check(monitor.EventsRunning, "真实 Windows 窗口事件监听已安装");
            monitor.Dispose(); Check(!monitor.EventsRunning, "停用时释放原生事件监听线程");
        }
        finally { fixture.Close(); }
    }
}
