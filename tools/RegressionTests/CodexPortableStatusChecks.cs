using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static async Task CodexPortableStatusChecks(string root)
    {
        string nativeHome = Path.Combine(root, "native-home"), executable = Path.Combine(root, "Codex.exe");
        File.Copy(Environment.GetEnvironmentVariable("ComSpec")!, executable);
        using var host = Process.Start(new ProcessStartInfo(executable, "/d /q /k")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        string? previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            CodexNativeRegistryChecks(host.Id);
            Environment.SetEnvironmentVariable("CODEX_HOME", nativeHome);
            using var fixture = new TurnHistoryFixture(nativeHome);
            fixture.Exec("ALTER TABLE thread_turns ADD COLUMN started_at INTEGER; ALTER TABLE thread_turns ADD COLUMN root_turn_id TEXT;");
            fixture.Exec("INSERT INTO thread_turns(thread_id,turn_id,status,started_at) VALUES('stale-thread','stale-turn','inProgress',1);");
            using var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
            int port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
            string endpoint = $"http://127.0.0.1:{port}/", configuration = Path.Combine(root, "codex-hooks.json");
            File.WriteAllText(configuration, JsonSerializer.Serialize(new { endpoint, allProjects = true, runtimeDir = Path.Combine(root, "runtime") }));
            using var bridge = new AgentBridgeService(port, _ => false, false, new CodexStatusJournal(endpoint, configuration, _ => host.Id));
            using var http = new HttpClient { BaseAddress = new Uri(endpoint) };
            bridge.Start();
            await Task.Delay(1100);
            Check(!AgentStatus.Snapshot.Any(item => item.Owner.StartsWith("external:codex/", StringComparison.Ordinal)),
                "没有 Hooks 时不把历史遗留的未结束记录误判为思考中");
            long started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            fixture.Exec($"INSERT INTO thread_turns(thread_id,turn_id,status,started_at,root_turn_id) VALUES('portable-thread','portable-turn','inProgress',{started},'portable-turn'),('child-thread','child-turn','inProgress',{started},'portable-turn');");
            async Task<bool> Active()
            {
                using var response = JsonDocument.Parse(await http.GetStringAsync("status"));
                return response.RootElement.GetProperty("connections").EnumerateArray().Any(connection =>
                    connection.GetProperty("source").GetString() == "codex" && connection.GetProperty("state").GetString() != "idle");
            }
            var watch = Stopwatch.StartNew();
            while (!await Active() && watch.ElapsedMilliseconds < 2400) await Task.Delay(40);
            Check(await Active(), "新电脑没有 Hooks 回调时，真实原生任务开始仍传到岛的状态通道");
            using (var status = JsonDocument.Parse(await http.GetStringAsync("status")))
                Check(status.RootElement.GetProperty("connections").EnumerateArray().Count(c => c.GetProperty("state").GetString() != "idle") == 1,
                    "原生子任务不被误识别为独立助手");
            Check((await AgentIntegrationChannelCheck.CheckAsync(port)).Contains("本地任务状态可用"), "设置状态检查读取实际桥接健康信息");
            bridge.NativeStatusAllowed = () => false;
            watch.Restart(); while (await Active() && watch.ElapsedMilliseconds < 2400) await Task.Delay(40);
            Check(!await Active(), "游戏模式或状态开关关闭时暂停原生补充读取并撤下其状态");
            bridge.NativeStatusAllowed = () => true;
            watch.Restart(); while (!await Active() && watch.ElapsedMilliseconds < 2400) await Task.Delay(40);
            Check(await Active(), "恢复状态显示后重新核对本轮任务");
            fixture.Exec($"UPDATE thread_turns SET status='completed',completed_at={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} WHERE turn_id='portable-turn';");
            watch.Restart(); while (await Active() && watch.ElapsedMilliseconds < 2400) await Task.Delay(40);
            Check(!await Active(), "原生任务完成后及时撤下思考状态");
            File.WriteAllText(configuration, JsonSerializer.Serialize(new { endpoint, allProjects = false, runtimeDir = Path.Combine(root, "runtime") }));
            fixture.Exec($"INSERT INTO thread_turns(thread_id,turn_id,status,started_at,root_turn_id) VALUES('restricted-thread','restricted-turn','inProgress',{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},'restricted-turn');");
            await Task.Delay(1200);
            Check(!await Active(), "仅接入指定项目时不擅自读取其他项目的活动状态");
            File.WriteAllText(configuration, JsonSerializer.Serialize(new { endpoint, allProjects = true, runtimeDir = Path.Combine(root, "runtime") }));
            File.WriteAllText(Path.Combine(root, "integrations.json"), "{\"providers\":{\"codex\":{\"enabled\":false}}}");
            await Task.Delay(1200);
            Check(!await Active(), "用户停用 Codex 接入后不以原生补充读取重新启用");
            using var health = JsonDocument.Parse(await http.GetStringAsync("health"));
            Check(health.RootElement.GetProperty("codexNativeStatus").GetProperty("mode").GetString() == "disabled", "诊断能明确区分接入停用与没有活动任务");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
            try { await host.StandardInput.WriteLineAsync("exit"); await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { if (!host.HasExited) host.Kill(true); }
        }
    }

    private static void CodexNativeRegistryChecks(int host)
    {
        using var lifetime = new RegistryCleanup(); var registry = lifetime.Registry;
        var now = DateTime.UtcNow;
        Check(registry.ObserveNativeCodex("native.test", "execution-thread", "same-turn", host, now, now, "Codex"), "登记隔离的原生任务");
        Check(registry.Update("codex", "hook-session", "tool-wait", "", 300, now, out _, host, "codex",
            beginsActivity:true, nativeThreadId:"rollout-thread", nativeTurnId:"same-turn"), "正常 Hooks 接管同一轮原生任务");
        Check(registry.Snapshot.Count(c => c.State != "idle") == 1 && registry.Snapshot.Single().State == "tool-wait"
            && registry.Snapshot.Single().ObservationSource == "event", "桌面线程与回调线程不同也不会拆成两个助手");
        registry.ObserveNativeCodex("native.test", "execution-thread", "same-turn", host, now, now, "Codex");
        Check(registry.Snapshot.Single().State == "tool-wait", "原生补充读取不覆盖等待授权的精确回调状态");
        registry.Update("codex", "hook-session", "idle", "", 5, now, out _, host, nativeThreadId:"rollout-thread", nativeTurnId:"same-turn");
        registry.Sweep(now.AddSeconds(6));
        registry.ObserveNativeCodex("native.test", "execution-thread", "same-turn", host, now, now.AddSeconds(6), "Codex");
        Check(!registry.Snapshot.Any(c => c.State != "idle"), "结束回调先于数据库更新时也不复活旧轮任务");
        registry.ObserveNativeCodex("native.lease", "lease-thread", "lease-turn", host, now, now, "Codex");
        registry.Sweep(now.AddSeconds(11));
        Check(!registry.Snapshot.Any(c => c.State != "idle"), "任务表持续不可读时原生状态有期限，不永久驻留");
        registry.Close();
        Check(!registry.ObserveNativeCodex("native.closed", "closed-thread", "closed-turn", host, now, now, "Codex"), "桥接关闭后的在途读取不能重新发布状态");
    }
}
