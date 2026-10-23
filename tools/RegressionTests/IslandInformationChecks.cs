using System.Net;
using System.Net.Http;
using System.Text.Json;
using AlphaIsland.AgentTools;
using AlphaIsland.AgentTools.IslandTools;
using AlphaIsland.PluginSdk;
using alpha_island.Services;
using alpha_island.Core;
using alpha_island.ViewModels;

internal static partial class Program
{
    private static async Task IslandInformationAgentChecks()
    {
        using var registry = new ToolRegistry(() => true);
        foreach (var tool in DesktopTools.Create(new DesktopToolFixture())) registry.Register("island", tool);
        using var handler = new AgentHttpFixture((_, _) => AgentResponse(AgentSse(new
        {
            choices = new[] { new { delta = new { content = "本机测试回复" }, finish_reason = "stop" } }
        })));
        using var http = new HttpClient(handler);
        using var service = new ChatService(http, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        var config = new ChatConfig("http://127.0.0.1/v1", TestApiKey, "test-model", "", .7, 1024);
        await foreach (var _ in service.RunAgentAsync([new("user", "我的鼠标还有多少电？")], config, registry, "battery-query")) { }
        using var request = JsonDocument.Parse(handler.Bodies.Single());
        Check(request.RootElement.TryGetProperty("tools", out var tools) && tools.EnumerateArray().Any(tool =>
                tool.GetProperty("function").GetProperty("name").GetString() is "get_desktop_state" or "get_island_info"),
            "询问鼠标电量时，实际 API 请求包含读取岛内状态的工具");
        var snapshot = new DesktopContextSnapshot(DateTimeOffset.UtcNow, new() { ["deviceBatteries"] = "MCHOSE K5 Ultra：78%，已连接" });
        Check(DesktopContextPolicy.Filter(snapshot, "我的鼠标还有多少电？").Fields.ContainsKey("deviceBatteries"),
            "鼠标电量进入相关上下文，不再被状态字段白名单丢弃");
        foreach (var (prompt, section) in new (string, string)[] {
            ("键盘电量", "devices"), ("耳机正在充电吗", "devices"), ("手柄还剩多少电", "devices"),
            ("今天下载了多少流量", "network"), ("天气如何", "weather"), ("番茄钟还有多久", "focus"),
            ("今天玩了多久", "productivity"), ("通知有多少未读", "notifications"), ("有哪些插件", "plugins"),
            ("扬声器音量多大", "audio"), ("内存使用多少", "resources") })
            Check(DesktopContextPolicy.RequestedSections(prompt).Contains(section), "相关提问选择对应分区：" + prompt);
        Check(DesktopContextPolicy.Filter(snapshot, "帮我写首诗").Fields.Count == 0, "写诗不注入设备电量");
        await foreach (var _ in service.RunAgentAsync([new("user", "帮我写首诗")], config, registry, "poem")) { }
        Check(!handler.Bodies[^1].Contains("get_island_info"), "明确无关的写诗请求不提供岛内读取工具");
        await foreach (var _ in service.RunAgentAsync([new("user", "我的鼠标还有多少电？"), new("assistant", "78%"), new("user", "现在呢？")], config, registry, "battery-query")) { }
        Check(handler.Bodies[^1].Contains("get_island_info"), "同一会话简短追问仍可发现最新状态读取能力");
        var definition = (await registry.GetToolsAsync()).Single(t => t.Name == "get_island_info");
        var names = definition.Parameters.GetProperty("properties").GetProperty("section").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Check(DesktopContextPolicy.InformationSections.All(names.Contains) && names.Contains("all"), "工具 schema 与状态分区一一对应");
        var options = new IslandOptions();
        var context = new ConversationPluginFixture(options, System.IO.Path.GetTempPath()) { ToolDispatcher = registry };
        var dispatcher = new AlphaIsland.Plugins.ApiChat.ApiToolDispatcher(context);
        SettingsRegistry.SetExtra(options, "AiDesktopContextEnabled", false);
        Check(!(await dispatcher.GetToolsAsync()).Any(t => t.Name is "get_island_info" or "get_desktop_state"), "关闭系统状态开关同时隐藏两种状态读取工具");
        Check(!(await dispatcher.ExecuteAsync(new("disabled-state", "get_island_info", JsonSerializer.SerializeToElement(new { section = "devices" })))).Success,
            "绕过列表直接请求也无法在状态开关关闭时执行");
        SettingsRegistry.SetExtra(options, "AiDesktopContextEnabled", true); SettingsRegistry.SetExtra(options, "AiToolsEnabled", false);
        Check((await dispatcher.GetToolsAsync()).Length == 0, "关闭工具开关后不提供任何工具");
        IslandInformationDataChecks();
        await IslandInformationRoundTripChecks(config);
    }
    private static DeviceBatterySample InformationDevice(string id, string name, DeviceBatteryKind kind, int? percent,
        DateTimeOffset time, bool connected = true, bool charging = false, string source = "receiver", DeviceBatteryTransport transport = DeviceBatteryTransport.WirelessReceiver,
        string physicalId = "", bool expires = true)
        => new(source, new(id, name, transport, kind, percent, connected, charging, PhysicalId: physicalId), time, expires);
    private static void IslandInformationDataChecks()
    {
        var now = DateTimeOffset.Now;
        const string hardwareId = "private-hardware-id";
        var samples = new[] {
            InformationDevice("private-mouse-id", "MCHOSE K5 Ultra", DeviceBatteryKind.Mouse, 78, now, charging: true),
            InformationDevice("keyboard", "我的键盘", DeviceBatteryKind.Keyboard, 48, now, source: "bluetooth", transport: DeviceBatteryTransport.Bluetooth),
            InformationDevice("gamepad", "飞智黑武士 4 Pro", DeviceBatteryKind.Gamepad, null, now),
            InformationDevice("disconnected", "已断开耳机", DeviceBatteryKind.Audio, 90, now, connected: false),
            InformationDevice("expired", "过期鼠标", DeviceBatteryKind.Mouse, 99, now.AddMinutes(-4)),
            InformationDevice("zero", "零电量鼠标", DeviceBatteryKind.Mouse, 0, now),
            InformationDevice("windows-earbuds", "OPPO Enco Clip2", DeviceBatteryKind.Audio, 90, now, source: "bluetooth", transport: DeviceBatteryTransport.Bluetooth, physicalId: hardwareId),
            InformationDevice("earbuds/left", "OPPO Enco Clip2 · 左耳", DeviceBatteryKind.Audio, 70, now, source: "pods", transport: DeviceBatteryTransport.Bluetooth, physicalId: hardwareId + "/left"),
            InformationDevice("earbuds/right", "OPPO Enco Clip2 · 右耳", DeviceBatteryKind.Audio, 80, now, source: "pods", transport: DeviceBatteryTransport.Bluetooth, physicalId: hardwareId + "/right") };
        var workspace = new IslandWorkspaceViewModel();
        workspace.Media.Commit(new() { Title = "测试歌曲", Artist = "测试歌手", SourceApp = "Spotify.exe", State = PlaybackState.Playing });
        var data = new IslandInformationState {
            DeviceBatteries = samples, Resources = new(12, 34, 8, 4, 12, "", ""), Volume = 35, Muted = false,
            Network = new(1024, 512), DailyTraffic = new(DateOnly.FromDateTime(now.LocalDateTime), 2048, 1024),
            Weather = new(WeatherCity.Beijing, 20, 19, 0, 12, 22, now.UtcDateTime),
            Productivity = new(now.ToString("yyyy-MM-dd"), GameSeconds: 120), ClipboardCount = 2, SensitiveGuard = true,
            ActiveReminders = 1, LauncherCount = 30, Plugins = [new("电量插件", "1.0", true, false)] };
        var baseline = new DesktopContextSnapshot(now, new() { ["camera"] = "Teams", ["foregroundApp"] = "chrome", ["rawSecret"] = "must-not-leak" });
        var state = IslandInformationService.Capture(workspace, data, baseline, ["devices"], now);
        string devices = state.Fields["deviceBatteries"];
        Check(state.Fields.Count == 1 && devices.Contains("78%") && devices.Contains("48%") && devices.Contains("正在充电"), "仅设备分区返回鼠标、键盘和真实充电信息");
        Check(devices.Contains("飞智黑武士 4 Pro") && devices.Contains("未上报电量"), "接收器未报告百分比时明确未知，不编造手柄电量");
        Check(!devices.Contains("已断开耳机") && !devices.Contains("99%") && devices.Contains("读数已过期"), "断连与过期处理和电池卡片一致");
        Check(devices.Contains("电量 0%"), "已上报的零电量不误判为未知");
        Check(devices.Split("OPPO Enco Clip2").Length == 2 && devices.Contains("左耳 70%") && devices.Contains("右耳 80%"), "Windows 与插件耳机合并一次并保留左右耳电量");
        Check(!devices.Contains(hardwareId) && !devices.Contains("private-mouse-id"), "设备信息不暴露硬件标识");
        var disconnected = samples.Select(s => s.Device.Id == "windows-earbuds" ? s with { Device = s.Device with { Connected = false } } : s).ToArray();
        Check(!IslandInformationService.FormatDevices(disconnected, now).Contains("OPPO"), "Windows 确认断开后不会被插件旧耳机读数重新显示");
        var all = IslandInformationService.Capture(workspace, data, baseline, ["all"], now);
        Check(all.Fields.ContainsKey("weather") && all.Fields["weather"].Contains("20℃") && all.Fields["network"].Contains("1 KB/s"), "天气与网速来自现有观测且单位正确");
        Check(all.Fields["media"].Contains("测试歌曲") && all.Fields["audio"].Contains("35%") && all.Fields["resources"].Contains("12 W"), "媒体、音量和资源可读取");
        Check(all.Fields["productivity"].Contains("游戏 2 分钟") && all.Fields["focus"].Contains("未启动"), "今日统计与番茄钟读取已有状态");
        Check(all.Fields["notifications"].Contains("共 0 条") && all.Fields["clipboard"].Contains("2 条") && all.Fields["reminders"].Contains("1 条"), "通知、剪贴板和提醒默认只返回数量及状态");
        Check(all.Fields.ContainsKey("plugins") && all.Fields.ContainsKey("launcher") && all.Fields.ContainsKey("island"), "插件、启动器与岛内能力概况可发现");
        Check(!all.Fields.ContainsKey("rawSecret") && !JsonSerializer.Serialize(all).Contains("must-not-leak"), "未知原始字段不进入 API 数据");
        var relevant = DesktopContextPolicy.Filter(all, "我的鼠标还有多少电？");
        Check(relevant.Fields.ContainsKey("deviceBatteries") && !relevant.Fields.ContainsKey("media") && !relevant.Fields.ContainsKey("camera"), "电量查询不附带无关媒体与摄像头信息");
        var empty = IslandInformationService.Capture(workspace, new(), DesktopContextSnapshot.Empty, ["power", "audio", "network", "weather"], now);
        Check(empty.Fields.Values.All(s => !s.Contains("0%")) && empty.Fields["weather"].Contains("尚无可用"), "未采样的状态明确不可用，不当作零或猜测天气");
        var bounded = DesktopContextPolicy.Select(new(now, new() { ["deviceBatteries"] = new('x', 100000), ["plugins"] = new('y', 100000) }), ["all"]);
        Check(bounded.Fields["deviceBatteries"].Length == 3000 && bounded.Fields.Values.Sum(s => s.Length) <= 12000, "异常长状态和设备列表有独立及总长度上限");
        var estimated = samples[0] with { Device = samples[0].Device with { Percent = 50, Estimated = true } };
        Check(IslandInformationService.FormatDevices([estimated], now).Contains("约 50%"), "估算电量保留约数标记，不向模型冒充精确读数");
        var changed = IslandInformationService.Capture(workspace, data with { DeviceBatteries = [samples[0] with { Device = samples[0].Device with { Percent = 42 } }] }, baseline, ["devices"], now.AddSeconds(1));
        Check(changed.Fields["deviceBatteries"].Contains("42%") && !changed.Fields["deviceBatteries"].Contains("78%"), "状态聚合不持有前次电量缓存");
    }
    private static async Task IslandInformationRoundTripChecks(ChatConfig config)
    {
        var now = DateTimeOffset.Now; int percent = 78;
        var workspace = new IslandWorkspaceViewModel();
        var host = new InformationToolFixture(sections => IslandInformationService.Capture(workspace,
            new() { DeviceBatteries = [InformationDevice("fixture", "MCHOSE K5 Ultra", DeviceBatteryKind.Mouse, percent, now)] }, DesktopContextSnapshot.Empty, sections, now));
        using var registry = new ToolRegistry(() => true);
        foreach (var tool in DesktopTools.Create(host)) registry.Register("island", tool);
        using var handler = new AgentHttpFixture((n, _) => AgentResponse(n % 2 == 1 ? AgentSse(new {
            choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, id = "battery-read", function = new { name = "get_island_info", arguments = "{\"section\":\"devices\"}" } } } }, finish_reason = "tool_calls" } }
        }) : AgentSse(new { choices = new[] { new { delta = new { content = "已读取电量" }, finish_reason = "stop" } } })));
        using var http = new HttpClient(handler); using var service = new ChatService(http, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        await foreach (var _ in service.RunAgentAsync([new("user", "我的鼠标还有多少电？")], config, registry, "same-session")) { }
        using (var request = JsonDocument.Parse(handler.Bodies[1]))
        {
            var result = request.RootElement.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "tool");
            Check(result.GetProperty("content").GetString()!.Contains("78%"), "模型工具调用经注册表读取后，实际下一轮 HTTP 请求收到鼠标电量");
        }
        percent = 42;
        await foreach (var _ in service.RunAgentAsync([new("user", "我的鼠标还有多少电？"), new("assistant", "78%"), new("user", "现在呢？")], config, registry, "same-session")) { }
        using (var request = JsonDocument.Parse(handler.Bodies[3]))
        {
            var result = request.RootElement.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "tool");
            Check(result.GetProperty("content").GetString()!.Contains("42%") && host.Reads == 2, "同一会话重复工具调用 ID 仍在新提问读取新电量");
        }
        int before = host.Reads;
        var invalid = await registry.ExecuteAsync(new("invalid-section", "get_island_info", JsonSerializer.SerializeToElement(new { section = "raw_config" })));
        Check(!invalid.Success && host.Reads == before, "未开放的配置分区在执行前被拒绝");
    }
    private sealed class InformationToolFixture(Func<IReadOnlyList<string>, DesktopContextSnapshot> capture) : IIslandToolHost
    {
        internal int Reads;
        public ValueTask<ToolResult> ExecuteAsync(string command, JsonElement arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads++;
            return ValueTask.FromResult(ToolResult.Ok("岛内状态", capture([arguments.GetProperty("section").GetString()!])));
        }
    }
}
