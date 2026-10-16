using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static async Task AgentConversationStateChecks(string testRoot)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var bridge = new AgentBridgeService(port, _ => false); bridge.Start();
        using var http = new HttpClient { BaseAddress = new Uri(bridge.Endpoint) };
        async Task Report(string session, string state, string? parent = null, string? alias = null)
        {
            using var result = await http.PostAsync("status", new StringContent(JsonSerializer.Serialize(new
            { source = "codex", session, state, parentSession = parent, sessionAlias = alias, ttlSec = 300 }), Encoding.UTF8, "application/json"));
            Check(result.IsSuccessStatusCode, "桥接接受会话状态 " + session + "/" + state);
        }
        AgentActivity[] Active() => AgentStatus.Snapshot.Where(item => item.Owner.StartsWith("external:codex/")).ToArray();
        try
        {
            await Report("root-a", "thinking"); await Report("root-b", "thinking");
            await Report("child-a", "thinking", "root-a"); await Report("child-b", "thinking", "root-a");
            Check(Active().Length == 2, "两个主对话及其子智能体只显示两个助手岛");
            await Report("child-a", "tool-wait", "root-a");
            Check(Active().Single(item => item.Owner == "external:codex/root-a").State == AgentRunState.ToolWait, "子智能体审批归到所属主对话");
            await Report("child-b", "idle", "root-a");
            Check(Active().Length == 2 && Active().Single(item => item.Owner == "external:codex/root-a").State == AgentRunState.ToolWait, "子智能体结束不收回仍在工作的主对话");
            await Report("root-a", "idle"); await Report("child-a", "thinking", "root-a");
            Check(Active().Length == 1 && Active()[0].Owner == "external:codex/root-b", "主对话结束后，迟到子事件不复活额外岛");
            await Report("root-b", "working");
            Check(Active()[0].State.ToString() == "Working", "执行任务状态与思考状态独立");
            await Report("grandchild", "tool-wait", "nested-child");
            await Report("nested-child", "thinking", "root-b");
            Check(Active().Length == 1 && Active()[0].State == AgentRunState.ToolWait, "父关系后到时，嵌套子智能体仍归到同一主会话");
            await Report("nested-child", "idle", "root-b");
            await Report("grandchild", "idle", "nested-child");
            await Report("old-transport", "thinking");
            await Report("native-conversation", "thinking", alias: "old-transport");
            Check(Active().Length == 2 && !Active().Any(item => item.Owner.EndsWith("/old-transport")), "原生身份替换传输身份时清理旧岛");
            await Report("old-transport", "thinking");
            Check(Active().Length == 2, "旧传输标识的迟到事件经过别名映射，不再生成额外岛");
            await Report("native-conversation", "idle");
            await Report("root-b", "streaming");
            Check(Active()[0].State == AgentRunState.Streaming, "输出状态继续独立保留");
            await Report("root-b", "idle");

            // The desktop exposes execution IDs and frontend IDs separately. Exercise
            // the actual adapter and HTTP bridge together, including overlapping Stop.
            string repository = Environment.CurrentDirectory;
            string hook = Path.Combine(repository, "examples", "CodexHooks", "hook.mjs");
            string node = Path.Combine(repository, "bin", "InstallerTools", "node-v24.21.0", "node.exe");
            if (!File.Exists(node)) node = "node";
            string config = Path.Combine(testRoot, "codex-hooks.json");
            await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
            { endpoint = bridge.Endpoint, runtimeDir = testRoot, allProjects = true, approvalMode = "native" }));
            var transcripts = new string[2];
            for (int i = 0; i < transcripts.Length; i++)
            {
                transcripts[i] = Path.Combine(testRoot, $"execution-{i}.jsonl");
                await File.WriteAllTextAsync(transcripts[i], JsonSerializer.Serialize(new
                { type = "session_meta", payload = new { id = $"execution-{i}", session_id = "frontend-chat", parent_thread_id = "frontend-chat", source = new { subagent = new { other = "task" } } } }) + "\n");
            }
            async Task Hook(int index, string eventName, bool includeMetadata = true, string inputSession = "frontend-chat")
            {
                var start = new ProcessStartInfo(node)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add(hook); start.ArgumentList.Add("--config"); start.ArgumentList.Add(config);
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new
                { session_id = inputSession, transcript_path = includeMetadata ? transcripts[index] : null, cwd = testRoot, hook_event_name = eventName, tool_name = "Bash" }));
                process.StandardInput.Close();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch { if (!process.HasExited) process.Kill(true); throw; }
                Check(process.ExitCode == 0 && (await output).Trim() == "{}" && string.IsNullOrWhiteSpace(await error), "原生 Codex Hook 正常完成 " + eventName);
            }
            string frontendOwner = "external:codex/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("frontend-chat"))).ToLowerInvariant()[..24];
            await Hook(0, "UserPromptSubmit", includeMetadata: false, inputSession: "early-frontend-chat");
            long startingActivity = Active().Single().ActivityId;
            await Hook(0, "SessionStart", includeMetadata: false, inputSession: "early-frontend-chat");
            Check(Active().Length == 1 && Active()[0].ActivityId == startingActivity && Active()[0].State == AgentRunState.Thinking,
                "新任务上报后迟到的会话初始化不能清除思考提示");
            await Hook(0, "Stop", includeMetadata: false, inputSession: "early-frontend-chat");
            await Report(frontendOwner["external:codex/".Length..], "idle"); // SessionStart before execution metadata exists.
            await Hook(0, "PreToolUse"); await Hook(1, "PreToolUse");
            Check(Active().Length == 1 && Active()[0].Owner == frontendOwner && Active()[0].State == AgentRunState.Working,
                "真实桌面元数据经过 Node 和 HTTP 后，两个执行线程只显示一个前台对话岛");
            await Hook(0, "Stop");
            Check(Active().Length == 1 && Active()[0].Owner == frontendOwner, "一个执行线程结束后，仍在运行的同会话线程保留小岛");
            await Hook(1, "Stop");
            Check(Active().Length == 0, "同会话执行线程全部结束后正确收回小岛");
            await Hook(0, "UserPromptSubmit");
            await Report(frontendOwner["external:codex/".Length..], "idle");
            Check(Active().Length == 0, "前台对话明确结束时清理所属执行线程");
            await Hook(1, "UserPromptSubmit");
            Check(Active().Length == 1 && Active()[0].Owner == frontendOwner, "明确提交下一轮任务能清除旧结束记录并立即恢复提示");
            await Hook(0, "PreToolUse");
            Check(Active().Length == 1 && Active()[0].State == AgentRunState.Thinking, "上一轮执行线程的迟到事件不能污染新一轮任务");
            long priorRound = Active().Single().ActivityId;
            await Hook(1, "UserPromptSubmit");
            long nextRound = Active().Single().ActivityId;
            Check(nextRound != priorRound, "真实 Node 新任务事件经过 HTTP 后，即使旧轮仍活跃也更新任务身份");
            await Hook(1, "PostToolUse");
            Check(Active().Single().ActivityId == nextRound, "真实工具返回与续期不会伪造新一轮任务");
            await Report("parallel-codex", "thinking");
            long parallelActivity = Active().Single(item => item.Owner.EndsWith("/parallel-codex")).ActivityId;
            await Hook(1, "UserPromptSubmit");
            Check(Active().Length == 2 && Active().Single(item => item.Owner.EndsWith("/parallel-codex")).ActivityId == parallelActivity,
                "一个对话提交新任务不会重新弹出另一个仍在思考的助手岛");
            await Report("parallel-codex", "idle");
            await Hook(1, "Stop");
        }
        finally { bridge.Stop(); }
    }
}
