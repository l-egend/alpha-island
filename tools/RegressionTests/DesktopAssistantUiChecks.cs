using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using System.Windows.Threading;
using AlphaIsland.AgentTools;
using AlphaIsland.AgentTools.IslandTools;
using AlphaIsland.PluginSdk;
using alpha_island.Core;
using alpha_island.Plugins;
using alpha_island.Services;

internal static partial class Program
{
    private static void DesktopAssistantUiChecks(alpha_island.MainWindow window, string root)
    {
        var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        try
        {
            using var keys = new HotkeyService(window.Dispatcher); var writes = new List<string>();
            string history = Path.Combine(root, "guarded-clipboard.json");
            using var clipboard = new ClipboardService(window.Dispatcher, keys, () => 1, () => "", history, writes.Add);
            clipboard.CopyFromHistory("normal-copy"); string secret = "sk-" + new string('z', 30); string preview = "";
            clipboard.ContentCopied += (_, e) => preview = e.Summary; clipboard.CopyFromHistory(secret);
            Check(writes.Last() == secret && clipboard.History.Count == 1 && clipboard.History[0].Text == "normal-copy", "敏感复制保留系统剪贴板原文，同时跳过历史");
            Check(!preview.Contains(secret) && clipboard.LastSensitiveKind.Length > 0 && !File.ReadAllText(history).Contains(secret), "敏感预览和持久化文件均不含原文");
            clipboard.SetSensitiveGuard(false); clipboard.CopyFromHistory(secret); Check(clipboard.History.Count == 2, "用户关闭保护后可保留原有历史行为");
            clipboard.SetSensitiveGuard(true); Check(clipboard.History.Count == 1 && !File.Exists(history + ".bak"), "重新开启保护会清理敏感历史及其备份");
            var registry = AgentField<ToolRegistry>(window, "_desktopTools");
            var timer = AgentField<alpha_island.ViewModels.IslandWorkspaceViewModel>(window, "_workspace").Focus.Timer;
            var start = new ToolRequest("ui-focus", "pomodoro_control", JsonSerializer.SerializeToElement(new { action = "start", minutes = 7 }), "ui");
            var result = AwaitPlugin(registry.ExecuteAsync(start).AsTask());
            Check(result.Success && timer.Running && timer.Remaining.TotalMinutes == 7, "工具通过真实主窗口适配器启动七分钟番茄钟");
            Check(!AwaitPlugin(registry.ExecuteAsync(start with { CallId = "ui-second-start" }).AsTask()).Success, "再次 start 不会覆盖已经开始的专注任务");
            AwaitPlugin(registry.ExecuteAsync(new("ui-pause", "pomodoro_control", JsonSerializer.SerializeToElement(new { action = "pause" }), "ui")).AsTask());
            Check(!timer.Running, "工具暂停结果与卡片使用的同一计时器一致");
            using var settings = new ClipboardSettingsLifetime(); settings.Window.LoadFrom(new IslandOptions().Normalized());
            Check(settings.Window.FindName("MeetingGuardEnabledToggle") is CheckBox && settings.Window.FindName("TimelineRetentionDaysBox") is TextBox, "新增本地功能开关与保留天数实际进入设置页");
            DesktopObservation observation = new(null, "短时音乐", 0, null, null);
            using (var events = new DesktopAssistantService(window.Dispatcher, IslandTicker.Shared(window.Dispatcher), () => observation, _ => true, null))
            {
                events.NotifyActivityChanged(); observation = observation with { Media = null }; events.NotifyActivityChanged();
                Check(events.Entries.Length == 1 && events.Entries[0].Ended != null, "短于轮询间隔的音乐也通过现有事件立即记录起止");
            }
            var options = new IslandOptions(); var fixtureHost = new DesktopToolFixture(); using var tools = new ToolRegistry(() => true);
            foreach (var tool in DesktopTools.Create(fixtureHost)) tools.Register("island", tool);
            string folder = Path.Combine(root, "tool-plugin"); Directory.CreateDirectory(folder);
            File.Copy(Path.GetFullPath($"tools/PluginFixture/bin/{TestBuildConfiguration}/net8.0-windows/AlphaIsland.PluginFixture.dll"), Path.Combine(folder, "AlphaIsland.PluginFixture.dll"));
            File.WriteAllText(Path.Combine(folder, "plugin.json"), JsonSerializer.Serialize(new { id = "fixture.tools", name = "工具夹具", version = "1.0.0", apiVersion = 3, assembly = "AlphaIsland.PluginFixture.dll", entryType = "AlphaIsland.PluginFixture.ToolProbePlugin" }));
            SettingsRegistry.SetExtra(options, "plugin/fixture.tools/enabled", true);
            using var host = new PluginHost(window.Dispatcher, options, [Path.GetDirectoryName(folder)!], _ => { }, _ => { }, _ => { })
            { ToolDispatcher = tools, DataRoot = Path.Combine(root, "tool-data"), ContextReader = (_, _) => ValueTask.FromResult(new DesktopContextSnapshot(DateTimeOffset.UtcNow, new() { ["cpu"] = "88%" })) };
            try
            {
            host.Discover(); var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!host.Entries.Any(e => e.Manifest?.Id == "fixture.tools" && e.Running) && watch.ElapsedMilliseconds < 15000) PumpAnimations(20);
            var entry = host.Entries.Single(e => e.Manifest?.Id == "fixture.tools");
            Check(entry.Running && !AwaitPlugin(tools.GetToolsAsync().AsTask()).Any(t => t.Name == "fixture_tools__echo"), "真实独立插件贡献已注册，默认不向模型开放");
            host.AllowPluginTools = true;
            var echo = AwaitPlugin(tools.ExecuteAsync(new("echo", "fixture_tools__echo", JsonSerializer.SerializeToElement(new { }), "remote")).AsTask());
            Check(echo.Success && echo.Data!.Value.GetProperty("process").GetInt32() != Environment.ProcessId, "贡献工具通过管道在所属插件进程执行");
            Check(host.InvokeAction("probe"), "通过插件动作触发工作进程到宿主的工具调用");
            watch.Restart(); while (!options.Extras.ContainsKey("toolProbeResult") && watch.ElapsedMilliseconds < 10000) PumpAnimations(20);
            Check(options.Extras.TryGetValue("toolProbeResult", out var report) && report.GetString() == $"{DesktopTools.Create(fixtureHost).Count()+1}:88%:True:True" && fixtureHost.Calls == 1, "真实双向 IPC 获取工具、读取状态并去重执行，不互相等待卡死");
            SettingsRegistry.SetExtra(options, "plugin/fixture.tools/enabled", false); host.Sync(options);
            Check(!AwaitPlugin(tools.GetToolsAsync().AsTask()).Any(t => t.Name == "fixture_tools__echo"), "停用真实插件后其工具立即从注册表移除");
            AwaitPlugin(host.ShutdownCompletion.ContinueWith(_ => true));
            }
            finally { host.Dispose(); AwaitPlugin(host.ShutdownCompletion.ContinueWith(_ => true)); }
            DesktopContextUiChecks(window, root);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void DesktopContextUiChecks(alpha_island.MainWindow window, string root)
    {
        var options = new IslandOptions(); SettingsRegistry.SetExtra(options, "AiEnabled", true);
        int reads = 0;
        var context = new ConversationPluginFixture(options, Path.Combine(root, "context-ui"))
        { ContextRequested = prompt => { reads++; return DesktopContextPolicy.Filter(new(DateTimeOffset.UtcNow, new() { ["camera"] = "Teams" }), prompt); } };
        using var view = new AlphaIsland.Plugins.ApiChat.ApiChatView(context);
        var store = new ChatSessionStore(Path.Combine(root, "context-ui", "sessions"));
        SetAgentField(view, "_chatSessions", store);
        SetAgentField(view, "_aiConfigFactory", new Func<alpha_island.Conversations.IChatSettings, ChatConfig>(_ => new("https://example.invalid/v1", "", "fixture", "", .7, 1024)));
        using var http = new AgentHttpFixture((_, _) => AgentResponse(AgentSse(new { choices = new[] { new { delta = new { content = "已收到" }, finish_reason = "stop" } } })));
        using var client = new System.Net.Http.HttpClient(http); using var chat = new ChatService(client, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        SetAgentField(view, "_ai", chat);
        void Send(string text) => AwaitPlugin(((Task)PrivateCall(view, "SendAiAsync", text)!).ContinueWith(_ => true));
        Send("帮我写首诗"); Check(http.Bodies.Count == 1 && !http.Bodies[0].Contains("Teams") && reads == 0, "写诗不会读取或附加系统状态");
        Send("我在开会，摄像头情况如何"); Send("会议中还有什么建议");
        Check(reads == 0 && http.Bodies.Count == 3 && store.ReadDesktopContext("default").Length == 0, "未发出状态工具调用时，相关提问也不自动采集或保存状态包");
        using (var body = JsonDocument.Parse(http.Bodies[2]))
            Check(!body.RootElement.GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("content").GetString()!.StartsWith("[本会话首次相关提问时的系统快照", StringComparison.Ordinal)), "后续请求不自动重发状态包");
        store.WriteDesktopContext("default", JsonSerializer.Serialize(new DesktopContextSnapshot(DateTimeOffset.UtcNow, new() { ["camera"] = "Teams" })));
        Send("会议中有哪些建议"); Check(!http.Bodies[^1].Contains("Teams") && reads == 0, "升级前保存的旧会话状态包也不会附加到请求");
        Send("帮我写首诗"); Check(!http.Bodies[^1].Contains("Teams") && reads == 0, "已有旧状态包时写诗仍不发送状态数据");
        SettingsRegistry.SetExtra(options, "AiDesktopContextEnabled", false); Send("会议还有什么需要注意");
        Check(!http.Bodies[^1].Contains("Teams") && reads == 0, "关闭状态开关后不发送既有状态数据");
        string secret = "sk-" + new string('q', 28); int before = http.Bodies.Count;
        SetAgentField(view, "_confirmSensitiveSend", new Func<SensitiveTextReport, bool>(_ => false)); Send(secret);
        Check(http.Bodies.Count == before && !store.Load().Any(m => m.Content.Contains(secret)), "用户拒绝敏感发送时不调用接口也不写入历史");
        SetAgentField(view, "_confirmSensitiveSend", new Func<SensitiveTextReport, bool>(_ => true)); Send(secret);
        Check(http.Bodies[^1].Contains(secret) && !File.ReadAllText(Path.Combine(store.DirectoryPath, "default.json")).Contains(secret), "用户允许仅本次发送原文，磁盘仅保存脱敏版本");
        Send("继续说明"); Check(!http.Bodies[^1].Contains(secret), "下一轮历史不会再次把上轮敏感原文发送到接口");
        var service = AgentField<DesktopAssistantService>(window, "_desktopAssistant");
        // Keep all fixture entries on the selected local day, including midnight runs.
        var ledger = AgentField<TimelineLedger>(service, "_ledger");
        var current = DateTimeOffset.Now;
        var now = new DateTimeOffset(current.Date, current.Offset);
        for (int i = 0; i < 40; i++) ledger.Observe(new() { ["music"] = ("music", "示例曲目 " + i) }, now.AddSeconds(i), 1000 + i * 1000);
        var timeline = new alpha_island.Controls.TimelineView(service, CancellationToken.None);
        SaveConversationPreview(new System.Windows.Controls.Border { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 24, 30)), Child = timeline }, "docs/screens/v115-timeline.png", 414, 400);
        var list = AgentField<System.Windows.Controls.ListBox>(timeline, "_list");
        Check(list.Items.Count == 40 && VirtualizingPanel.GetIsVirtualizing(list), "时间轴使用可回收虚拟列表显示实际事件");
        var first = list.Items[0]; PrivateCall(timeline, "Refresh");
        Check(ReferenceEquals(first, list.Items[0]), "定时刷新保留已有行对象，不重建列表导致滚动跳动");
        using var settings = new AlphaIsland.Plugins.ApiChat.ApiSettingsView(context: context);
        SaveConversationPreview(settings, "docs/screens/v115-api-settings.png", 650, 840);
        Check(AgentField<Dictionary<string, CheckBox>>(settings, "_assistantOptions").Count == 3, "API 插件包含工具、上下文和敏感发送三个独立开关");
    }
}
