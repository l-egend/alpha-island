using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void AgentStartupLatencyChecks(alpha_island.MainWindow window, Grid root)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        var saved = new Dictionary<string, System.Text.Json.JsonElement>(options.Extras);
        var navigation = AgentField<AgentAppNavigationService>(window, "_agentNavigation");
        const string owner = "external:codex/startup-latency";
        bool reduced = MotionPolicy.ReduceMotion, power = MotionPolicy.PowerSave; int tier = MotionPolicy.RenderTier;
        double Present(AgentRunState state)
        {
            var watch = Stopwatch.StartNew(); double painted = -1;
            EventHandler frame = (_, _) =>
            {
                if (painted < 0 && AgentStatus.Owner == owner && window.AgentNotifyLayer.IsVisible
                    && window.AgentNotifyLayer.Opacity > .05 && window.AgentNotifyTitle.Text.Length > 0)
                    painted = watch.Elapsed.TotalMilliseconds;
            };
            CompositionTarget.Rendering += frame;
            try
            {
                var worker = new Thread(() => { watch.Restart(); AgentStatus.Set(state, "Codex latency fixture", owner); }) { IsBackground = true };
                worker.Start(); worker.Join();
                while (painted < 0 && watch.ElapsedMilliseconds < 4000) PumpAnimations(10);
                root.UpdateLayout(); return painted;
            }
            finally { CompositionTarget.Rendering -= frame; }
        }
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            SettingsRegistry.Write(options, "AgentStatusEnabled", true);
            SetAgentField(window, "_agentNavigation", new AgentAppNavigationService(new AgentWindowFixture()));
            PrivateCall(window, "StartAgentStatus");
            double cold = Present(AgentRunState.Thinking);
            Console.WriteLine($"TIMING: codex-first-thinking-paint={cold:F1}ms");
            Check(cold >= 0 && cold < 250, "冷启动新任务的思考提示在 250 ms 内开始绘制");
            AgentStatus.Reset(owner: owner); PumpAnimations(40);
            double warm = Present(AgentRunState.Thinking);
            Console.WriteLine($"TIMING: codex-next-thinking-paint={warm:F1}ms");
            Check(warm >= 0 && warm < 150, "后续新任务立即恢复思考提示，不等待秒级轮询");
            AgentStatus.Reset(owner: owner); PumpAnimations(30);
            var registry = new ExternalAgentRegistry();
            try
            {
                void BeginTask() => registry.Update("codex", "startup-latency", "thinking", "Codex latency fixture", 300,
                    DateTime.UtcNow, out _, beginsActivity: true);
                BeginTask(); PumpAnimations(80);
                long previous = AgentStatus.ActivityId;
                PrivateCall(window, "CollapseToBar", true);
                Check(AgentField<IslandState>(window, "_state") == IslandState.Bar, "上一轮仍有租约时可正常收起旧提示");
                var watch = Stopwatch.StartNew(); double firstPaint = -1;
                EventHandler rendered = (_, _) =>
                {
                    if (firstPaint < 0 && AgentStatus.ActivityId != previous && window.AgentNotifyLayer.IsVisible && window.AgentNotifyLayer.Opacity > .05)
                        firstPaint = watch.Elapsed.TotalMilliseconds;
                };
                CompositionTarget.Rendering += rendered;
                try
                {
                    var reporter = new Thread(BeginTask); reporter.Start(); reporter.Join();
                    while (firstPaint < 0 && watch.ElapsedMilliseconds < 1000) PumpAnimations(10);
                }
                finally { CompositionTarget.Rendering -= rendered; }
                Console.WriteLine($"TIMING: codex-new-round-first-paint={firstPaint:F1}ms");
                Check(AgentStatus.ActivityId != previous && firstPaint >= 0 && firstPaint < 150,
                    "上一轮租约未清理时，新任务也在 150 ms 内绘制提示");
                PumpAnimations(450);
                long current = AgentStatus.ActivityId;
                PrivateCall(window, "CollapseToBar", true);
                registry.Update("codex", "startup-latency", "thinking", "Codex latency fixture", 300, DateTime.UtcNow, out _);
                PumpAnimations(80);
                Check(AgentStatus.ActivityId == current && AgentField<IslandState>(window, "_state") == IslandState.Bar,
                    "普通心跳保留任务身份，不把用户收起的同一轮提示重复弹出");
            }
            finally { registry.Clear(); }
        }
        finally
        {
            AgentStatus.Reset(owner: owner); PrivateCall(window, "StopAgentStatus");
            SetAgentField(window, "_agentNavigation", navigation); options.Extras = saved;
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduced, power);
        }
    }
}
