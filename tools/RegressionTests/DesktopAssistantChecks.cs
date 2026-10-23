using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AlphaIsland.AgentTools;
using AlphaIsland.AgentTools.IslandTools;
using AlphaIsland.PluginSdk;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private sealed class DesktopToolFixture : IIslandToolHost
    {
        internal int Calls;
        internal Func<CancellationToken, Task>? Delay;
        public async ValueTask<ToolResult> ExecuteAsync(string command, JsonElement arguments, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); if (Delay != null) await Delay(cancellationToken); return ToolResult.Ok(command, new { arguments }); }
    }
    private sealed class AgentHttpFixture(Func<int, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { string body = await request.Content!.ReadAsStringAsync(token); Bodies.Add(body); return respond(Bodies.Count, body); }
    }
    private static string AgentSse(params object[] chunks) => string.Join("", chunks.Select(c => "data: " + JsonSerializer.Serialize(c) + "\n\n")) + "data: [DONE]\n\n";
    private static HttpResponseMessage AgentResponse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    private static async Task DesktopToolChecks(string root)
    {
        bool enabled = true; var host = new DesktopToolFixture();
        using var registry = new ToolRegistry(() => enabled);
        foreach (var tool in DesktopTools.Create(host)) registry.Register("island", tool);
        var definitions = await registry.GetToolsAsync();
        Check(definitions.Length == 10 && definitions.All(t => t.Name is not "shell" and not "file_write") && definitions.Any(t => t.Name == "create_reminder") && definitions.Single(t => t.Name == "get_island_info").Risk == ToolRiskLevel.ReadOnly, "只开放明确的岛控、只读状态与本机提醒工具，不开放任意文件和 Shell");
        JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();
        var request = new ToolRequest("once", "set_volume", Args("{\"percent\":35}"), "session-a");
        var both = await Task.WhenAll(registry.ExecuteAsync(request).AsTask(), registry.ExecuteAsync(request).AsTask());
        Check(both.All(r => r.Success) && host.Calls == 1, "并发重传相同调用只执行一次，复用同一结果");
        Check(!(await registry.ExecuteAsync(request with { Arguments = Args("{\"percent\":50}") })).Success && host.Calls == 1, "调用 ID 相同但参数变化时拒绝再次执行");
        Check(!(await registry.ExecuteAsync(request with { CallId = "invalid", Arguments = Args("{\"percent\":300}") })).Success && host.Calls == 1, "越界音量不会传给实际设备接口");
        Check(!(await registry.ExecuteAsync(request with { CallId = "extra", Arguments = Args("{\"percent\":30,\"shell\":\"ignored\"}") })).Success, "未知参数不会进入工具实现");
        enabled = false;
        Check((await registry.GetToolsAsync()).Length == 0 && !(await registry.ExecuteAsync(request with { CallId = "disabled" })).Success, "总开关关闭后既不公布工具也不执行缓存外请求"); enabled = true;
        using var owner = new CancellationTokenSource(); bool allowed = false;
        registry.Register("plugin", new TestToolHandler("plugin__echo", ToolRiskLevel.ReadOnly), owner.Token, () => allowed);
        Check(!(await registry.GetToolsAsync()).Any(t => t.Name == "plugin__echo"), "第三方工具的自报低风险不会绕过宿主未开启的授权");
        allowed = true; Check((await registry.GetToolsAsync()).Any(t => t.Name == "plugin__echo"), "用户允许后第三方工具可被模型发现");
        owner.Cancel(); Check(!(await registry.GetToolsAsync()).Any(t => t.Name == "plugin__echo"), "停用插件立即撤回其工具");
        registry.Register("danger", new TestToolHandler("dangerous", ToolRiskLevel.RequiresConfirmation));
        Check(!(await registry.ExecuteAsync(new("risk", "dangerous", Args("{}")))).Success, "首期高风险工具即使注册也不会被自动执行");
        host.Delay = token => Task.Delay(1000, token); using var cancel = new CancellationTokenSource(25);
        var canceled = await registry.ExecuteAsync(request with { CallId = "cancel" }, cancel.Token);
        Check(!canceled.Success && host.Calls == 2, "取消信号传到处理器并返回明确状态"); host.Delay = null;

        using var http = new AgentHttpFixture((n, _) => AgentResponse(n == 1 ? AgentSse(
            new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, id = "agent-pomodoro", function = new { name = "pomodoro_control", arguments = "{\"action\":\"sta" } } } } } } },
            new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, function = new { arguments = "rt\",\"minutes\":25}" } } } } } } },
            new { choices = new[] { new { finish_reason = "tool_calls", delta = new { } } }, usage = new { prompt_tokens = 100, completion_tokens = 10, prompt_cache_hit_tokens = 20 } })
            : AgentSse(new { choices = new[] { new { delta = new { content = "工具返回成功，番茄钟已启动。" }, finish_reason = "stop" } }, usage = new { prompt_tokens = 140, completion_tokens = 15 } })));
        using var client = new HttpClient(http); using var chat = new ChatService(client, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        var config = new ChatConfig("https://example.invalid/v1", "", "fixture-model", "", .7, 1024);
        var events = new List<AgentChatEvent>();
        await foreach (var item in chat.RunAgentAsync([new("user", "启动25分钟番茄钟")], config, registry, "native-api")) events.Add(item);
        Check(host.Calls == 3 && events.Any(e => e.Kind == "tool") && events.Any(e => e.Kind == "text"), "真实 SSE 工具参数分片汇合后执行，再继续流式回复");
        using var second = JsonDocument.Parse(http.Bodies[1]);
        Check(second.RootElement.GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("role").GetString() == "tool" && m.GetProperty("tool_call_id").GetString() == "agent-pomodoro"), "执行结果按对应 tool_call_id 返回给接口");
        Check(chat.LastUsage is { InputTokens: 240, OutputTokens: 25, CachedTokens: 20 }, "用量合计包含工具前后两次请求，不把会话复用声称为免费");
        using var fallbackHttp = new AgentHttpFixture((n, _) => n == 1 ? new(HttpStatusCode.BadRequest) { Content = new StringContent("tools unsupported") }
            : AgentResponse(AgentSse(new { choices = new[] { new { delta = new { content = "普通聊天仍可使用" }, finish_reason = "stop" } } })));
        using var fallbackClient = new HttpClient(fallbackHttp); using var fallback = new ChatService(fallbackClient, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        await foreach (var _ in fallback.RunAgentAsync([new("user", "调整音量")], config, registry, "fallback")) { }
        Check(fallbackHttp.Bodies.Count == 2 && !fallbackHttp.Bodies[1].Contains("\"tools\"") && host.Calls == 3, "接口拒绝工具参数时仅在尚未执行操作前回退普通聊天");
        using var noToolsHttp = new AgentHttpFixture((_, body) => AgentResponse(AgentSse(new { choices = new[] { new { delta = new { content = "诗句" }, finish_reason = "stop" } } })));
        using var noToolsClient = new HttpClient(noToolsHttp); using var noTools = new ChatService(noToolsClient, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        await foreach (var _ in noTools.RunAgentAsync([new("user", "帮我写首诗")], config, registry, "poem")) { }
        Check(!noToolsHttp.Bodies[0].Contains("\"set_volume\"") && !noToolsHttp.Bodies[0].Contains("\"pomodoro_control\""), "明显无关的问题不重复发送桌面控制工具定义");
        var snapshot = new DesktopContextSnapshot(DateTimeOffset.UtcNow, new() { ["camera"] = "Teams", ["cpu"] = "90%", ["media"] = "测试歌曲" });
        Check(DesktopContextPolicy.Filter(snapshot, "帮我写首诗").Fields.Count == 0, "摄像头被占用也不会给写诗请求注入上下文");
        var meeting = DesktopContextPolicy.Filter(snapshot, "会议中摄像头情况如何");
        Check(meeting.Fields.Count == 1 && meeting.Fields.ContainsKey("camera"), "会议提问仅附加相关字段，不附带歌曲和 CPU");
        var sessions = new ChatSessionStore(Path.Combine(root, "desktop-context")); string session = sessions.Create();
        sessions.Save(session, [new("user", "会议")]); sessions.WriteDesktopContext(session, "first-context"); sessions.Save(session, [new("user", "会议"), new("assistant", "回答")]);
        Check(new ChatSessionStore(sessions.DirectoryPath).ReadDesktopContext(session) == "first-context", "状态包按真实会话保存，重启和多轮不会新生成第二份");
        sessions.Save(session, []); Check(sessions.ReadDesktopContext(session).Length == 0, "清空会话同时清除其附加状态");
        int beforeTurns = host.Calls;
        ToolRequest Turn(string id) => JsonSerializer.Deserialize<ToolRequest>(JsonSerializer.Serialize(new {
            CallId = "provider-reused-id", Name = "set_volume", Arguments = new { percent = 35 }, SessionId = "same-conversation", TurnId = id }))!;
        await registry.ExecuteAsync(Turn("logical-turn-a")); await registry.ExecuteAsync(Turn("logical-turn-b"));
        Check(host.Calls == beforeTurns + 2, "接口在不同提问中复用 tool_call_id 时，两次明确操作仍各执行一次");
        await registry.ExecuteAsync(Turn("logical-turn-a"));
        Check(host.Calls == beforeTurns + 2, "同一逻辑提问的重传仍复用结果，不与下一轮混淆");
    }
    private sealed class TestToolHandler(string name, ToolRiskLevel risk) : IToolHandler
    {
        public ToolDefinition Definition { get; } = new(name, "测试工具", JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }), risk);
        public ValueTask<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(ToolResult.Ok("回声"));
    }
    private static void DesktopLocalChecks(string root)
    {
        const string id = "11010519491231002X", card = "4111111111111111";
        string key = "sk-" + new string('x', 28);
        Check(SensitiveTextDetector.Inspect(id).Sensitive && SensitiveTextDetector.Inspect(card).Sensitive && SensitiveTextDetector.Inspect(key).Sensitive, "身份证校验、银行卡校验和已知密钥格式都在本地识别");
        Check(!SensitiveTextDetector.Inspect("订单 12345678901234567890").Sensitive && !SensitiveTextDetector.Inspect("普通文字").Sensitive, "普通文本和不匹配的长订单号不过度阻断");
        Check(!SensitiveTextDetector.Inspect("Key=" + key).Redacted.Contains(key), "预览脱敏结果不保留访问密钥原文");
        var guard = new MeetingGuard(); var now = DateTimeOffset.UtcNow;
        Check(guard.Observe(["Teams"], true, true, now) == null, "会议设备刚开始使用时先稳定观察，避免瞬态提示");
        Check(guard.Observe(["Teams"], true, true, now.AddSeconds(7))?.Kind == "microphone", "会议应用稳定占用且输入端点静音后给出麦克风提醒");
        guard.Observe(["Teams"], true, true, now.AddSeconds(10));
        Check(guard.Observe(["Teams"], true, true, now.AddSeconds(13)) == null, "同一会议不会循环提醒");
        var browser = new MeetingGuard(); browser.Observe(["Chrome"], true, true, now);
        Check(browser.Observe(["Chrome"], true, true, now.AddMinutes(1)) == null, "浏览器占用摄像头不被猜测为某个会议网页");
        Check(new MeetingGuard().Observe(["Teams"], false, true, now.AddMinutes(1)) == null, "状态过期或读取失败时不触发提醒");
        var ledger = new TimelineLedger(TimeZoneInfo.Utc); var t = new DateTimeOffset(2026, 10, 16, 23, 59, 50, TimeSpan.Zero);
        var active = new Dictionary<string, (string, string)> { ["music"] = ("music", "曲目"), ["game"] = ("game", "游戏") };
        ledger.Observe(active, t, 1000); ledger.Observe(active, t.AddSeconds(5), 6000);
        Check(ledger.Snapshot().Entries.Length == 2, "连续采样合并为同一活动段，不每三秒追加记录");
        ledger.Observe(active, t.AddSeconds(15), 16000);
        Check(ledger.Snapshot().Entries.Length == 4 && ledger.Snapshot().Entries.Count(e => e.Ended == null) == 2, "跨午夜活动按日期拆段");
        var recovered = new TimelineLedger(TimeZoneInfo.Utc); recovered.Restore(ledger.Snapshot());
        Check(recovered.Snapshot().Entries.All(e => e.Ended != null) && recovered.Snapshot().Entries.Count(e => e.Interrupted) == 2, "异常退出恢复时在最后观察时关闭记录，不补算关机期间");
        ledger.Observe(active, t.AddHours(1), 3601000);
        Check(ledger.Snapshot().Entries.Any(e => e.Interrupted) && ledger.Snapshot().Entries.Count(e => e.Ended == null) == 2, "长时间采样缺口结束旧段，再从当前时间开始");
        ledger.CloseAll(t.AddHours(1).AddSeconds(2)); Check(ledger.Snapshot().Entries.All(e => e.Ended != null), "锁屏或休眠可立即结束所有活动段");
        ledger.Clear(t); Check(ledger.Snapshot().Entries.Length == 0, "用户清空记录同时移除活动段索引");
    }
}
