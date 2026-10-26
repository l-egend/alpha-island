using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;

internal static partial class Program
{
    private static void AgentWindowResponsivenessChecks(alpha_island.MainWindow window, Grid root)
    {
        HwndSource MakeWindow(string name, int x) => new(new HwndSourceParameters(name)
        { Width = 640, Height = 420, PositionX = x, PositionY = -31000,
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        using var codex = MakeWindow("Agent latency Codex", -31000);
        using var ag = MakeWindow("Agent latency Antigravity", -30000);
        var fixture = new AgentWindowFixture { Activating = target => NativeMethods.ShowWindowAsync(target.Handle, NativeMethods.SW_RESTORE), Windows = [new(codex.Handle, "Codex", "Fixture", false), new(ag.Handle, "Antigravity", "Fixture", false)] };
        var navigation = AgentField<AgentAppNavigationService>(window, "_agentNavigation");
        var options = AgentField<IslandOptions>(window, "_options");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        const string codexOwner = "external:codex/ui-latency", agOwner = "external:antigravity/ui-latency";
        var context = SynchronizationContext.Current;
        bool reduce = alpha_island.Animation.MotionPolicy.ReduceMotion, save = alpha_island.Animation.MotionPolicy.PowerSave;
        alpha_island.Animation.MotionPolicy.Apply(false, false);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        void Until(Func<bool> done, int milliseconds = 900)
        { var timeout = Stopwatch.StartNew(); while (!done() && timeout.ElapsedMilliseconds < milliseconds) PumpAnimations(10); }
        try
        {
            SettingsRegistry.Write(options, "AgentStatusEnabled", true); SettingsRegistry.Write(options, "MediaWindowHandoffEnabled", true);
            SetAgentField(window, "_agentNavigation", new AgentAppNavigationService(fixture));
            PrivateCall(window, "StartAgentStatus");
            AgentStatus.Set(AgentRunState.Thinking, "Fixture", codexOwner); PrivateCall(window, "UpdateAgentStatusUi");
            AgentStatus.Set(AgentRunState.Thinking, "Fixture", agOwner); PrivateCall(window, "UpdateAgentStatusUi");
            string agKey = (string)PrivateCall(window, "AgentSessionKey", agOwner)!;
            AgentStatus.Reset(owner: agOwner); PrivateCall(window, "UpdateAgentStatusUi");
            for (int index = 0; index < 40; index++) window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Thread.Sleep(4)));
            var watch = Stopwatch.StartNew();
            double latency = -1;
            DispatcherHookEventHandler refreshed = (_, _) =>
            {
                if (latency < 0 && layers.ActiveKeys.Any(key => (string?)PrivateCall(window, "AgentSessionKey", agOwner) == key))
                    latency = watch.Elapsed.TotalMilliseconds;
            };
            window.Dispatcher.Hooks.OperationCompleted += refreshed;
            try
            {
                // Isolate dispatcher latency from unrelated ThreadPool startup/capture work.
                var reporter = new Thread(() => { watch.Restart(); AgentStatus.Set(AgentRunState.Thinking, "Fixture", agOwner); }) { IsBackground = true };
                reporter.Start(); reporter.Join();
                Until(() => latency >= 0);
            }
            finally { window.Dispatcher.Hooks.OperationCompleted -= refreshed; }
            Console.WriteLine($"TIMING: agent-state-dispatch={latency:F1}ms");
            Check(latency >= 0 && latency < 110, "后台工作队列繁忙时，新助手状态优先刷新而不排在所有后台任务之后");
            PumpAnimations(220);
            agKey = (string)PrivateCall(window, "AgentSessionKey", agOwner)!;
            var tracker = AgentField<MediaWindowService>(window, "_agentWindows");
            fixture.Foreground = ag.Handle;
            typeof(MediaWindowService).GetMethod("OnWinEvent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(tracker, [IntPtr.Zero, (uint)3, ag.Handle, 0, 0, (uint)0, (uint)0]);
            PumpAnimations(70);
            Check(AgentField<string>(window, "_agentWindowOwner") == agOwner && !layers.IsActive(agKey),
                "非当前跟踪的反重力进入前台时，窗口事件立即切换对应跟踪并隐藏它的岛");
            tracker = AgentField<MediaWindowService>(window, "_agentWindows");
            typeof(MediaWindowService).GetProperty("Snapshot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(tracker, AgentFixtureWindowSnapshot());
            WatcherCall(tracker, "SuppressTransitions");
            fixture.Foreground = IntPtr.Zero; NativeMethods.ShowWindowAsync(ag.Handle, 6);
            Until(() => AgentField<alpha_island.Animation.MediaHandoffOverlay?>(window, "_agentHandoff") != null, 180);
            Check(AgentField<alpha_island.Animation.MediaHandoffOverlay?>(window, "_agentHandoff") != null,
                "多个助手并行时，反重力真实最小化事件仍立即触发吸入动画");
            Until(() => AgentField<alpha_island.Animation.MediaHandoffOverlay?>(window, "_agentHandoff") == null);
            Check(layers.IsActive(agKey) && layers.IsActive((string)PrivateCall(window, "AgentSessionKey", codexOwner)!),
                "反重力吸入结束后保留双方工作岛，不丢失缩小对话");
            typeof(MediaWindowService).GetProperty("Snapshot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(tracker, null);
            fixture.Activated = null;
            var open = (Task)PrivateCall(window, "OpenAgentWindowAsync", fixture.Windows[1])!;
            Check(!open.IsCompleted && (AgentField<bool>(window, "_agentHandoffPreparing") || AgentField<alpha_island.Animation.MediaHandoffOverlay?>(window, "_agentHandoff") != null),
                "窗口截图尚未缓存时，点击助手立即异步准备真实画面且不阻塞界面");
            Until(()=>open.IsCompleted,1500);
            Check(open.IsCompletedSuccessfully && fixture.Activated?.Handle==ag.Handle,
                "未缓存窗口的放大动画完成后正常打开助手，并回收动画代理");
            NativeMethods.ShowWindowAsync(ag.Handle,6);Until(()=>NativeMethods.IsIconic(ag.Handle),400);
            PumpAnimations(40);PrivateCall(window,"CancelAgentHandoff");fixture.Foreground=ag.Handle;
            var minimizedOpen=(Task)PrivateCall(window,"OpenAgentWindowAsync",fixture.Windows[1])!;
            Check(!minimizedOpen.IsCompleted && (AgentField<bool>(window,"_agentHandoffPreparing") || AgentField<alpha_island.Animation.MediaHandoffOverlay?>(window,"_agentHandoff")!=null),
                "真实最小化后即使旧前台句柄未更新，点击仍进入放大而非被当作前台忽略");
            Until(()=>minimizedOpen.IsCompleted,1500);
            Check(minimizedOpen.IsCompletedSuccessfully && !NativeMethods.IsIconic(ag.Handle),"快速点击最小化助手最终恢复真实窗口并完成交接");
        }
        finally
        {
            AgentStatus.Reset(owner: codexOwner); AgentStatus.Reset(owner: agOwner);
            PrivateCall(window, "StopAgentStatus"); SetAgentField(window, "_agentNavigation", navigation);
            SynchronizationContext.SetSynchronizationContext(context);
            alpha_island.Animation.MotionPolicy.Apply(reduce, save);
            NativeMethods.ShowWindowAsync(ag.Handle, NativeMethods.SW_RESTORE);
        }
    }
}
