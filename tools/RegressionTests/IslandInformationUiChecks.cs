using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using AlphaIsland.AgentTools;
using AlphaIsland.PluginSdk;
using alpha_island.Core;
using alpha_island.Plugins;
using alpha_island.Services;
using alpha_island.ViewModels;

internal static partial class Program
{
    private static void IslandInformationUiChecks(alpha_island.MainWindow window, string root)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        var originalHost = ReadWindowMember(window, "_plugins");
        var options = AgentField<IslandOptions>(window, "_options");
        try
        {
            using var host = new PluginHost(window.Dispatcher, options, [], _ => { }, _ => { }, _ => { });
            var entry = new PluginHost.Entry { Directory = root,
                Manifest = new() { Id = "fixture.information", Name = "设备状态测试", Version = "1.0" },
                Instance = new InformationPluginFixture(), Active = true };
            entry.Context = new(host, entry);
            AgentField<List<PluginHost.Entry>>(host, "_entries").Add(entry);
            WriteWindowMember(window, "_plugins", host);
            var registry = AgentField<ToolRegistry>(window, "_desktopTools");
            var workspace = AgentField<IslandWorkspaceViewModel>(window, "_workspace");
            void Report(int percent, bool connected = true) => entry.Context.ReportDeviceBatteries([
                new("fixture-mouse", "MCHOSE K5 Ultra", DeviceBatteryTransport.WirelessReceiver, DeviceBatteryKind.Mouse, percent, connected)]);
            ToolResult Read(string id) => AwaitPlugin(registry.ExecuteAsync(new(id, "get_island_info", JsonSerializer.SerializeToElement(new { section = "devices" }), "host-information")).AsTask());
            Report(78);
            var result = Read("host-78");
            string Info(ToolResult value) => value.Data!.Value.Deserialize<DesktopContextSnapshot>()!.Fields["deviceBatteries"];
            Check(result.Success && Info(result).Contains("78%"), "真实主窗口工具适配器读取运行插件报告的鼠标电量");
            var legacy = AwaitPlugin(registry.ExecuteAsync(new("legacy-minimal", "get_desktop_state", JsonSerializer.SerializeToElement(new { }), "host-information")).AsTask());
            Check(legacy.Success && !legacy.Data!.Value.Deserialize<DesktopContextSnapshot>()!.Fields.ContainsKey("deviceBatteries"), "旧状态端点保留原有最小载荷，兼容已有插件客户端");
            var card = new alpha_island.Controls.DeviceBatteryCard();
            card.UpdateGroups(DeviceBatteryCatalog.Group(host.DeviceBatteries, DateTimeOffset.Now));
            Check(Descendants<TextBlock>(AgentField<StackPanel>(card, "_rows")).Any(t => t.Text.Contains("78%")), "同一插件报告在实际电池卡片中同样显示 78%");
            Report(42); Check(Info(Read("host-42")).Contains("42%"), "主窗口不会缓存旧工具读数，后一次查询得到 42%");
            Report(42, false); Check(!Info(Read("host-disconnected")).Contains("K5 Ultra"), "插件上报断开后实际宿主不再返回旧设备电量");
            Report(78);
            var pluginOptions = new IslandOptions(); SettingsRegistry.SetExtra(pluginOptions, "AiEnabled", true);
            int automaticReads = 0;
            var context = new ConversationPluginFixture(pluginOptions, Path.Combine(root, "island-information")) {
                ToolDispatcher = registry,
                ContextRequested = prompt => { automaticReads++; return AwaitPlugin(((ValueTask<DesktopContextSnapshot>)PrivateCall(window, "ReadDesktopContext", prompt, CancellationToken.None)!).AsTask()); }
            };
            using var view = new AlphaIsland.Plugins.ApiChat.ApiChatView(context);
            var store = new ChatSessionStore(Path.Combine(root, "island-information", "sessions"));
            SetAgentField(view, "_chatSessions", store);
            SetAgentField(view, "_aiConfigFactory", new Func<alpha_island.Conversations.IChatSettings, ChatConfig>(_ => new("http://127.0.0.1/v1", TestApiKey, "fixture", "", .7, 1024)));
            using var handler = new AgentHttpFixture((n, _) => AgentResponse(n % 2 == 1 ? AgentSse(new {
                choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, id = "ui-battery", function = new { name = "get_island_info", arguments = "{\"section\":\"devices\"}" } } } }, finish_reason = "tool_calls" } }
            }) : AgentSse(new { choices = new[] { new { delta = new { content = "已读取设备电量" }, finish_reason = "stop" } } })));
            using var http = new HttpClient(handler); using var chat = new ChatService(http, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
            SetAgentField(view, "_ai", chat);
            void Send(string prompt) => AwaitPlugin(((Task)PrivateCall(view, "SendAiAsync", prompt)!).ContinueWith(_ => true));
            Send("我的鼠标还有多少电？");
            Check(handler.Bodies.Count == 2 && handler.Bodies[1].Contains("78%"), "真实 API 对话发送入口经主窗口状态工具把鼠标电量返回模型");
            Report(42); Send("现在呢？");
            using (var body = JsonDocument.Parse(handler.Bodies[^1]))
            {
                var data = body.RootElement.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "tool").GetProperty("content").GetString()!;
                Check(data.Contains("42%") && !data.Contains("78%"), "真实 API 对话同会话追问以本次工具结果为准");
            }
            var observed = AgentField<List<string>>(view, "_aiStateReads");
            Check(observed.Count == 1 && observed[0].Contains("42%") && !observed[0].Contains("78%"), "状态查看只保留最近一轮实际工具读取，不跨轮累积或重新采集");
            Check(automaticReads == 0 && store.ReadDesktopContext("default").Length == 0 && !handler.Bodies[0].Contains("78%"), "仅工具调用结果提供电量，首轮不自动读取或保存状态包");
            store.WriteDesktopContext("default", JsonSerializer.Serialize(new DesktopContextSnapshot(DateTimeOffset.Now, new() { ["media"] = "升级前的旧快照" })));
            Report(35); Send("我的鼠标现在还有多少电？");
            using (var body = JsonDocument.Parse(handler.Bodies[^1]))
            {
                var data = body.RootElement.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "tool").GetProperty("content").GetString()!;
                Check(data.Contains("35%") && automaticReads == 0, "升级前旧快照不会阻止按需查询，且不会触发自动状态读取");
            }
            SettingsRegistry.SetExtra(options, "AiDesktopContextEnabled", false);
            Check(!Read("host-disabled").Success, "真实宿主也校验系统状态总开关，无法绕过插件权限读取");
            SettingsRegistry.SetExtra(options, "AiDesktopContextEnabled", true);
            WriteWindowMember(window, "_sessionLocked", true);
            var locked = AwaitPlugin(((ValueTask<DesktopContextSnapshot>)PrivateCall(window, "ReadDesktopContext", "鼠标电量", CancellationToken.None)!).AsTask());
            Check(locked.Fields.Count == 0, "锁屏期间自动上下文不返回设备信息");
        }
        finally
        {
            WriteWindowMember(window, "_sessionLocked", false);
            SettingsRegistry.SetExtra(options, "AiDesktopContextEnabled", true);
            WriteWindowMember(window, "_plugins", originalHost);
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
    private sealed class InformationPluginFixture : IIslandPlugin
    {
        public void Initialize(IPluginContext context) { }
        public void Dispose() { }
    }
}
