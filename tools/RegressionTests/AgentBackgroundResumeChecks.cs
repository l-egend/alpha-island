using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void AgentBackgroundResumeChecks(alpha_island.MainWindow window, Grid root)
    {
        using var destination = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Agent background resume fixture")
        { Width = 640, Height = 420, PositionX = -31000, PositionY = -31800,
            WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        var target = new AgentAppWindow(destination.Handle, "Codex", "Fixture", false);
        var fixture = new AgentWindowFixture { Windows = [target] };
        var oldNavigation = AgentField<AgentAppNavigationService>(window, "_agentNavigation");
        var options = AgentField<IslandOptions>(window, "_options");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        var registry = new ExternalAgentRegistry();
        const string session = "background-resume-fixture", owner = "external:codex/" + session;
        void Report(string state) => Task.Run(() =>
        {
            if (!registry.Update("codex", session, state, "Fixture", 300, DateTime.UtcNow, out var error, Environment.ProcessId))
                throw new InvalidOperationException(error);
        }).GetAwaiter().GetResult();
        bool Shown() => AgentField<IslandState>(window, "_state") == IslandState.Pill
            && window.AgentNotifyLayer.Visibility == Visibility.Visible;
        try
        {
            SettingsRegistry.Write(options, "AgentStatusEnabled", true);
            SettingsRegistry.Write(options, "MediaWindowHandoffEnabled", true);
            SetAgentField(window, "_agentNavigation", new AgentAppNavigationService(fixture));
            PrivateCall(window, "StartAgentStatus");
            Report("thinking"); PrivateCall(window, "UpdateAgentStatusUi");
            PumpAnimations(100); root.UpdateLayout();
            Check(Shown() && AgentStatus.Owner == owner, "真实外部状态链路展开后台 Codex 小岛");

            // The timed presentation has elapsed while ForceActive keeps a live task visible.
            // Deliver the worker's idle/resume around the hover tick, before queued UI refreshes run.
            layers.ShowUntil("agentstatus", DateTime.UtcNow.AddSeconds(-1));
            Report("idle");
            PrivateCall(window, "HoverTimer_Tick", null, EventArgs.Empty);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "短暂空闲由正式超时路径自动收起，而非用户手动撤下");
            Report("thinking");
            PumpAnimations(100); PrivateCall(window, "UpdateAgentStatusUi"); root.UpdateLayout();
            bool resumed = Shown();
            fixture.Foreground = destination.Handle; PrivateCall(window, "UpdateAgentStatusUi");
            fixture.Foreground = IntPtr.Zero; PrivateCall(window, "UpdateAgentStatusUi");
            PumpAnimations(100); root.UpdateLayout();
            Check(Shown(), "前台再退回后台能恢复同一 Codex 状态，确认消失问题的恢复特征");
            Check(resumed, "状态恢复后小岛自行显示，无须切到前台再最小化");
            for (int cycle = 0; cycle < 3; cycle++)
            {
                long previousActivity = AgentStatus.ActivityId;
                layers.ShowUntil("agentstatus", DateTime.UtcNow.AddSeconds(-1));
                if (cycle == 1)
                    Task.Run(() => registry.Sweep(DateTime.UtcNow.AddSeconds(301))).GetAwaiter().GetResult();
                else Report("idle");
                PrivateCall(window, "HoverTimer_Tick", null, EventArgs.Empty);
                Report("thinking");
                PumpAnimations(100); root.UpdateLayout();
                Check(Shown() && AgentStatus.ActivityId != previousActivity,
                    cycle == 1 ? "租约超时后收到新状态直接恢复小岛" : "连续后台结束与恢复不会遗留已经显示的旧标记");
                SetAgentField(window, "_hoverOutTicks", 1000);
                PrivateCall(window, "HoverTimer_Tick", null, EventArgs.Empty);
                PrivateCall(window, "HandleOutsideIslandClick");
                Check(Shown(), "恢复后的后台任务不因移开鼠标或操作其他软件再次掉下去");
                Check(!layers.AnyPinned, "后台恢复不改成强制置顶");
            }
            long activity = AgentStatus.ActivityId;
            PrivateCall(window, "CollapseToBar", true);
            foreach (string state in new[] { "thinking", "streaming", "thinking" })
            {
                Report(state); PumpAnimations(70); PrivateCall(window, "UpdateAgentStatusUi");
                Check(AgentStatus.ActivityId == activity && AgentField<IslandState>(window, "_state") == IslandState.Bar,
                    "同一轮任务的续期和输出切换保留用户手动收起");
            }
            Report("tool-wait"); PumpAnimations(70); root.UpdateLayout();
            Check(Shown() && AgentStatus.ActivityId == activity,
                "同一轮任务进入授权等待仍会重新提醒");
            Report("thinking"); PumpAnimations(70);
            fixture.Foreground = destination.Handle; PrivateCall(window, "UpdateAgentStatusUi");
            Report("idle"); Report("thinking"); PumpAnimations(100); root.UpdateLayout();
            Check(!layers.IsActive("agentstatus") && AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "新的运行身份遵守 Codex 在前台时隐藏的规则");
            fixture.Foreground = IntPtr.Zero; PrivateCall(window, "UpdateAgentStatusUi");
            PumpAnimations(100); root.UpdateLayout();
            Check(Shown(), "前台任务回到后台后正常显示");
            PrivateCall(window, "CollapseToBar", true);
            Report("idle"); Report("thinking"); PumpAnimations(100); root.UpdateLayout();
            Check(Shown(), "手动收起只影响当前轮，下一轮后台任务正常提醒");
            Report("idle"); PumpAnimations(100); PrivateCall(window, "HandleOutsideIslandClick");
            PumpAnimations(100); root.UpdateLayout();
            Check(!AgentStatus.IsBusy && !layers.IsActive("agentstatus")
                && AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "任务真正结束后正常收起且不复活旧提示");
        }
        finally
        {
            PrivateCall(window, "StopAgentStatus");
            AgentStatus.Reset(owner: owner);
            SetAgentField(window, "_agentNavigation", oldNavigation);
            layers.HideAll(); PrivateCall(window, "SetState", IslandState.Bar, true);
        }
    }
}