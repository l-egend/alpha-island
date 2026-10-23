using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Controls;
using alpha_island.Interop;
using alpha_island.Services;
using AlphaIsland.Plugins.ApiChat;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

internal static partial class Program
{
    private static void InteractionFeedbackChecks(alpha_island.MainWindow window, Grid root, string testRoot)
    {
        const string previews = "bin/InteractionFeedback152/previews";
        Directory.CreateDirectory(previews);
        MotionPolicy.OverrideRenderTierForDebug(2);
        MotionPolicy.Apply(true, false);
        PrivateCall(window, "SetState", IslandState.Card, true, "screen-tools");
        PumpAnimations(350); root.UpdateLayout();
        var card = AgentField<Grid>(window, "_screenToolsCard");
        var grid = card.Children.OfType<Grid>().Single();
        var buttons = grid.Children.OfType<ToolActionButton>().ToArray();
        Check(buttons.Length == 8 && buttons.All(b => b.ActualWidth > 150), "屏幕工具八个入口按两列等宽排列，充分利用卡片宽度");
        Check(buttons.Select(b => Math.Round(b.ActualWidth, 1)).Distinct().Count() == 1, "主工具和辅助操作列宽保持一致");
        var footer = AgentField<TextBlock>(window, "_externalTranslationHint");
        var footerOrigin = footer.TranslatePoint(new System.Windows.Point(), card);
        Check(footerOrigin.Y + footer.ActualHeight <= card.ActualHeight + 1, "说明文字完整位于卡片范围内");
        Check(buttons.All(b => !string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(b))), "所有工具按钮保留键盘和辅助功能名称");
        SaveThemePreview(window.IslandBody, previews + "/screen-tools.png");

        string path = Path.Combine(testRoot, "feedback-clipboard.json");
        File.WriteAllText(path, JsonSerializer.Serialize(Enumerable.Range(1, 3).Select(i => new ClipboardEntry("待整理的剪贴板记录 " + i, DateTime.Now.AddMinutes(-i)))));
        uint sequence = 1; string payload = "";
        using var hotkeys = new HotkeyService(Dispatcher.CurrentDispatcher);
        using var clipboard = new ClipboardService(Dispatcher.CurrentDispatcher, hotkeys, () => sequence, () => payload, path, text => { payload = text; sequence++; });
        PrivateCall(clipboard, "LoadHistory"); SetAgentField(window, "_clipboard", clipboard);
        PrivateCall(window, "SetState", IslandState.Card, true, "clipboard");
        PumpAnimations(350); root.UpdateLayout();
        PrivateCall(window, "SyncClipboardHistoryList");
        var items = window.ClipboardHistoryList.ItemsSource;
        PrivateCall(window, "SyncClipboardHistoryList");
        Check(ReferenceEquals(items, window.ClipboardHistoryList.ItemsSource), "无数据变化的刷新保持原列表，避免打断交互动画");
        MotionPolicy.Apply(false, false);
        var first = window.ClipboardHistoryList.Items[0];
        PrivateCall(window, "DeleteClipboardItem_Click", new Button { DataContext = first }, new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(clipboard.History.Count == 2 && window.ClipboardHistoryList.Items.Count == 3, "删除立即保存数据，旧行短暂保留用于淡出收拢");
        Check(AgentField<bool>(window, "_clipboardRemovalInProgress"), "删除使用实际卡片动画路径");
        PumpAnimations(400);
        Check(window.ClipboardHistoryList.Items.Count == 2 && window.ClipboardHistoryList.IsHitTestVisible, "删除动画完成后剩余记录补位并恢复交互");
        PrivateCall(window, "ClearClipboardButton_Click", window.ClearClipboardButton, new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(clipboard.History.Count == 0, "清空按钮立即清除历史并持久化");
        clipboard.CopyFromHistory("清除过程中刚复制的新内容");
        PrivateCall(window, "SyncClipboardHistoryList");
        PumpAnimations(400);
        Check(clipboard.History.Count == 1 && window.ClipboardHistoryList.Items.Count == 1, "清除动画期间的新复制内容保留并显示，不会被延迟清空误删");
        PrivateCall(window, "ExecuteClipboardItemCopy", "清除过程中刚复制的新内容");
        PrivateCall(window, "SyncClipboardHistoryList");
        Check(window.ClipboardHistoryHint.Text.Contains("已复制"), "后台同步不会立即覆盖复制成功提示");
        MotionPolicy.Apply(true, false);
        PrivateCall(window, "ClearClipboardButton_Click", window.ClearClipboardButton, new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(!AgentField<bool>(window, "_clipboardRemovalInProgress") && window.ClipboardEmptyState.Visibility == Visibility.Visible && !window.ClearClipboardButton.IsEnabled,
            "削减动效时立即清空并显示空态，空列表的清空按钮不可重复操作");
        SetAgentField(window, "_clipboard", null);
        ApiWaitingFeedbackChecks(testRoot, previews);
    }

    private static void ApiWaitingFeedbackChecks(string root, string previews)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            var options = new IslandOptions();
            SettingsRegistry.Write(options, "AiEnabled", true);
            SettingsRegistry.Write(options, "AiBaseUrl", "http://127.0.0.1:19731/v1");
            SettingsRegistry.Write(options, "AiModel", "test-model");
            SettingsRegistry.Write(options, "AiDesktopContextEnabled", false);
            var context = new ConversationPluginFixture(options, Path.Combine(root, "waiting-feedback"));
            Check(AiSecretStore.TrySave(Path.Combine(context.ConfigDirectory, "ai", "secrets.dat"), TestApiKey), "等待状态使用独立测试凭据，不访问真实接口");
            using var view = new ApiChatView(context);
            var gates = new List<TaskCompletionSource<HttpResponseMessage>>();
            using var http = new HttpClient(new AiTestHandler((_, token) =>
            {
                var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                gates.Add(gate); return gate.Task.WaitAsync(token);
            }));
            using var service = new ChatService(http, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(20));
            SetAgentField(view, "_ai", service);
            using var source = new HwndSource(new HwndSourceParameters("waiting feedback test")
            { Width = 414, Height = 314, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000) });
            source.RootVisual = view; view.UpdateLayout(); PumpAnimations(50);
            void Send(string prompt)
            {
                int count = gates.Count;
                view.AiInputBox.Text = prompt; view.AiSendButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                PumpUntilState(() => gates.Count > count, 2000);
                Check(AgentField<TextBlock>(view, "_aiLiveBubble").Text == "正在等待回复", "等待网络时立即显示非空回复气泡");
            }
            HttpResponseMessage Reply(string text) => new(HttpStatusCode.OK) { Content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"content\":\"\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":" + JsonSerializer.Serialize(text) + "}}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
            Send("请简短回复测试消息"); PumpAnimations(220);
            Check(AgentField<bool>(view, "_aiWaitingAnimating") == SystemParameters.ClientAreaAnimation, "等待脉冲遵循系统动画偏好");
            SaveThemePreview(view, previews + "/api-waiting.png");
            var surface = new alpha_island.Plugins.Isolation.PluginWorkerSurface(view);
            var frame = surface.Capture(414, 314);
            Check(frame.Elements.Any(e => e.Text == "正在等待回复"), "等待文字经过实际插件图像与控件协议传递给主岛");
            var remote = new alpha_island.Plugins.Isolation.RemotePluginView("chat", _ => Task.CompletedTask, error => throw error);
            remote.Apply(frame); remote.Measure(new System.Windows.Size(414, 314)); remote.Arrange(new Rect(0, 0, 414, 314)); remote.UpdateLayout();
            SaveThemePreview(remote, previews + "/api-waiting-host.png"); remote.Close();
            var messages = AgentField<List<ChatMessage>>(view, "_aiMessages");
            Check(messages.Count == 1 && messages.All(m => !m.Content.Contains("正在等待回复")), "等待文案只用于界面，不写入对话历史或发送上下文");
            view.Visibility = Visibility.Collapsed; PumpAnimations(25);
            Check(!AgentField<bool>(view, "_aiWaitingAnimating"), "隐藏卡片时停止等待动画");
            view.Visibility = Visibility.Visible;
            SettingsRegistry.Write(options, "ReduceEffectsEnabled", true); view.OptionsChanged();
            Check(!AgentField<bool>(view, "_aiWaitingAnimating") && AgentField<TextBlock>(view, "_aiLiveBubble").Text.Length > 0, "削减动效保留静态等待文字");
            gates[0].SetResult(Reply("收到，已为你准备好。"));
            PumpUntilState(() => !AgentField<bool>(view, "_aiStreaming"), 3000);
            Check(!AgentField<bool>(view, "_aiWaitingForReply") && messages.Last().Content == "收到，已为你准备好。", "空首块不会导致空白，实际回复原位替换等待状态");
            SaveThemePreview(view, previews + "/api-reply.png");
            Send("请回复第二次测试"); view.AiStopButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntilState(() => !AgentField<bool>(view, "_aiStreaming"), 2000);
            Check(!AgentField<bool>(view, "_aiWaitingAnimating") && messages.Count == 3, "停止请求释放动画且不会保存等待文字为回复");
            Send("请回复第三次测试"); view.AiClearButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(messages.Count == 0 && !AgentField<bool>(view, "_aiWaitingForReply"), "请求中清空立即撤下等待气泡并取消任务");
            Send("请回复清空后的新测试"); gates[2].TrySetResult(Reply("过期的回复")); gates[3].SetResult(Reply("新的回复"));
            PumpUntilState(() => !AgentField<bool>(view, "_aiStreaming"), 3000);
            Check(messages.Count == 2 && messages.Last().Content == "新的回复", "旧请求延迟完成不会覆盖清空后的新对话");
            Send("测试空响应"); gates[4].SetResult(Reply("   "));
            PumpUntilState(() => !AgentField<bool>(view, "_aiStreaming"), 3000);
            Check(!AgentField<bool>(view, "_aiWaitingForReply") && messages.Last().Role == ChatMessage.RoleUser, "空白响应显示明确提示且不写入空助手消息");
            Send("测试失败响应"); gates[5].SetResult(new(HttpStatusCode.BadRequest) { Content = new StringContent("test request rejected") });
            PumpUntilState(() => !AgentField<bool>(view, "_aiStreaming"), 3000);
            Check(!AgentField<bool>(view, "_aiWaitingAnimating") && view.AiStopButton.Visibility == Visibility.Collapsed, "失败路径结束等待并恢复输入控件");
            source.RootVisual = null;
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
}
