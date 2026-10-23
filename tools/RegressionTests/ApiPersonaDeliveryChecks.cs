using System.IO;
using System.Net;
using System.Net.Http;
using alpha_island.Services;
using AlphaIsland.AgentTools;
using AlphaIsland.AgentTools.IslandTools;

internal static partial class Program
{
    private static async Task ApiPersonaDeliveryChecks(string root)
    {
        const string personality = "PERSONA_FIRST_REQUEST_ONLY";
        const string rules = "FIXED_RULES_FOR_EVERY_REQUEST";
        var config = new ChatConfig("https://example.invalid/v1", "", "fixture", rules, .7, 1024);
        using var registry = new ToolRegistry(() => true);
        foreach (var tool in DesktopTools.Create(new DesktopToolFixture())) registry.Register("island", tool);
        int accepted = 0;
        using var toolHttp = new AgentHttpFixture((n, _) => AgentResponse(n == 1
            ? AgentSse(new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, id = "once", function = new { name = "set_mute", arguments = "{\"muted\":true}" } } } }, finish_reason = "tool_calls" } } })
            : AgentSse(new { choices = new[] { new { delta = new { content = "done" }, finish_reason = "stop" } } })));
        using var toolClient = new HttpClient(toolHttp);
        using var toolChat = new ChatService(toolClient, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        await foreach (var _ in toolChat.RunAgentAsync([new("user", "静音")], config, registry, "once", cancellationToken: default, initialSystemPrompt: rules + personality,
            onFirstRequestAccepted: () => accepted++)) { }
        Check(toolHttp.Bodies.Count == 2 && toolHttp.Bodies[0].Contains(personality) && !toolHttp.Bodies[1].Contains(personality)
            && toolHttp.Bodies.All(b => b.Contains(rules)), "首次工具请求带人格，工具续接只带固定规则，不重复发送人格");
        Check(accepted == 1, "同一条指令的多次工具续接只确认一次首次请求");

        int fallbackAccepted = 0;
        using var fallbackHttp = new AgentHttpFixture((n, _) => n == 1
            ? new(HttpStatusCode.BadRequest) { Content = new StringContent("tools unsupported") }
            : AgentResponse(AgentSse(new { choices = new[] { new { delta = new { content = "done" }, finish_reason = "stop" } } })));
        using var fallbackClient = new HttpClient(fallbackHttp);
        using var fallbackChat = new ChatService(fallbackClient, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        await foreach (var _ in fallbackChat.RunAgentAsync([new("user", "静音")], config, registry, "fallback", cancellationToken: default, initialSystemPrompt: rules + personality,
            onFirstRequestAccepted: () => fallbackAccepted++)) { }
        Check(fallbackHttp.Bodies.Count == 2 && fallbackHttp.Bodies.All(b => b.Contains(personality)) && fallbackAccepted == 1,
            "工具参数被拒后的普通对话回退仍携带首次人格，失败请求不消耗首次标记");

        int rejectedAccepted = 0;
        using var rejectedHttp = new AgentHttpFixture((_, _) => new(HttpStatusCode.Forbidden) { Content = new StringContent("denied") });
        using var rejectedClient = new HttpClient(rejectedHttp);
        using var rejectedChat = new ChatService(rejectedClient, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        try
        {
            await foreach (var _ in rejectedChat.RunAgentAsync([new("user", "你好")], config, registry, "rejected", cancellationToken: default, initialSystemPrompt: rules + personality,
                onFirstRequestAccepted: () => rejectedAccepted++)) { }
        }
        catch (ChatException) { }
        Check(rejectedAccepted == 0 && rejectedHttp.Bodies.Count == 1, "HTTP 请求失败不记录人格已发送，下一次可重新尝试");

        string directory = Path.Combine(root, "persona-session-delivery");
        var store = new ChatSessionStore(directory); string id = store.Create();
        store.Save(id, [new("user", "本地提醒") { LocalOnly = true }, new("assistant", "已记录") { LocalOnly = true }]);
        Check(!store.HasStartedApi(id), "本地提醒不消耗首次 API 人格");
        store.MarkApiStarted(id);
        var reopened = new ChatSessionStore(directory);
        Check(reopened.HasStartedApi(id) && !File.ReadAllText(Path.Combine(directory, id + ".json")).Contains(personality),
            "重开历史及重启后保留首次标记，会话文件不额外保存人格正文");
        reopened.SaveTail(id, 0, [new("user", "你好")]);
        Check(new ChatSessionStore(directory).HasStartedApi(id), "保存或裁剪当前历史不重置人格首次标记");
        reopened.Save(id, []);
        Check(!reopened.HasStartedApi(id), "清空会话后下一次可重新附加当前人格");
        reopened.Save("legacy", [new("user", "你好"), new("assistant", "你好")]);
        Check(reopened.HasStartedApi("legacy"), "升级前已有 API 回答的会话视为已开始，不重复附加人格");
        Check(!reopened.HasStartedApi(reopened.Create()), "新会话独立维护首次发送状态");
    }
}
