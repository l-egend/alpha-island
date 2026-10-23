using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void AgentCompletionVisualChecks(alpha_island.MainWindow window, Grid root, string testRoot)
    {
        var failures = new List<Exception>();
        void Case(string title, Action body)
        {
            try { body(); }
            catch (Exception error) { failures.Add(error); Console.WriteLine("FAIL CASE: " + title + " / " + error.Message); }
        }
        foreach (string source in new[] { "codex", "claude-code", "antigravity" })
            Case(source + " completion ordering", () =>
            {
                using var scope = new RegistryCleanup(); var registry = scope.Registry; DateTime now = DateTime.UtcNow;
                string owner = "external:" + source + "/completion-race";
                registry.Update(source, "completion-race", "thinking", "", 300, now, out _, beginsActivity: true);
                registry.Update(source, "completion-race", "working", "", 300, now.AddSeconds(1), out _);
                registry.Update(source, "completion-race", "idle", "", 5, now.AddSeconds(2), out _);
                registry.Update(source, "completion-race", "thinking", "", 300, now.AddSeconds(3), out _);
                Check(!AgentStatus.Snapshot.Any(entry => entry.Owner == owner), source + " 明确结束后迟到的工具状态不会弹出正在思考");
                registry.Update(source, "completion-race", "thinking", "", 300, now.AddSeconds(4), out _, beginsActivity: true);
                Check(AgentStatus.Snapshot.Any(entry => entry.Owner == owner), source + " 下一次明确提交仍能正常开始");
            });
        Case("late recovered prompt after stop", () =>
        {
            using var scope = new RegistryCleanup(); var registry = scope.Registry;
            var journal = new CodexStatusJournal("http://127.0.0.1:1/"); DateTime now = DateTime.UtcNow;
            void Report(string state, int second, bool begins = false)
            {
                using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new { source = "codex", session = "delayed-start", state,
                    reportedAtUnixMs = new DateTimeOffset(now.AddSeconds(second)).ToUnixTimeMilliseconds(), statusEventId = Guid.NewGuid().ToString("D"), beginsActivity = begins }));
                journal.Deliver(payload.RootElement, now.AddSeconds(10), () => registry.Update("codex", "delayed-start", state, "", 300, now.AddSeconds(10), out _, beginsActivity: begins));
            }
            Report("working", 1); Report("idle", 2); Report("thinking", 0, true);
            Check(!AgentStatus.Snapshot.Any(entry => entry.Owner == "external:codex/delayed-start"), "结束后恢复的旧提交不重新弹出正在思考");
        });
        Case("completed native task before first delivery", () =>
        {
            string? original = Environment.GetEnvironmentVariable("CODEX_HOME");
            EventHandler? observer = null;
            try
            {
                string home = Path.Combine(testRoot, "completion-before-delivery"); Environment.SetEnvironmentVariable("CODEX_HOME", home);
                using var fixture = new TurnHistoryFixture(home);
                long finished = DateTimeOffset.UtcNow.AddSeconds(-2).ToUnixTimeMilliseconds();
                fixture.Exec("INSERT INTO thread_turns VALUES('terminal-chat','terminal-turn','completed',NULL," + finished + ");");
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
                using var bridge = new AgentBridgeService(port, _ => false); bridge.Start();
                using var http = new HttpClient { BaseAddress = new Uri(bridge.Endpoint) };
                int flashed = 0;
                observer = (_, _) => { if (AgentStatus.Snapshot.Any(activity => activity.Owner == "external:codex/already-ended")) Interlocked.Exchange(ref flashed, 1); };
                AgentStatus.Changed += observer;
                async Task<bool> Report(string turn, long sent)
                {
                    using var response = await http.PostAsync("status", new StringContent(JsonSerializer.Serialize(new { source = "codex", session = "already-ended",
                        state = "thinking", beginsActivity = true, nativeThreadId = "terminal-chat", nativeTurnId = turn,
                        statusEventId = Guid.NewGuid().ToString("D"), reportedAtUnixMs = sent }), Encoding.UTF8, "application/json"));
                    response.EnsureSuccessStatusCode();
                    return true;
                }
                AwaitPlugin(Report("terminal-turn", finished - 1000));
                Check(Volatile.Read(ref flashed) == 0, "原生任务早已结束时，迟到的状态在发布到 UI 之前被拦截，不闪现思考");
                AwaitPlugin(Report("", finished - 1000));
                Check(Volatile.Read(ref flashed) == 0, "缺少任务编号的旧提交也按实际发送时间判断结束，不短暂复活");
                string runtime = Path.Combine(home, "outbox"), config = Path.Combine(home, "hooks.json");
                Directory.CreateDirectory(Path.Combine(runtime, "status-events"));
                File.WriteAllText(config, JsonSerializer.Serialize(new { endpoint = bridge.Endpoint, runtimeDir = runtime }));
                string id = Guid.NewGuid().ToString("D");
                File.WriteAllText(Path.Combine(runtime, "status-events", $"{finished - 1000:D13}-{id}.json"), JsonSerializer.Serialize(new { version = 1,
                    payload = new { source = "codex", session = "already-ended", state = "thinking", beginsActivity = true,
                        nativeThreadId = "terminal-chat", nativeTurnId = "terminal-turn", statusEventId = id, reportedAtUnixMs = finished - 1000 } }));
                using (var scope = new RegistryCleanup())
                    new CodexStatusJournal(bridge.Endpoint, config, _ => Environment.ProcessId).Drain(scope.Registry, DateTime.UtcNow);
                Check(Volatile.Read(ref flashed) == 0, "离线队列中的已结束任务同样在发布前被过滤，不闪出助手岛");
                AwaitPlugin(Report("", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                Check(Volatile.Read(ref flashed) == 1, "旧任务完成之后的新提交仍可正常显示，不能一概屏蔽同一对话");
            }
            finally { if (observer != null) AgentStatus.Changed -= observer; Environment.SetEnvironmentVariable("CODEX_HOME", original); }
        });
        Case("card satellite background", () =>
        {
            using var destination = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Card satellite material fixture")
            { Width = 640, Height = 420, PositionX = -31000, PositionY = -31000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
            var navigation = AgentField<AgentAppNavigationService>(window, "_agentNavigation");
            bool reduce = alpha_island.Animation.MotionPolicy.ReduceMotion, save = alpha_island.Animation.MotionPolicy.PowerSave;
            var options = AgentField<IslandOptions>(window, "_options");
            try
            {
                alpha_island.Animation.MotionPolicy.OverrideRenderTierForDebug(2); alpha_island.Animation.MotionPolicy.Apply(false, false);
                SettingsRegistry.Write(options, "AgentStatusEnabled", true);
                SetAgentField(window, "_agentNavigation", new AgentAppNavigationService(new AgentWindowFixture
                { Windows = [new(destination.Handle, "Codex", "Fixture", false)] }));
                AgentStatus.Set(AgentRunState.Thinking, "Fixture", "external:codex/card-a");
                AgentStatus.Set(AgentRunState.Thinking, "Fixture", "external:codex/card-b");
                PrivateCall(window, "UpdateAgentStatusUi"); PumpAnimations(600);
                var orbs = AgentField<Dictionary<string, Border>>(window, "_satelliteOrbViews");
                PrivateCall(window, "SetState", IslandState.Card, true); PumpAnimations(650); root.UpdateLayout();
                var interrupted = orbs.Values.First(orb => orb.IsVisible);
                var transform = (TransformGroup)interrupted.RenderTransform;
                double targetX = transform.Children.OfType<TranslateTransform>().Single().X;
                alpha_island.Animation.SatelliteOrbAnimator.SlideOut(interrupted, 0, targetX);
                root.UpdateLayout(); PrivateCall(window, "RenderLiquidBackdrop");
                Check(MaterialAlpha(interrupted) <= 5, "真实液态出场在主岛内部暂时裁去重复背景，保留图标");
                alpha_island.Animation.SatelliteOrbAnimator.CompleteMotion(interrupted); root.UpdateLayout();
                Check(MaterialAlpha(interrupted) > 5, "出场动画被中断并落到左侧时同步恢复小圆岛背景");
                var random = new Random(155);
                for (int repeat = 0; repeat < 4; repeat++)
                {
                    for (int step = 0; step < 5; step++)
                    {
                        PrivateCall(window, "SetState", step % 2 == 0 ? IslandState.Card : IslandState.Pill, false);
                        if (step == 3) AgentStatus.Reset(owner: "external:codex/card-b");
                        if (step == 4) AgentStatus.Set(AgentRunState.Thinking, "Fixture", "external:codex/card-b");
                        PrivateCall(window, "UpdateAgentStatusUi"); PumpAnimations(random.Next(3, 28));
                    }
                    PrivateCall(window, "SetState", IslandState.Card, false); PumpAnimations(650); root.UpdateLayout();
                    Check(orbs.Values.Count(orb => orb.IsVisible && orb.Opacity > .95) == 2, "完整卡片旁的两个助手缩小岛均可见");
                    foreach (var orb in orbs.Values.Where(orb => orb.IsVisible && orb.Opacity > .95))
                        Check(MaterialAlpha(orb) > 5, "多助手展开卡片后每个缩小岛保留背景（切换 " + repeat + "）");
                    PrivateCall(window, "SetState", IslandState.Pill, false); PumpAnimations(420);
                }
                PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.System); PumpAnimations(650); root.UpdateLayout();
                Directory.CreateDirectory("bin/AgentCompletion155/previews");
                SaveThemePreview(root, "bin/AgentCompletion155/previews/agents-beside-card.png");
            }
            finally
            {
                AgentStatus.Reset(owner: "external:codex/card-a"); AgentStatus.Reset(owner: "external:codex/card-b");
                PrivateCall(window, "UpdateAgentStatusUi"); alpha_island.Animation.MotionPolicy.Apply(reduce, save);
                SetAgentField(window, "_agentNavigation", navigation);
            }
        });
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private static byte MaterialAlpha(Border satellite)
    {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(satellite.Background, null, new Rect(0, 0, 52, 44));
            var image = new RenderTargetBitmap(52, 44, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
            byte[] pixels = new byte[52 * 44 * 4]; image.CopyPixels(pixels, 52 * 4, 0);
            return pixels[(22 * 52 + 26) * 4 + 3];
    }
}
