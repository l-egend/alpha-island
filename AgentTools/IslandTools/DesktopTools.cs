using System.Text.Json;
using AlphaIsland.PluginSdk;

namespace AlphaIsland.AgentTools.IslandTools;

public interface IIslandToolHost
{
    ValueTask<ToolResult> ExecuteAsync(string command, JsonElement arguments, CancellationToken cancellationToken);
}

public static class DesktopTools
{
    public static IEnumerable<IToolHandler> Create(IIslandToolHost host)
    {
        yield return Tool("set_volume", "设置系统扬声器音量，范围 0 到 100。", """
            {"type":"object","properties":{"percent":{"type":"integer","minimum":0,"maximum":100}},"required":["percent"],"additionalProperties":false}
            """, host);
        yield return Tool("set_mute", "设置系统扬声器静音。muted=true 为静音，false 为取消静音。", """
            {"type":"object","properties":{"muted":{"type":"boolean"}},"required":["muted"],"additionalProperties":false}
            """, host);
        yield return Tool("media_control", "控制当前选定的媒体播放器。必须按用户请求选择操作。", """
            {"type":"object","properties":{"action":{"type":"string","enum":["play","pause","next","previous"]}},"required":["action"],"additionalProperties":false}
            """, host);
        yield return Tool("pomodoro_control", "启动、继续、暂停或重置番茄钟。仅 start 可使用 minutes（1 到 240）；已有计时不会被 start 覆盖。", """
            {"type":"object","properties":{"action":{"type":"string","enum":["start","resume","pause","reset"]},"minutes":{"type":"integer","minimum":1,"maximum":240}},"required":["action"],"additionalProperties":false}
            """, host);
        yield return Tool("get_desktop_state", "在用户询问电脑当前状态时读取最新的最小系统状态。没有聊天、剪贴板、路径或窗口标题。", """
            {"type":"object","properties":{},"additionalProperties":false}
            """, host, ToolRiskLevel.ReadOnly);
        yield return Tool("get_island_info", "按需读取岛内已采集状态。devices 查鼠标/键盘/耳机/手柄电量与充电，island 查能力概况，其余分区见参数。只选相关 section，all 仅用于明确的整体查询。当前值及追问重新读取，未知和过期值不可猜测。不返回聊天/剪贴板正文、设备标识、路径或密钥。", """
            {"type":"object","properties":{"section":{"type":"string","enum":["devices","power","media","audio","resources","network","weather","focus","productivity","notifications","agents","clipboard","reminders","launcher","plugins","island","privacy","foreground","all"]}},"required":["section"],"additionalProperties":false}
            """, host, ToolRiskLevel.ReadOnly);
        yield return Tool("create_reminder", "按用户明确要求创建本地提醒。text 必须含明确时间与事项，例如30分钟后提醒我喝水、每个工作日9点提醒我专注。模糊时间须先询问用户。", """
            {"type":"object","properties":{"text":{"type":"string","minLength":1,"maxLength":512}},"required":["text"],"additionalProperties":false}
            """, host);
        yield return Tool("list_reminders", "列出本机待办提醒及取消所需的ID，不修改提醒。", """
            {"type":"object","properties":{},"additionalProperties":false}
            """, host, ToolRiskLevel.ReadOnly);
        yield return Tool("cancel_reminder", "仅按用户明确请求取消提醒；提供列表中的 id，或用户给出的完整 title。同名多条时需要用户选择，不能编造 ID。", """
            {"type":"object","properties":{"id":{"type":"string","minLength":32,"maxLength":32},"title":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """, host);
        yield return Tool("snooze_reminder", "用户明确要求后，把指定提醒延后1到1440分钟；不改变原有重复日程。", """
            {"type":"object","properties":{"id":{"type":"string","minLength":32,"maxLength":32},"minutes":{"type":"integer","minimum":1,"maximum":1440}},"required":["id","minutes"],"additionalProperties":false}
            """, host);
    }
    private static IToolHandler Tool(string name, string description, string schema, IIslandToolHost host, ToolRiskLevel risk = ToolRiskLevel.DeviceControl)
    {
        using var doc = JsonDocument.Parse(schema);
        return new Handler(new(name, description, doc.RootElement.Clone(), risk), host);
    }
    private sealed class Handler(ToolDefinition definition, IIslandToolHost host) : IToolHandler
    {
        public ToolDefinition Definition => definition;
        public ValueTask<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken)
            => host.ExecuteAsync(definition.Name, request.Arguments, cancellationToken);
    }
}
