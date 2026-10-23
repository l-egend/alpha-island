using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void AiConversationStoreChecks(string root)
    {
        var store = new ChatSessionStore(Path.Combine(root, "conversations"));
        Check(store.List().Count == 0, "无对话时历史管理正常显示空列表");
        Directory.CreateDirectory(store.DirectoryPath);
        var messages = Enumerable.Range(0, 62).Select(i => new ChatMessage(i % 2 == 0 ? "user" : "assistant", "历史内容 " + i)).ToList();
        File.WriteAllText(Path.Combine(store.DirectoryPath, "default.json"), JsonSerializer.Serialize(new { Session = "default", Updated = "2026-10-07 18:00:00", Messages = messages }));
        Check(store.Load().SequenceEqual(messages) && store.List()[0].MessageCount == 62, "兼容旧会话文件，完整历史不再被截断到 40 条");
        string fresh = store.Create();
        Check(store.List().Count == 2 && store.Load(fresh).Count == 0 && store.Load().Count == 62, "新建独立会话保留原始记录");
        store.Save(fresh, new[] { new ChatMessage("user", "新问题"), new ChatMessage("assistant", "新回复") });
        store.Rename(fresh, "测试对话"); store.Save(fresh, store.Load(fresh));
        Check(store.List().First(s => s.Id == fresh).Title == "测试对话" && store.List()[0].Id == fresh, "改名在继续保存后保留，按更新时间排列会话");
        store.EditMessage(fresh, 0, "修改后的问题");
        Check(store.Load(fresh)[0] == new ChatMessage("user", "修改后的问题") && store.Load(fresh)[1].Content == "新回复", "编辑消息保留角色和其他内容");
        string path = Path.Combine(store.DirectoryPath, fresh + ".json");
        Check(!File.Exists(path + ".bak"), "编辑清除旧备份，不会恢复已修改的旧内容");
        store.Save(fresh, store.Load(fresh)); store.DeleteMessage(fresh, 1);
        Check(store.Load(fresh).Count == 1 && store.Load(fresh)[0].Content == "修改后的问题" && !File.Exists(path + ".bak"), "删除单条消息及包含旧内容的备份");
        bool rejected = false;
        try { store.Save("../outside", messages); } catch (ArgumentException) { rejected = true; }
        Check(rejected && !File.Exists(Path.Combine(root, "outside.json")), "对话标识不能越出历史目录");
        rejected = false;
        try { store.EditMessage(fresh, 0, "  "); } catch (ArgumentException) { rejected = true; }
        Check(rejected && store.Load(fresh)[0].Content == "修改后的问题", "无效编辑不覆盖原始内容");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            rejected = false;
            try { store.EditMessage(fresh, 0, "不应写入"); } catch (IOException) { rejected = true; }
            Check(rejected, "文件被占用时修改明确失败");
        }
        Check(store.Load(fresh)[0].Content == "修改后的问题", "修改失败后原始记录完整");
        store.Save(fresh, store.Load(fresh));
        using (var locked = new FileStream(path + ".bak", FileMode.Open, FileAccess.Read, FileShare.None))
        {
            rejected = false;
            try { store.EditMessage(fresh, 0, "不应写入"); } catch (IOException) { rejected = true; }
            Check(rejected && store.Load(fresh)[0].Content == "修改后的问题", "无法清除旧备份时拒绝编辑并保留主文件");
        }
        File.WriteAllText(path, "corrupt");
        Check(store.Load(fresh)[0].Content == "修改后的问题", "主文件损坏时从有效备份恢复历史");
        File.WriteAllText(Path.Combine(store.DirectoryPath, "damaged.json"), "broken");
        Check(store.List().Count == 3 && store.List().Any(s => s.Id == "damaged") && store.Load().Count == 62, "损坏会话不阻止其他历史查看，自身仍可选择删除");
        store.Delete(fresh); store.Delete("damaged");
        Check(!store.Exists(fresh) && store.List().Count == 1 && store.Load().Count == 62, "删除会话及备份，其他会话不受影响");

        var options = new IslandOptions(); string keyPath = Path.Combine(root, "state-key.dat");
        var config = new ChatConfig("https://example.invalid/v1", TestApiKey, "test-model", "", .7, 1024);
        SettingsRegistry.Write(options, "AiBaseUrl", config.BaseUrl); SettingsRegistry.Write(options, "AiModel", config.Model);
        Check(!AiConnectionState.Update(options, keyPath), "没有密钥时自动关闭对话");
        AiSecretStore.TrySave(keyPath, TestApiKey);
        Check(!AiConnectionState.Update(options, keyPath), "仅有密钥未验证连接时自动关闭对话");
        Check(AiConnectionState.Verify(options, keyPath, config) && AiConnectionState.Update(options, keyPath), "成功连接凭证绑定当前接口与加密密钥文件");
        string configJson = JsonSerializer.Serialize(options, IslandOptions.JsonOptions);
        Check(!configJson.Contains(TestApiKey) && !configJson.Contains(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(TestApiKey)))), "连接证明不保存明文密钥或明文密钥哈希");
        var restored = JsonSerializer.Deserialize<IslandOptions>(configJson, IslandOptions.JsonOptions)!;
        Check(AiConnectionState.Update(restored, keyPath), "重启恢复配置后已验证的接口仍可启用");
        SettingsRegistry.Write(options, "AiBaseUrl", "https://another.invalid/v1");
        Check(!AiConnectionState.Update(options, keyPath), "更换接口后自动关闭对话");
        SettingsRegistry.Write(options, "AiBaseUrl", config.BaseUrl); AiSecretStore.TrySave(keyPath, "replacement-test-key");
        Check(!AiConnectionState.Update(options, keyPath), "更换密钥后不能复用之前的连接证明");
        AiConnectionState.Verify(options, keyPath, config); AiConnectionState.Invalidate(options);
        Check(!AiConnectionState.Update(options, keyPath), "连接失败后失效证明，不自动恢复旧开启状态");
    }

    private static void AiConversationUiChecks(alpha_island.MainWindow main, Grid root, string testRoot)
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var options = AgentField<IslandOptions>(main, "_options");
        var pages = AgentField<IslandPageHost>(main, "_pages");
        var context = new ConversationPluginFixture(options, Path.Combine(testRoot,"api-plugin"));
        var window = new AlphaIsland.Plugins.ApiChat.ApiChatView(context);
        const string pageId = "plugin:ai.api-chat/chat";
        var page = new alpha_island.Plugins.PluginIslandPage("ai.api-chat",new AlphaIsland.PluginSdk.PluginPage("chat","AI 对话",()=>window,Height:314)
            {Available=()=>SettingsRegistry.GetBool(options,"AiEnabled",false)},e=>throw e);
        main.CardViewport.Children.Add(page.View);pages.Register(page);
        context.PageRequested = _ => { if(pages.IndexOf(pageId)>=0) PrivateCall(main,"SelectComponent",pageId); };
        context.RefreshRequested = () => { pages.Sync(options); window.OptionsChanged(); };
        var store = new ChatSessionStore(Path.Combine(testRoot, "ui-conversations"));
        store.Save("default", Enumerable.Range(0, 62).Select(i => new ChatMessage(i % 2 == 0 ? "user" : "assistant", "测试消息 " + i)).ToArray());
        SetAgentField(window, "_chatSessions", store); SetAgentField(window, "_aiLoaded", false);
        alpha_island.Controls.ChatHistoryWindow? history = null;
        var config = new ChatConfig("https://example.invalid/v1", TestApiKey, "test-model", "", .7, 1024);
        SetAgentField(window, "_aiConfigFactory", new Func<alpha_island.Conversations.IChatSettings, ChatConfig>(_ => config));
        void Complete(Task task)
        {
            var limit = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && limit.ElapsedMilliseconds < 5000) PumpAnimations(10);
            if (!task.IsCompleted) throw new TimeoutException("AI UI test timeout");
            task.GetAwaiter().GetResult();
        }
        static void Click(System.Windows.Controls.Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        try
        {
            SettingsRegistry.Write(options, "AiEnabled", false); pages.Sync(options);
            Check(pages.IndexOf(pageId) < 0, "AI 关闭时岛内对话入口隐藏，不再显示未连接的对话卡片");
            PrivateCall(window, "OpenAiConversation"); root.UpdateLayout();
            Check(pages.ActiveId != pageId, "关闭时直接调用对话动作也不能打开卡片");
            Check(!SettingsRegistry.GetBool(options,"AiEnabled",false), "打开入口不会绕过连接状态");
            PrivateCall(window, "SyncAiCardContent");
            Check(AgentField<List<ChatMessage>>(window, "_aiMessages").Count == 62 && window.AiBubbleStack.Children.Count == 40, "保留全部历史，但岛内只绘制最近气泡");
            window.AiInputBox.Text = "问题";
            Check(!window.AiSendButton.IsEnabled, "未连接时不能发送消息");
            SettingsRegistry.Write(options, "AiEnabled", true); pages.Sync(options); PrivateCall(window, "OpenAiConversation"); PrivateCall(window, "OnAiOptionsChanged");
            Check(window.AiSendButton.IsEnabled, "连接成功状态同步后发送按钮可用");
            Click(window.AiNewButton); string fresh = AgentField<string>(window, "_aiSessionId");
            Check(fresh != "default" && store.Load().Count == 62 && AgentField<List<ChatMessage>>(window, "_aiMessages").Count == 0, "新建按钮切入空会话并保留旧历史");
            Check(SettingsRegistry.GetText(options, "AiActiveSession", "") == fresh, "当前会话标识写入配置以便重启继续");
            PrivateCall(window, "ActivateAiConversation", "default");
            history = (alpha_island.Controls.ChatHistoryWindow)PrivateCall(window, "CreateAiHistoryWindow")!;
            SetAgentField(window, "_chatHistoryWindow", history);
            Check(history.SessionList.Items.Count == 2 && history.MessageList.Items.Count == 62 && history.MessageEditor.Text == "测试消息 0", "历史界面列出会话、完整消息和所选消息全文");
            history.TitleBox.Text = "工作讨论"; Click(history.RenameButton);
            Check(store.List().First(s => s.Id == "default").Title == "工作讨论", "历史界面可修改会话名称");
            history.MessageEditor.Text = "已修改的提问"; Click(history.SaveMessageButton);
            Check(store.Load()[0].Content == "已修改的提问" && AgentField<List<ChatMessage>>(window, "_aiMessages")[0].Content == "已修改的提问", "编辑后同步当前岛内会话");
            Click(history.DeleteMessageButton);
            Check(store.Load().Count == 61 && AgentField<List<ChatMessage>>(window, "_aiMessages").Count == 61, "删除消息后岛和本地文件同步");
            string dataPath = Path.Combine(store.DirectoryPath, "default.json");
            using (var locked = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                history.MessageEditor.Text = "不应写入"; Click(history.SaveMessageButton);
                Check(history.StatusText.Text.Contains("操作失败"), "历史保存失败明确提示，不误报成功");
                Click(window.AiClearButton);
                Check(AgentField<List<ChatMessage>>(window, "_aiMessages").Count == 61 && window.AiStatusText.Text.Contains("清空失败"), "清空失败时保留岛内记录");
            }
            SetAgentField(window, "_aiStreaming", true); history.Reload("default");
            Check(history.MessageEditor.IsReadOnly && !history.SaveMessageButton.IsEnabled && !history.DeleteSessionButton.IsEnabled, "生成期间允许查看，禁止修改、删除与切换会话");
            PrivateCall(history, "SaveMessage_Click", history.SaveMessageButton, new RoutedEventArgs());
            Check(history.StatusText.Text.Contains("正在生成") && store.Load().Count == 61, "已打开的管理窗口再次检查生成状态，避免覆盖返回中的回复");
            SetAgentField(window, "_aiStreaming", false); history.Reload("default");
            history.MessageList.SelectedIndex = history.MessageList.Items.Count - 1;
            history.MessageEditor.Text = "发送前修改的最后回复"; Click(history.SaveMessageButton);
            var request = (List<ChatMessage>)PrivateCall(window, "BuildAiRequest", config)!;
            Check(request.Count == 20 && request[^1].Content == "发送前修改的最后回复" && !request.Any(m => m.Content == "已修改的提问"), "请求使用修改后的最近 20 条上下文，保留历史不增加请求长度");
            history.ShowActivated = false; history.ShowInTaskbar = false; history.Topmost = false;
            history.WindowStartupLocation = WindowStartupLocation.Manual; history.Left = history.Top = -32000;
            history.Show();
            SaveConversationPreview((FrameworkElement)history.Content, "docs/screens/ai-conversation-history.png", 860, 570);
            history.Hide();
            PrivateCall(main, "SelectComponent", pageId); root.UpdateLayout();
            SaveConversationPreview(main.IslandBody, "docs/screens/ai-conversation-card.png", (int)main.IslandBody.ActualWidth, (int)main.IslandBody.ActualHeight);
            Check(window.AiHistoryButton.ActualWidth > 60 && window.AiNewButton.ActualWidth > 60 && window.AiInputBorder.ActualHeight > 25, "历史、新建与输入区均具有可见布局空间");
            string? sentBody = null;
            using var http = new HttpClient(new AiTestHandler(async (req, _) =>
            {
                sentBody = await req.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"模拟回复成功\"}}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
            }));
            using var service = new ChatService(http, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            SetAgentField(window, "_ai", service);
            Complete((Task)PrivateCall(window, "SendAiAsync", "从入口发送测试问题")!);
            Check(store.Load()[^1].Content == "模拟回复成功" && store.Load()[^2].Content == "从入口发送测试问题" && !AgentField<bool>(window, "_aiStreaming"), "岛内发送、流式回复、结束状态与落盘完成整个对话流程");
            using var sent = JsonDocument.Parse(sentBody!);
            Check(sent.RootElement.GetProperty("messages").EnumerateArray().Count(m => m.GetProperty("role").GetString() is "user" or "assistant") == 20 && !sent.RootElement.GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("content").GetString() == "测试消息 1"), "实际请求只携带有限上下文，不发送已删除或很旧的内容");
            Check(!sentBody!.Contains(TestApiKey) && !IslandLog.DumpText().Contains(TestApiKey), "测试密钥不进入消息正文或日志");
            history.SessionList.SelectedItem = history.SessionList.Items.Cast<ChatSessionSummary>().First(s => s.Id == fresh);
            Click(history.ContinueButton);
            Check(AgentField<string>(window, "_aiSessionId") == fresh && pages.ActiveId == pageId, "继续对话切回所选会话的岛内入口");
            history = null; Click(window.AiClearButton);
            Check(store.Load(fresh).Count == 0 && store.Load().Count == 63, "清空当前会话保留其他会话");
            using (var blockedHttp = new HttpClient(new AiTestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StreamContent(new AiBlockingStream("data: {\"choices\":[{\"delta\":{\"content\":\"旧回复片段\"}}]}\n\n")) }))))
            using (var blockedService = new ChatService(blockedHttp, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15)))
            {
                SetAgentField(window, "_ai", blockedService);
                Task pending = (Task)PrivateCall(window, "SendAiAsync", "即将清空的问题")!;
                PumpAnimations(50);
                Check(AgentField<bool>(window, "_aiStreaming") && !window.AiNewButton.IsEnabled, "真实未完成的流式请求期间禁用新建会话");
                Click(window.AiClearButton); Click(window.AiNewButton);
                string next = AgentField<string>(window, "_aiSessionId"); Complete(pending);
                Check(next != fresh && store.Load(fresh).Count == 0 && store.Load(next).Count == 0
                    && AgentField<List<ChatMessage>>(window, "_aiMessages").Count == 0, "清空并新建后迟到的旧流式片段不会恢复记录或写入新会话");
            }
            using (var failedHttp = new HttpClient(new AiTestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                { Content = new StringContent("invalid test key") }))))
            using (var failedService = new ChatService(failedHttp, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)))
            {
                SetAgentField(window, "_ai", failedService);
                Complete((Task)PrivateCall(window, "SendAiAsync", "失败请求")!);
                Check(!SettingsRegistry.GetBool(options, "AiEnabled", false) && !window.AiSendButton.IsEnabled
                    && window.AiEnableButton.Visibility == Visibility.Visible, "实际鉴权失败后关闭发送并从岛内移除对话页");
            }
            var settings = new AlphaIsland.Plugins.ApiChat.ApiSettingsView(false, Path.Combine(testRoot, "isolated-key.dat"));
            try
            {
                settings.LoadFrom(new IslandOptions()); bool opened = false, managed = false;
                settings.AiConversationRequested += (_, _) => opened = true; settings.AiHistoryRequested += (_, _) => managed = true;
                Click(settings.OpenAiConversationButton); Click(settings.ManageAiHistoryButton);
                Check(!opened && managed && settings.AiEnabledToggle.IsChecked == false && !settings.AiEnabledToggle.IsEnabled, "关闭时隐藏打开按钮，管理历史仍可用，不能手动绕过自动连接开关");
            }
            finally { settings.Dispose(); }
        }
        finally { SetAgentField(window, "_aiStreaming", false); history?.Close(); window.Dispose(); SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static void SaveConversationPreview(FrameworkElement view, string path, int width, int height)
    {
        view.Measure(new System.Windows.Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        string file = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var stream = File.Create(file); png.Save(stream);
    }
}
