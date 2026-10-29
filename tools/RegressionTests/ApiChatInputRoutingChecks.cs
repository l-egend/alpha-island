using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Plugins;
using alpha_island.Plugins.Isolation;
using alpha_island.Services;
using AlphaIsland.Plugins.ApiChat;
using AlphaIsland.PluginSdk;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

internal static partial class Program
{
    private static void ApiChatInputRoutingChecks(string root)
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
        var options = new IslandOptions();
        SettingsRegistry.Write(options, "AiEnabled", true);
        SettingsRegistry.Write(options, "AiBaseUrl", "http://127.0.0.1:19731/v1");
        SettingsRegistry.Write(options, "AiModel", "test-model");
        SettingsRegistry.Write(options, "AiDesktopContextEnabled", false);
        var context = new ConversationPluginFixture(options, Path.Combine(root, "api-manual-input"));
        Check(AiSecretStore.TrySave(Path.Combine(context.ConfigDirectory, "ai", "secrets.dat"), TestApiKey), "手动发送测试仅使用独立的加密测试密钥");
        using var view = new ApiChatView(context);
        var retryIconButton = (Button)view.FindName("AiRetryButton");
        Check(retryIconButton.Content as string == "\uE777" && retryIconButton.ToolTip as string == "重试上一条提问",
            "重试按钮使用已有的重置矢量图标，并保留重试上一条提问的含义");
        int requests = 0;
        using var http = new HttpClient(new AiTestHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"输入路由测试成功\"}}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") });
        }));
        using var service = new ChatService(http, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        SetAgentField(view, "_ai", service);
        using var worker = new HwndSource(new HwndSourceParameters("API manual input worker")
            { Width = 414, Height = 314, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x80000000) });
        worker.RootVisual = view;
        view.UpdateLayout();
        Directory.CreateDirectory("bin/weather-retry-previews");
        SaveThemePreview((FrameworkElement)retryIconButton.Parent, "bin/weather-retry-previews/api-toolbar.png");
        var surface = new PluginWorkerSurface(view);
        var errors = new List<Exception>();
        var commands = new List<RemoteCommand>();
        RemotePluginView? remote = null;
        remote = new("chat", command =>
        {
            commands.Add(command);
            bool accepted = surface.Input(command);
            if (command.Action != "position" && !accepted) errors.Add(new InvalidOperationException("Rejected " + command.Action));
            remote!.Apply(surface.Capture(414, 314));
            return Task.CompletedTask;
        }, error => errors.Add(error));
        using var host = new HwndSource(new HwndSourceParameters("API manual input host")
            { Width = 414, Height = 314, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x80000000) });
        host.RootVisual = remote;
        remote.Apply(surface.Capture(414, 314));
        remote.Measure(new System.Windows.Size(414, 314)); remote.Arrange(new Rect(0, 0, 414, 314)); remote.UpdateLayout();
        var input = (TextBox)remote.FindName("AiInputBox");
        var send = (Button)remote.FindName("AiSendButton");
        void Complete(string prompt, bool enter)
        {
            int before = requests;
            input.Text = prompt;
            PumpUntilState(() => view.AiInputBox.Text == prompt, 2000);
            remote.Apply(surface.Capture(414, 314));
            Check(send.IsEnabled, "输入非空消息后实际宿主发送按钮可用");
            if (enter) input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, host, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            else send.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntilState(() => requests > before && !AgentField<bool>(view, "_aiStreaming"), 2000);
            remote.Apply(surface.Capture(414, 314));
            Check(requests == before + 1 && view.AiInputBox.Text.Length == 0 && input.Text.Length == 0,
                enter ? "回车通过宿主与插件输入协议实际发送消息并清空输入框" : "点击通过宿主与插件输入协议实际发送消息并清空输入框");
        }
        Complete("请回复手动按钮测试", false);
        Complete("请回复手动回车测试", true);
        Check(errors.Count == 0, "消息输入、按钮和回车命令均被实际插件接受");
        Check(commands.Any(command => command.Action == "text") && commands.Any(command => command.Action == "click") && commands.Any(command => command.Action == "key"), "检查覆盖宿主输入队列，未绕过按钮直接调用发送方法");
        var configFactory = AgentField<Func<alpha_island.Conversations.IChatSettings, ChatConfig>>(view, "_aiConfigFactory");
        SetAgentField(view, "_aiConfigFactory", new Func<alpha_island.Conversations.IChatSettings, ChatConfig>(_ => throw new IOException("测试配置暂不可用")));
        int bubblesBefore = view.AiBubbleStack.Children.Count;
        view.AiInputBox.Text = "测试发送前配置失败";
        view.AiSendButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpAnimations(40);
        Check(view.AiBubbleStack.Children.Count > bubblesBefore && view.AiInputBox.Text == "测试发送前配置失败" && view.AiSendButton.IsEnabled,
            "发送前配置异常可见地提示失败并保留草稿，按钮不会静默无响应");
        SetAgentField(view, "_aiConfigFactory", configFactory);
        var waitingTools = new CancelledApiInputTools(); context.ToolDispatcher = waitingTools;
        using (var cancellation = new CancellationTokenSource())
        {
            view.AiInputBox.Text = "30分钟后提醒我喝水";
            var pending = view.SubmitTextAsync(Guid.NewGuid().ToString("N"), view.AiInputBox.Text, cancellation.Token);
            PumpUntilState(() => waitingTools.Started, 2000);
            cancellation.Cancel();
            AwaitPlugin(pending);
            Check(!AgentField<bool>(view, "_aiStreaming") && view.AiSendButton.IsEnabled,
                "取消本地提醒后保留草稿并恢复发送按钮，不留下就绪但无法发送的状态");
        }
        waitingTools = new CancelledApiInputTools(); context.ToolDispatcher = waitingTools;
        view.AiInputBox.Text = "20分钟后提醒我休息";
        view.AiSendButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntilState(() => waitingTools.Started, 2000);
        Check(view.AiStopButton.Visibility == Visibility.Visible, "本地指令执行中提供可用的停止入口");
        view.AiStopButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntilState(() => !AgentField<bool>(view, "_aiStreaming"), 2000);
        Check(view.AiSendButton.IsEnabled && view.AiStopButton.Visibility == Visibility.Collapsed && view.AiInputBox.Text == "20分钟后提醒我休息",
            "手动停止本地指令取消实际任务并恢复发送，不清除草稿");
        remote.Close(); host.RootVisual = null; worker.RootVisual = null;
        ApiChatWorkerInputChecks(root);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private sealed class CancelledApiInputTools : IToolDispatcher
    {
        internal bool Started;
        public ValueTask<ToolDefinition[]> GetToolsAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Array.Empty<ToolDefinition>());
        public async ValueTask<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
        { Started = true; await Task.Delay(Timeout.Infinite, cancellationToken); return ToolResult.Fail("cancelled fixture"); }
    }

    private static void ApiChatWorkerInputChecks(string root)
    {
        using var port = new TcpListener(IPAddress.Loopback, 0); port.Start();
        int number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{number}/"); listener.Start();
        int requests = 0;
        var server = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                var request = await listener.GetContextAsync();
                Interlocked.Increment(ref requests);
                byte[] response = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"真实隔离输入测试成功\"}}]}\n\ndata: [DONE]\n\n");
                request.Response.StatusCode = 200; request.Response.ContentType = "text/event-stream";
                await request.Response.OutputStream.WriteAsync(response); request.Response.Close();
            }
        });
        var options = new IslandOptions();
        string data = Path.Combine(root, "api-worker-input-data"), target = Path.Combine(root, "api-worker-input-plugin");
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(ExternalPluginSource($"ApiChatPlugin/bin/{TestBuildConfiguration}/net8.0-windows"))
            .Where(file => file.EndsWith(".dll") || file.EndsWith(".deps.json"))) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        File.Copy(ExternalPluginSource("ApiChatPlugin/plugin.json"), Path.Combine(target, "plugin.json"));
        var manifest = PluginManifest.Read(Path.Combine(target, "plugin.json"));
        string secret = Path.Combine(data, "config", "ai", "secrets.dat");
        Check(AiSecretStore.TrySave(secret, TestApiKey), "真实隔离输入回归不读取用户密钥");
        var config = new ChatConfig($"http://127.0.0.1:{number}/v1", TestApiKey, "test-model", "", .7, 1024);
        SettingsRegistry.Write(options, "AiBaseUrl", config.BaseUrl); SettingsRegistry.Write(options, "AiModel", config.Model);
        SettingsRegistry.Write(options, "AiToolsEnabled", false); SettingsRegistry.Write(options, "AiDesktopContextEnabled", false);
        Check(AiConnectionState.Verify(options, secret, config), "真实隔离输入测试使用已验证的本机 HTTP 接口");
        using var session = new PluginProcessSession(Dispatcher.CurrentDispatcher);
        session.Event += async item =>
        {
            if (item.Name == "host-call")
            {
                var call = PluginWire.Read<RemoteHostCall>(item.Value!.Value);
                object result = call.Kind == "desktop-context" ? DesktopContextSnapshot.Empty : Array.Empty<ToolDefinition>();
                await session.Request<bool>("host-result", new RemoteHostReply(call.Id, PluginWire.Element(result)));
            }
        };
        AwaitPlugin(session.StartAsync(new(target, manifest.Assembly, manifest.EntryType, manifest.Id, data, options.Extras), CancellationToken.None));
        try
        {
        RemotePluginView? remote = null;
        var errors = new List<Exception>();
        remote = new("chat", async command =>
        {
            if (!await session.Request<bool>("input", command) && command.Action != "position") errors.Add(new InvalidOperationException("Worker rejected " + command.Action));
        }, error => errors.Add(error));
        using var host = new HwndSource(new HwndSourceParameters("API real isolated input host")
            { Width = 414, Height = 314, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x80000000) });
        host.RootVisual = remote;
        RemoteFrame Capture() => AwaitPlugin(session.Request<RemoteFrame>("frame", new RemoteViewRequest("chat", 414, 314)));
        remote.Apply(Capture());
        remote.Measure(new System.Windows.Size(414, 314)); remote.Arrange(new Rect(0, 0, 414, 314)); remote.UpdateLayout();
        var input = (TextBox)remote.FindName("AiInputBox"); var send = (Button)remote.FindName("AiSendButton");
        for (int round = 0; round < 2; round++)
        {
            input.Text = round == 0 ? "请回复隔离按钮测试" : "请回复隔离回车测试";
            PumpAnimations(120); remote.Apply(Capture());
            Check(send.IsEnabled && Capture().Elements.Any(item => item.Name == "AiInputBox" && item.Text == input.Text), "用户输入同步到真实 API 插件且发送按钮启用");
            if (round == 0) send.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            else input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, host, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 4000 && requests <= round) { PumpAnimations(40); remote.Apply(Capture()); }
            Console.WriteLine($"Real isolated API input: round={round}, requests={requests}, inputLength={input.Text.Length}, rejected={errors.Count}, status={Capture().Elements.FirstOrDefault(item => item.Name == "AiStatusText")?.Text}");
            Check(requests == round + 1 && input.Text.Length == 0, round == 0 ? "真实插件进程的发送按钮触发 HTTP 请求并清空输入" : "真实插件进程的回车提交触发 HTTP 请求并清空输入");
            PumpAnimations(80); remote.Apply(Capture());
        }
        Check(errors.Count == 0, "真实跨进程输入均被接受，无静默丢弃");
        remote.Close(); host.RootVisual = null;
        listener.Stop();
        if (server.IsCompletedSuccessfully) server.GetAwaiter().GetResult();
        }
        finally { session.Dispose(); AwaitPlugin(session.Completion.ContinueWith(_ => true)); }
    }
}
