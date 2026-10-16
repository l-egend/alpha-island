using System.Windows.Controls;
using System.Windows.Input;
using alpha_island.Core;
using alpha_island.Services;
using Image = System.Windows.Controls.Image;

internal static partial class Program
{
    private sealed class AgentWindowFixture : IAgentWindowAccess
    {
        public IReadOnlyList<AgentAppWindow> Windows { get; init; } = [];
        public IntPtr Foreground { get; set; }
        public AgentAppWindow? Activated;
        public Action<AgentAppWindow>? Activating;
        public IReadOnlyList<AgentAppWindow> ReadWindows() => Windows;
        public bool Activate(AgentAppWindow window) { Activating?.Invoke(window); Activated = window; Foreground = window.Handle; return true; }
    }
    private static void AgentHandoffUiChecks(alpha_island.MainWindow window, Grid root)
    {
        using var destination = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Agent handoff destination")
        { Width = 640, Height = 420, PositionX = -31000, PositionY = -31800,
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        var target = new AgentAppWindow(destination.Handle, "Codex", "当前测试对话", false);
        var fixture = new AgentWindowFixture { Windows = [target] };
        var oldNavigation = AgentField<AgentAppNavigationService>(window, "_agentNavigation");
        bool reduced = alpha_island.Animation.MotionPolicy.ReduceMotion;
        bool powerSave = alpha_island.Animation.MotionPolicy.PowerSave;
        var oldContext = SynchronizationContext.Current;
        const string owner = "external:codex/handoff-fixture";
        try
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
            alpha_island.Animation.MotionPolicy.Apply(false, false);
            SetAgentField(window, "_agentNavigation", new AgentAppNavigationService(fixture));
            AgentStatus.Set(AgentRunState.Thinking, "Fixture task", owner);
            PrivateCall(window, "UpdateAgentStatusUi");
            var image = AgentFixtureWindowSnapshot();
            var tracker = AgentField<MediaWindowService>(window, "_agentWindows");
            typeof(MediaWindowService).GetProperty("Snapshot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(tracker, image);
            WatcherCall(tracker, "SuppressTransitions");
            PrivateCall(window, "SetState", IslandState.Pill, true); PumpAnimations(350); root.UpdateLayout();
            var task = (Task)PrivateCall(window, "OpenAgentWindowAsync", target)!;
            var overlay = AgentField<alpha_island.Animation.MediaHandoffOverlay?>(window, "_agentHandoff");
            Check(overlay != null && !task.IsCompleted, "助手跳转复用媒体播放器出岛动画，而非立即切换窗口");
            var proxy = (Image)typeof(alpha_island.Animation.MediaHandoffOverlay).GetField("_windowImage",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(overlay)!;
            Check(ReferenceEquals(proxy.Source, image), "助手出岛代理使用窗口截图，不再生成中性预览卡片");
            bool middleFrame = false, activatedEarly = false;
            EventHandler sample = (_, _) =>
            {
                if (overlay!.Progress > 0 && overlay.Progress < 1)
                { middleFrame = true; activatedEarly |= fixture.Activated != null; }
            };
            System.Windows.Media.CompositionTarget.Rendering += sample;
            try { PumpAnimations(120); }
            finally { System.Windows.Media.CompositionTarget.Rendering -= sample; }
            Check(middleFrame && !activatedEarly,
                "助手窗口在出岛动画中途保持等待，曲线和形变已推进");
            var completion = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && completion.ElapsedMilliseconds < 1500) PumpAnimations(10);
            Check(task.IsCompletedSuccessfully && fixture.Activated?.Handle == destination.Handle,
                "出岛结束后激活对应助手的已有当前对话");
            Check(AgentField<alpha_island.Animation.MediaHandoffOverlay?>(window, "_agentHandoff") == null
                && alpha_island.Interop.NativeMethods.GetProp(destination.Handle, alpha_island.Interop.DwmTransitionLease.OriginalProperty) != IntPtr.Zero,
                "助手跳转完成后销毁动画窗口，持续抑制目标原生过渡以准备下一次最小化");
            fixture.Foreground = IntPtr.Zero; fixture.Activated = null;
            PrivateCall(window, "UpdateAgentStatusUi");
            task = (Task)PrivateCall(window, "OpenAgentWindowAsync", target)!;
            PrivateCall(window, "CancelAgentHandoff"); PumpAnimations(100);
            Check(task.IsCompletedSuccessfully && fixture.Activated == null && AgentField<alpha_island.Animation.MediaHandoffOverlay?>(window, "_agentHandoff") == null,
                "退出或取消动画时释放资源，不再抢回助手窗口焦点");
        }
        finally
        {
            AgentStatus.Reset(owner: owner);
            PrivateCall(window, "StopAgentWindowTracking");
            SynchronizationContext.SetSynchronizationContext(oldContext);
            alpha_island.Animation.MotionPolicy.Apply(reduced, powerSave);
            SetAgentField(window, "_agentNavigation", oldNavigation);
            PrivateCall(window, "SetState", IslandState.Bar, true);
        }
        Check(alpha_island.Interop.NativeMethods.GetProp(destination.Handle, alpha_island.Interop.DwmTransitionLease.OriginalProperty) == IntPtr.Zero,
            "助手任务结束释放窗口追踪并恢复原生动画");
    }

    private static void AgentAppNavigationUiChecks(alpha_island.MainWindow window, Grid root)
    {
        var fixture = new AgentWindowFixture { Windows = [new(new IntPtr(1), "cloudmusic", "音乐", false),
            new(new IntPtr(2), "Codex", "当前对话", true), new(new IntPtr(3), "Antigravity", "当前工作区", false)] };
        var navigation = new AgentAppNavigationService(fixture);
        var oldNavigation = AgentField<AgentAppNavigationService>(window, "_agentNavigation");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        string owner = "external:codex/navigation-fixture";
        try
        {
            SetAgentField(window, "_agentNavigation", navigation);
            AgentStatus.Set(AgentRunState.Thinking, "Codex · 思考中", owner);
            PrivateCall(window, "UpdateAgentStatusUi"); layers.ShowSeconds("agentstatus", 20);
            PrivateCall(window, "SetState", IslandState.Pill, true);
            SetAgentField(window, "_activeMainIslandKey", "agentstatus");
            var args = new MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = System.Windows.UIElement.MouseLeftButtonDownEvent };
            PrivateCall(window, "IslandBody_MouseLeftButtonDown", window.IslandBody, args);
            root.UpdateLayout();
            Check(fixture.Activated?.Handle == new IntPtr(2) && AgentField<IslandState>(window, "_state") == IslandState.Pill && args.Handled,
                "点击助手主岛直接恢复对应 Codex 对话窗口，不打开音乐或其他卡片");
            fixture.Activated = null;
            Check((bool)PrivateCall(window, "TryOpenAgentIsland", "agentstatus")! && fixture.Activated == null,
                "旁侧与卫星入口识别已前台的助手，不重复切换或播放动画");
            Check(navigation.TryOpen("external:antigravity/fixture", out _) && fixture.Activated?.Handle == new IntPtr(3),
                "Antigravity 状态切换到其自己的已打开窗口");
            Check(!navigation.TryOpen("external:unknown/fixture", out _) && !navigation.TryOpen("plugin:ai.api-chat", out _),
                "未知助手与内部插件不会误打开其他外部窗口");
            var dsh = new AgentAppWindow(new IntPtr(4), "WindowsTerminal", "DSH · 当前会话", false);
            Check(AgentAppNavigationService.Matches("deepseek-harness", dsh)
                && !AgentAppNavigationService.Matches("deepseek-harness", dsh with { Title = "PowerShell" }),
                "DSH 终端按自身会话标题匹配，不切换到普通终端");
            Check(AgentAppNavigationService.Matches("claude-code", dsh with { Title = "Claude Code · project" })
                && !AgentAppNavigationService.Matches("claude-code", dsh with { Title = "PowerShell" }),
                "Claude Code 只切换到可识别的 Claude 终端，不误打开普通终端");
            var host = new AgentWindowFixture { Windows = [new(new IntPtr(5), "ChatGPT", "当前工作对话", false)] };
            Check(new AgentAppNavigationService(host).TryOpen("external:codex/desktop-host", out _) && host.Activated?.Handle == new IntPtr(5),
                "Codex 集成在 ChatGPT 工作界面时也能打开已有对话窗口");
        }
        finally
        {
            AgentStatus.Reset(owner: owner); layers.HideAll();
            SetAgentField(window, "_agentNavigation", oldNavigation);
            PrivateCall(window, "SetState", IslandState.Bar, true);
        }
    }
}
