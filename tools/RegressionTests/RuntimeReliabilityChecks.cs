using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;

internal static partial class Program
{
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int ReliabilityCloak(IntPtr handle, int attribute, out int value, int length);
    private static async Task RuntimeReliabilityChecks(string root)
    {
        var failures = new List<Exception>();
        async Task Case(string title, Func<Task> body)
        {
            try { await body(); }
            catch (Exception error) { failures.Add(error); Console.WriteLine("FAIL CASE: " + title + " / " + error.Message); }
        }
        await Case("native flyout re-show", () =>
        {
            IntPtr popup = IntPtr.Zero;
            using var thread = new HookThread("Flyout re-show regression");
            Check(thread.Start(() => popup = CreateWindowEx(0x08000080, "STATIC", "Volume fixture", 0x80000000,
                -32000, -32000, 260, 70, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero), () => DestroyWindow(popup)), "建立独立原生音量浮层复现窗口");
            using var guard = new VolumeFlyoutGuard(handle => handle == popup);
            guard.SetEnabled(true); guard.TakeOverFeedback(); NativeMethods.ShowWindowAsync(popup, 4);
            Thread.Sleep(90);
            int exposed = 0;
            for (int repeat = 0; repeat < 80; repeat++)
            {
                NativeMethods.ShowWindowAsync(popup, 4);
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 3)
                    if (NativeMethods.IsWindowVisible(popup) && ReliabilityCloak(popup, 14, out int cloak, 4) == 0 && cloak == 0) exposed++;
            }
            Console.WriteLine("Native flyout uncloaked visible samples=" + exposed);
            Check(exposed == 0, "接管后的系统浮层反复 ShowWindow 时没有短暂可见帧");
            guard.SetEnabled(false); Thread.Sleep(70);
            Check(NativeMethods.IsWindowVisible(popup) && ReliabilityCloak(popup, 14, out int restored, 4) == 0 && (restored & 1) == 0,
                "停用接管释放自己设置的合成器遮蔽并恢复原生浮层");
            return Task.CompletedTask;
        });
        await Case("one native turn multiple reporters", () =>
        {
            using var scope = new RegistryCleanup(); var registry = scope.Registry; DateTime now = DateTime.UtcNow;
            registry.Update("codex", "transport-a", "thinking", "fixture", 300, now, out _, beginsActivity: true, nativeThreadId: "chat-a", nativeTurnId: "turn-a");
            registry.Update("codex", "transport-b", "working", "fixture", 300, now, out _, nativeThreadId: "chat-a", nativeTurnId: "turn-a");
            Check(AgentStatus.Snapshot.Count(a => a.Owner.StartsWith("external:codex/")) == 1, "同一原生对话同一轮的不同上报标识只生成一个 Agent");
            registry.Update("codex", "transport-a", "idle", "", 5, now, out _, nativeThreadId: "chat-a", nativeTurnId: "turn-a");
            Check(!AgentStatus.Snapshot.Any(a => a.Owner.StartsWith("external:codex/")), "一轮明确结束会清除同轮重复上报，不残留正在思考");
            return Task.CompletedTask;
        });
        await Case("completed turn without turn id", () =>
        {
            string home = Path.Combine(root, "reliability-history"); string? old = Environment.GetEnvironmentVariable("CODEX_HOME");
            try
            {
                Environment.SetEnvironmentVariable("CODEX_HOME", home);
                using var fixture = new TurnHistoryFixture(home);
                fixture.Exec("INSERT INTO thread_turns VALUES('completed-chat','completed-turn','completed',NULL," + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ");");
                using var scope = new RegistryCleanup(); var registry = scope.Registry;
                registry.Update("codex", "missing-turn", "thinking", "fixture", 300, DateTime.UtcNow.AddSeconds(-5), out _, beginsActivity: true, nativeThreadId: "completed-chat");
                new CodexTurnHistory().Reconcile(registry, DateTime.UtcNow);
                Check(!AgentStatus.Snapshot.Any(a => a.Owner == "external:codex/missing-turn"), "缺少 turn_id 的上报在原生任务明确完成后停止思考");
                registry.Update("codex", "missing-turn", "working", "late", 300, DateTime.UtcNow, out _, nativeThreadId: "completed-chat");
                Check(!AgentStatus.Snapshot.Any(a => a.Owner == "external:codex/missing-turn"), "已完成的无编号任务不会被迟到心跳复活");
                registry.Update("codex", "missing-turn", "thinking", "new", 300, DateTime.UtcNow.AddSeconds(1), out _, beginsActivity: true, nativeThreadId: "completed-chat");
                new CodexTurnHistory().Reconcile(registry, DateTime.UtcNow.AddSeconds(1));
                Check(AgentStatus.Snapshot.Any(a => a.Owner == "external:codex/missing-turn"), "新任务的开始时间晚于旧完成记录时继续工作，不误判结束");
                fixture.Exec("INSERT INTO thread_turns VALUES('completed-chat','live-turn','inProgress',NULL,NULL);");
                new CodexTurnHistory().Reconcile(registry, DateTime.UtcNow.AddSeconds(1));
                Check(AgentStatus.Snapshot.Any(a => a.Owner == "external:codex/missing-turn"), "同一原生线程还有进行中的任务时不根据历史记录收回");
            }
            finally { Environment.SetEnvironmentVariable("CODEX_HOME", old); }
            return Task.CompletedTask;
        });
        await Case("startup compact readiness", async () =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            using var bridge = new AgentBridgeService(port, _ => false); bridge.Start();
            string configuration = Path.Combine(root, "reliability-hook.json");
            await File.WriteAllTextAsync(configuration, JsonSerializer.Serialize(new { endpoint = bridge.Endpoint, runtimeDir = Path.Combine(root, "hook-runtime"), allProjects = true, approvalMode = "native" }));
            var start = new ProcessStartInfo(Path.Combine(Environment.CurrentDirectory, "bin/InstallerTools/node-v24.21.0/node.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(Environment.CurrentDirectory, "examples/CodexHooks/hook.mjs")); start.ArgumentList.Add("--config"); start.ArgumentList.Add(configuration);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { session_id = "initial-compact", cwd = root, hook_event_name = "SessionStart", source = "compact" })); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(process.ExitCode == 0 && (await stdout).Trim() == "{}" && string.IsNullOrWhiteSpace(await stderr), "实际 Node 初始化事件正常完成");
            Check(!AgentStatus.Snapshot.Any(a => a.Owner.StartsWith("external:codex/")), "应用初始化或恢复上下文事件本身不创建思考中的任务");
        });
        if (failures.Count != 0) throw new AggregateException(failures);
    }
}
