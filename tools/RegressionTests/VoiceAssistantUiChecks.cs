using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AlphaIsland.PluginSdk;
using alpha_island.Core;
using alpha_island.Services;
using alpha_island.Plugins.Isolation;

internal static partial class Program
{
    private static void VoiceAssistantUiChecks(alpha_island.MainWindow main, Grid root, string testRoot)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(main.Dispatcher));
        try
        {
            VoiceSessionChecks(); VoiceTextProtocolChecks(main, testRoot); VoiceApiInputChecks(testRoot);
            AgentField<VoiceAssistantSession>(main, "_voiceAssistant").Dispose();
            var capture = new FakeVoiceCapture(); int requests = 0;
            using var voice = new VoiceAssistantSession(main.Dispatcher, () => capture,
                (_, _) => { requests++; return Task.FromResult(new PluginTextInputResult(true, "ok")); },
                _ => Task.CompletedTask, () => true, () => Task.CompletedTask);
            SetAgentField(main, "_voiceAssistant", voice);
            voice.Changed += snapshot => PrivateCall(main, "OnVoiceAssistantChanged", snapshot);
            voice.Spectrum += frame => PrivateCall(main, "OnVoiceSpectrum", (object)frame);
            AwaitPlugin(voice.WakeAsync().ContinueWith(_ => true)); root.UpdateLayout(); PumpAnimations(400);
            var orb = AgentField<Border>(main, "_voiceStopOrb");
            var pill = AgentField<alpha_island.Controls.VoiceAssistantPill>(main, "_voicePill");
            Check(AgentField<IslandState>(main, "_state") == IslandState.Pill && pill.IsVisible && orb.IsVisible,
                "实际主窗口唤醒为一级小岛，右侧显示独立圆形取消岛");
            capture.Say("三十分钟后提醒我喝水"); capture.Frame(Enumerable.Range(0, 16).Select(i => i / 16f).ToArray());
            PumpAnimations(30); root.UpdateLayout();
            Check(pill.Transcript.Text == "三十分钟后提醒我喝水" && pill.Wave.ActualWidth == 66,
                "实际主岛中间显示识别文本，右侧为录音频谱");
            pill.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            pill.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent });
            Check(AgentField<IslandState>(main, "_state") == IslandState.Pill
                && !AgentField<IslandLayerHost>(main, "_layers").IsPinned("voice-assistant"),
                "录音期间点击正文不展开其他卡片，右键不会把临时语音层永久置顶");
            var center = orb.PointToScreen(new System.Windows.Point(18, 18));
            Check((bool)PrivateCall(main, "IsPointInsideInteractiveZone", (int)center.X, (int)center.Y)!,
                "圆形取消岛加入原生窗口命中区域，点击不会穿透");
            SaveConversationPreview(main.IslandScene, "docs/screens/voice-assistant-pill.png", 1200, 140);
            orb.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            PumpUntilState(() => !voice.Visible); root.UpdateLayout();
            Check(requests == 0 && capture.Disposed && !orb.IsVisible, "点击真实取消圆岛释放麦克风并关闭小岛，不提交指令");
            capture = new FakeVoiceCapture(); AwaitPlugin(voice.WakeAsync().ContinueWith(_ => true));
            main.ApplySessionNotification(true); PumpUntilState(() => !voice.Visible);
            Check(capture.Disposed && requests == 0, "实际锁屏路径立即取消录音并释放麦克风，不提交指令");
            main.ApplySessionNotification(false);
            VoiceFailureUiChecks(root);
            capture = new FakeVoiceCapture(); AwaitPlugin(voice.WakeAsync().ContinueWith(_ => true));
            const string failure = "麦克风已断开或被独占，本次录音已停止。请连接默认麦克风后重新唤醒语音助手，本次不会发送任何 API 请求。";
            capture.Error(failure); PumpUntilState(() => voice.Snapshot.State == VoiceAssistantState.Error);
            string opened = ""; SetAgentField(main, "_voiceOpenSettings", (Action<string>)(uri => opened = uri));
            pill.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
            var help = AgentField<alpha_island.Controls.VoiceAssistantHelpWindow>(main, "_voiceHelpWindow");
            try
            {
                Check(help.IsVisible && help.Detail.Text == failure && help.Detail.TextWrapping == TextWrapping.Wrap,
                    "点击真实错误小岛打开完整说明，长原因可换行阅读");
                help.SettingsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(opened == VoiceRecognitionProblem.MicrophoneSettings, "麦克风错误的设置按钮进入对应权限页面，不打开无关的联机语音开关");
                root.UpdateLayout(); PumpAnimations(250);
                SaveConversationPreview(main.IslandScene, "docs/screens/voice-assistant-error.png", 1200, 140);
                SaveConversationPreview((FrameworkElement)help.Content, "docs/screens/voice-assistant-help.png", 416, 210);
            }
            finally { help.Close(); }
            PumpUntilState(() => !voice.Visible);
            Check(requests == 0, "查看错误说明或系统设置不调用对话 API");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void VoiceTextProtocolChecks(alpha_island.MainWindow main, string root)
    {
        string directory = Path.Combine(root, "voice-text-probe"); Directory.CreateDirectory(directory);
        const string binary = "AlphaIsland.PluginFixture.dll";
        File.Copy(Path.GetFullPath("tools/PluginFixture/bin/Release/net8.0-windows/" + binary), Path.Combine(directory, binary));
        using var worker = new PluginProcessSession(main.Dispatcher);
        AwaitPlugin(worker.StartAsync(new(directory, binary, "AlphaIsland.PluginFixture.TextInputProbePlugin", "fixture.voice-input",
            Path.Combine(root, "voice-worker-data"), new Dictionary<string, System.Text.Json.JsonElement>()), CancellationToken.None));
        string id = Guid.NewGuid().ToString("N");
        var echo = AwaitPlugin(worker.Request<PluginTextInputResult>("text-submit", new RemoteTextInput("chat", id, "语音指令 🌙")));
        Check(echo.Accepted && echo.Message == "语音指令 🌙", "语音文字通过真正的插件独立进程 IPC 传递，Unicode 完整");
        bool pinged = false;
        worker.Event += item => { if (item.Name == "device-batteries") pinged = true; };
        id = Guid.NewGuid().ToString("N");
        var pending = worker.Request<PluginTextInputResult>("text-submit", new RemoteTextInput("chat", id, "wait"));
        PumpAnimations(70); AwaitPlugin(worker.Request<bool>("action", "text-ping")); PumpAnimations(40);
        Check(pinged && !pending.IsCompleted && worker.IsAlive, "等待模型期间插件消息循环、事件和心跳保持可用");
        AwaitPlugin(worker.Request<bool>("text-cancel", new RemoteTextInput("chat", Guid.NewGuid().ToString("N"))));
        Check(!pending.IsCompleted, "取消其他请求 ID 不会影响当前指令");
        AwaitPlugin(worker.Request<bool>("text-cancel", new RemoteTextInput("chat", id)));
        Check(AwaitPlugin(pending).Message == "cancelled", "录音取消消息能跨进程及时中断对应指令");
        id = Guid.NewGuid().ToString("N");
        pending = worker.Request<PluginTextInputResult>("text-submit", new RemoteTextInput("chat", id, "wait-throw"));
        PumpAnimations(40); AwaitPlugin(worker.Request<bool>("text-cancel", new RemoteTextInput("chat", id)));
        Check(AwaitPlugin(pending).Accepted && worker.IsAlive,
            "插件抛出正常取消异常时转换为停止结果，不误判为插件崩溃");
    }

    private static void VoiceApiInputChecks(string root)
    {
        var options = new IslandOptions(); string directory = Path.Combine(root, "voice-api");
        Directory.CreateDirectory(directory);
        using var view = new AlphaIsland.Plugins.ApiChat.ApiChatView(new ConversationPluginFixture(options, directory));
        string secret = Path.Combine(directory, "config", "ai", "secrets.dat");
        Check(AiSecretStore.TrySave(secret, TestApiKey), "语音对话验证仅创建隔离的测试密钥");
        var config = new ChatConfig("https://example.invalid/v1", TestApiKey, "test-model", "", .7, 1024);
        SettingsRegistry.Write(options, "AiBaseUrl", config.BaseUrl); SettingsRegistry.Write(options, "AiModel", config.Model);
        AiConnectionState.Verify(options, secret, config); PrivateCall(view, "OnAiOptionsChanged");
        var bodies = new List<string>();
        using var http = new HttpClient(new AiTestHandler(async (request, _) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"content\":\"指令已执行\"}}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }));
        using var service = new ChatService(http, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)); SetAgentField(view, "_ai", service);
        var first = AwaitPlugin(view.SubmitTextAsync(Guid.NewGuid().ToString("N"), "请回答第一条语音", CancellationToken.None));
        var second = AwaitPlugin(view.SubmitTextAsync(Guid.NewGuid().ToString("N"), "请继续回答第二条语音", CancellationToken.None));
        Check(first.Accepted && second.Accepted && second.Message == "指令已执行" && bodies.Count == 2,
            "语音复用已有对话发送及流式回复路径，无真实 API 费用");
        Check(bodies[1].Contains("请回答第一条语音") && AgentField<List<ChatMessage>>(view, "_aiMessages").Count == 4,
            "连续语音使用同一个对话历史，而不是每次新建会话");
        bool confirmed = false;
        SetAgentField(view, "_confirmSensitiveSend", (Func<SensitiveTextReport, bool>)(_ => { confirmed = true; return false; }));
        var declined = AwaitPlugin(view.SubmitTextAsync(Guid.NewGuid().ToString("N"), "我的 API Key 是 sk-abcdefghijklmnopqrstuvwxyz1234567890", CancellationToken.None));
        Check(confirmed && !declined.Accepted && bodies.Count == 2, "语音仍经过已有敏感内容确认，拒绝后不发送 API");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool stopped = false;
        try { AwaitPlugin(view.SubmitTextAsync(Guid.NewGuid().ToString("N"), "取消的指令", cancelled.Token)); }
        catch (OperationCanceledException) { stopped = true; }
        Check(stopped && bodies.Count == 2, "已取消的跨进程请求不能开始新的模型调用");
        using var duringConfirmation = new CancellationTokenSource(); stopped = false;
        SetAgentField(view, "_confirmSensitiveSend", (Func<SensitiveTextReport, bool>)(_ => { duringConfirmation.Cancel(); return true; }));
        try { AwaitPlugin(view.SubmitTextAsync(Guid.NewGuid().ToString("N"), "API Key sk-abcdefghijklmnopqrstuvwxyz1234567890", duringConfirmation.Token)); }
        catch (OperationCanceledException) { stopped = true; }
        Check(stopped && bodies.Count == 2, "确认窗口期间收到取消后，即使随后同意也不再发送指令");
    }
}
