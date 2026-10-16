using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static async Task ExternalAgentLifetimeChecks()
    {
        var tree = new Dictionary<int, AgentHostProcessMonitor.Entry> {
            [90] = new(90, 80, "node.exe"), [80] = new(80, 70, "powershell.exe"),
            [70] = new(70, 60, "codex.exe"), [60] = new(60, 1, "Codex.exe"),
            [50] = new(50, 40, "node.exe"), [40] = new(40, 1, "Antigravity.exe") };
        Check(AgentHostProcessMonitor.Resolve("codex", 90, tree) == 60, "短命 Node 钩子沿祖先关联到稳定 Codex 应用，而非临时脚本或子进程");
        Check(AgentHostProcessMonitor.Resolve("antigravity", 50, tree) == 40, "Antigravity 钩子关联到自己的应用进程");
        tree[130] = new(130, 120, "node.exe"); tree[120] = new(120, 115, "powershell.exe");
        tree[115] = new(115, 110, "claude.exe");
        Check(AgentHostProcessMonitor.Resolve("claude-code", 130, tree) == 115,
            "Claude 钩子关联到原生会话进程，不使用短命脚本作为存活依据");
        tree[100] = new(100, 110, "node.exe"); tree[110] = new(110, 1, "ChatGPT.exe");
        Check(AgentHostProcessMonitor.Resolve("codex", 100, tree) == 110,
            "Codex 在 ChatGPT 桌面宿主运行时也关联到稳定应用，而非一次性钩子");
        Check(AgentHostProcessMonitor.Resolve("unknown", 90, tree) == null, "未知来源不会错误关联到其他助手宿主");
        tree[10] = new(10, 20, "node.exe"); tree[20] = new(20, 10, "cmd.exe");
        Check(AgentHostProcessMonitor.Resolve("codex", 10, tree) == null, "循环或缺失祖先安全退回有期限状态，不挂起扫描");
        int ownPid = Environment.ProcessId;
        Check(AgentHostProcessMonitor.IsAlive(ownPid, AgentHostProcessMonitor.StartStamp(ownPid))
            && !AgentHostProcessMonitor.IsAlive(ownPid, 1), "进程创建时间防止 PID 重用保留过期思考状态");
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using var bridge = new AgentBridgeService(port, _ => false);
        using var child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "/d", "/c", "ping -n 30 127.0.0.1 >nul" } })!;
        bridge.Start();
        using var http = new HttpClient { BaseAddress = new Uri(bridge.Endpoint) };
        string owner = "external:lifetime-fixture/closed-host";
        try
        {
            var payload = new { source = "lifetime-fixture", session = "closed-host", state = "thinking",
                detail = "生命周期测试", ttlSec = 300, processId = child.Id };
            using var posted = await http.PostAsync("status", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
            Check(posted.IsSuccessStatusCode && AgentStatus.Owner == owner && AgentStatus.IsBusy, "真实桥接接收带宿主进程的思考状态");
            child.Kill(entireProcessTree: true); await child.WaitForExitAsync();
            var timeout = Stopwatch.StartNew();
            while (AgentStatus.IsBusy && timeout.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(80);
            Check(!AgentStatus.IsBusy, "思考时关闭宿主进程，岛在三秒内取消思考，无须重启或等待五分钟租约");
            using var status = JsonDocument.Parse(await http.GetStringAsync("status"));
            Check(status.RootElement.GetProperty("connections").GetArrayLength() == 0, "已关闭宿主的过期连接同步移除");
            AgentStatus.Set(AgentRunState.Streaming, "内部任务", "lifetime-internal");
            using var repeated = await http.PostAsync("status", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
            using var swept = await http.GetAsync("status");
            Check(AgentStatus.Owner == "lifetime-internal", "清理已关闭外部助手保留其他内部或外部任务");
            AgentStatus.Reset(owner: "lifetime-internal");
            Check(!AgentStatus.IsBusy, "宿主关闭清理的是对应外部 claim，未留下隐藏思考任务");
            using var invalid = await http.PostAsync("status", new StringContent("{\"source\":\"codex\",\"session\":\"invalid\",\"state\":\"thinking\",\"reporterPid\":-1}"));
            Check(invalid.StatusCode == HttpStatusCode.BadRequest, "桥接拒绝无效进程元数据");
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            AgentStatus.Reset(owner: owner);
            AgentStatus.Reset(owner: "lifetime-internal");
        }
        // A private copy of the signed Windows console executable supplies a native session name
        // without starting a model or affecting any installed Claude session.
        string fixtureDirectory = Path.Combine(Path.GetTempPath(), "AlphaIsland.ClaudeLifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureDirectory);
        string fixtureExecutable = Path.Combine(fixtureDirectory, "claude.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), fixtureExecutable);
        using var session = Process.Start(new ProcessStartInfo(fixtureExecutable)
        { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "/d", "/c", "ping -n 30 127.0.0.1 >nul" } })!;
        var registry = new ExternalAgentRegistry(); const string nativeOwner = "external:claude-code/native-lifetime";
        var now = DateTime.UtcNow;
        try
        {
            registry.Update("claude-code", "native-lifetime", "thinking", "Fixture", 5, now, out _, session.Id);
            registry.Sweep(now.AddSeconds(10));
            Check(AgentStatus.HasClaim(nativeOwner), "原生 Claude 会话持续思考时，不因固定状态租约到期而掉岛");
            registry.Update("claude-code", "native-lifetime", "tool-wait", "Fixture", 5, now, out _, session.Id);
            registry.Sweep(now.AddSeconds(10));
            Check(AgentStatus.HasClaim(nativeOwner), "原生 Claude 等待用户授权时保持该会话提示");
            registry.Update("claude-code", "native-lifetime", "idle", "", 5, now, out _, session.Id);
            Check(!AgentStatus.HasClaim(nativeOwner), "完成事件立即撤回自己的原生 Claude 会话提示");
            registry.Update("claude-code", "native-lifetime", "error", "Fixture", 5, now, out _, session.Id);
            registry.Sweep(now.AddSeconds(10));
            Check(!AgentStatus.HasClaim(nativeOwner), "短暂错误提示仍按租约结束，不因会话进程存在而常驻");
            registry.Update("claude-code", "native-lifetime", "thinking", "Fixture", 5, now, out _, session.Id);
            session.Kill(entireProcessTree: true); await session.WaitForExitAsync(); registry.Sweep(now.AddSeconds(1));
            Check(!AgentStatus.HasClaim(nativeOwner), "原生 Claude 关闭后立即清理长任务提示");
        }
        finally
        {
            if (!session.HasExited) { session.Kill(entireProcessTree: true); await session.WaitForExitAsync(); }
            registry.Clear();
            string absolute = Path.GetFullPath(fixtureDirectory);
            if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(absolute).StartsWith("AlphaIsland.ClaudeLifetime-", StringComparison.Ordinal)
                || (File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unsafe fixture cleanup target");
            Directory.Delete(absolute, recursive: true);
        }
        foreach (string provider in new[] { "codex", "antigravity" })
        {
            string fixtureRoot = Path.Combine(Path.GetTempPath(), "AlphaIsland.HostLifetime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixtureRoot);
            string executable = Path.Combine(fixtureRoot, provider + ".exe");
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), executable);
            using var host = Process.Start(new ProcessStartInfo(executable)
            { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "/d", "/c", "ping -n 30 127.0.0.1 >nul" } })!;
            var nativeRegistry = new ExternalAgentRegistry();
            string hostOwner = "external:" + provider + "/long-task";
            try
            {
                nativeRegistry.Update(provider, "long-task", "thinking", "Fixture", 300, now, out _, host.Id);
                nativeRegistry.Sweep(now.AddMinutes(6));
                Check(AgentStatus.HasClaim(hostOwner), provider + " 原生宿主仍运行时，长思考不因五分钟无工具事件而掉岛");
                nativeRegistry.Update(provider, "long-task", "working", "Fixture", 300, now, out _, host.Id);
                nativeRegistry.Sweep(now.AddMinutes(10));
                Check(AgentStatus.HasClaim(hostOwner), provider + " 长工具执行保持提示");
                nativeRegistry.Update(provider, "long-task", "idle", "", 5, now, out _, host.Id);
                Check(!AgentStatus.HasClaim(hostOwner), provider + " 结束事件立即清理，不等待宿主退出");
                nativeRegistry.Update(provider, "long-task", "thinking", "Fixture", 300, now, out _, host.Id);
                host.Kill(true); await host.WaitForExitAsync(); nativeRegistry.Sweep(now.AddSeconds(1));
                Check(!AgentStatus.HasClaim(hostOwner), provider + " 宿主退出仍及时清理");
            }
            finally
            {
                if (!host.HasExited) { host.Kill(true); await host.WaitForExitAsync(); }
                nativeRegistry.Clear();
                string absolute = Path.GetFullPath(fixtureRoot);
                if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(absolute).StartsWith("AlphaIsland.HostLifetime-", StringComparison.Ordinal)
                    || (File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unsafe fixture cleanup target");
                Directory.Delete(absolute, true);
            }
        }
    }
}
