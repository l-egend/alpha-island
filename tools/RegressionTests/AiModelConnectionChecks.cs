using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static HttpResponseMessage ModelResponse(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static async Task AiModelConnectionChecks()
    {
        var config = new ChatConfig("https://example.invalid/v1/chat/completions", TestApiKey, "", "private-prompt", .7, 1024);
        Check(config.ModelsEndpoint == "https://example.invalid/v1/models" && config.HasValidEndpoint && !config.IsUsable,
            "未填写模型名也能连接并获取模型，完整对话地址正确还原模型列表端点");
        string? method = null, endpoint = null; bool auth = false;
        using (var http = new HttpClient(new AiTestHandler((req, _) =>
        {
            method = req.Method.Method; endpoint = req.RequestUri!.AbsolutePath;
            auth = req.Headers.Authorization?.Parameter == TestApiKey;
            return Task.FromResult(ModelResponse("{\"data\":[{\"id\":\"z-model\"},{\"id\":\"a-model\"},{\"id\":\"a-model\"},{\"id\":null},{\"id\":\"\"},null]}"));
        })))
        using (var service = new ChatService(http, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)))
        {
            Check((await service.ListModelsAsync(config)).SequenceEqual(new[] { "a-model", "z-model" }), "服务器返回的模型排序去重且忽略无效条目");
            Check(method == "GET" && endpoint == "/v1/models" && auth, "模型列表使用真实 GET 端点及密钥鉴权");
        }
        foreach (var invalid in new[] { "{\"data\":[]}", "{}", "not-json", "{\"data\":null}" })
        {
            using var http = new HttpClient(new AiTestHandler((_, _) => Task.FromResult(ModelResponse(invalid))));
            using var service = new ChatService(http, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            bool rejected = false; try { await service.ListModelsAsync(config); } catch (ChatException) { rejected = true; }
            Check(rejected, "空模型列表或无效返回不误报连接成功 " + invalid);
        }
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests })
        {
            using var http = new HttpClient(new AiTestHandler((_, _) => Task.FromResult(ModelResponse("error " + TestApiKey, status))));
            using var service = new ChatService(http, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            bool rejected = false; try { await service.ListModelsAsync(config); }
            catch (ChatException ex) { rejected = !ex.Message.Contains(TestApiKey); }
            Check(rejected, "列表鉴权、限流及不支持接口正确提示且不泄露密钥 " + status);
        }
        string? body = null;
        using (var http = new HttpClient(new AiTestHandler(async (req, token) =>
        { body = await req.Content!.ReadAsStringAsync(token); method = req.Method.Method; return ModelResponse("{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}"); })))
        using (var service = new ChatService(http, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)))
        {
            var result = await service.TestModelAsync(config with { Model = "a-model" });
            using var document = JsonDocument.Parse(body!);
            Check(result.Model == "a-model" && result.ElapsedMilliseconds >= 0 && method == "POST", "模型测试实际提交所选模型并返回耗时");
            Check(document.RootElement.GetProperty("model").GetString() == "a-model"
                && document.RootElement.GetProperty("messages").GetArrayLength() == 1
                && !document.RootElement.GetProperty("stream").GetBoolean() && !body!.Contains("private-prompt")
                && !body.Contains(TestApiKey), "测试只发送固定短请求，不发送用户系统提示、历史或 JSON 明文密钥");
        }
        using (var http = new HttpClient(new AiTestHandler((_, _) => Task.FromResult(ModelResponse("{\"choices\":[{\"message\":{\"reasoning_content\":\"thinking\"}}]}")))))
        using (var service = new ChatService(http, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)))
            Check((await service.TestModelAsync(config with { Model = "reasoning-model" })).Model == "reasoning-model", "思考模型返回有效推理内容也能确认连接");
        foreach (var invalid in new[] { "{}", "{\"choices\":[]}", "{\"choices\":[{\"message\":{\"content\":\"\"}}]}", "{\"error\":\"" + TestApiKey + "\"}" })
        {
            using var http = new HttpClient(new AiTestHandler((_, _) => Task.FromResult(ModelResponse(invalid))));
            using var service = new ChatService(http, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            bool failed = false; try { await service.TestModelAsync(config with { Model = "a-model" }); }
            catch (ChatException ex) { failed = !ex.Message.Contains(TestApiKey); }
            Check(failed, "无模型输出或错误 JSON 不误报测试成功且隐藏密钥");
        }
        using (var http = new HttpClient(new AiTestHandler(async (_, token) =>
            { await Task.Delay(Timeout.Infinite, token); return ModelResponse("{}"); })))
        using (var service = new ChatService(http, TimeSpan.FromMilliseconds(70), TimeSpan.FromSeconds(1)))
        {
            bool timedOut = false; try { await service.ListModelsAsync(config); } catch (ChatException ex) { timedOut = ex.Message.Contains("超时"); }
            Check(timedOut, "获取模型列表超时后及时结束");
            using var cancellation = new CancellationTokenSource(30); bool stopped = false;
            try { await service.ListModelsAsync(config, cancellation.Token); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped, "取消模型连接保留用户取消语义");
        }
        using var portReservation = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        portReservation.Start(); int port = ((IPEndPoint)portReservation.LocalEndpoint).Port; portReservation.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var calls = new List<string>(); bool allAuthorized = true;
        string? probeBody = null;
        var server = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                var context = await listener.GetContextAsync();
                calls.Add(context.Request.HttpMethod + " " + context.Request.Url!.AbsolutePath);
                allAuthorized &= context.Request.Headers["Authorization"] == "Bearer " + TestApiKey;
                if (context.Request.HttpMethod == "POST")
                { using var reader = new System.IO.StreamReader(context.Request.InputStream); probeBody = await reader.ReadToEndAsync(); }
                byte[] bytes = Encoding.UTF8.GetBytes(i == 0 ? "{\"data\":[{\"id\":\"local-model\"}]}" : "{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}");
                context.Response.ContentType = "application/json"; context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
            }
        });
        using (var service = new ChatService())
        {
            var local = config with { BaseUrl = $"http://127.0.0.1:{port}/v1" };
            Check((await service.ListModelsAsync(local)).Single() == "local-model", "真实本机 HTTP 接口成功获取可用模型");
            Check((await service.TestModelAsync(local with { Model = "local-model" })).Model == "local-model", "真实本机 HTTP 所选模型测试成功");
        }
        await server.WaitAsync(TimeSpan.FromSeconds(3));
        Check(allAuthorized && calls.SequenceEqual(new[] { "GET /v1/models", "POST /v1/chat/completions" })
            && probeBody != null && !probeBody.Contains(TestApiKey), "实际网络请求正确分离列表获取与模型测试，密钥仅通过鉴权头发送");
    }

    private static void AiModelPickerUiChecks(string root)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        bool failConnection = false;
        using var http = new HttpClient(new AiTestHandler((req, _) => Task.FromResult(failConnection ? ModelResponse("unauthorized", HttpStatusCode.Unauthorized) : req.Method == HttpMethod.Get
            ? ModelResponse("{\"data\":[{\"id\":\"model-a\"},{\"id\":\"model-b\"}]}")
            : ModelResponse("{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}"))));
        using var service = new ChatService(http, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        var settings = new AlphaIsland.Plugins.ApiChat.ApiSettingsView(false, System.IO.Path.Combine(root, "picker", "key.dat"), service);
        void Complete(Task task) { var limit = System.Diagnostics.Stopwatch.StartNew(); while (!task.IsCompleted && limit.ElapsedMilliseconds < 5000) PumpAnimations(15); task.GetAwaiter().GetResult(); }
        try
        {
            var options = new IslandOptions().Normalized(); settings.LoadFrom(options);
            Check(settings.FindName("AiTemperatureSlider") == null && settings.FindName("AiMaxTokensSlider") == null
                && SettingsRegistry.Find("AiTemperature") == null && SettingsRegistry.Find("AiMaxTokens") == null, "温度和最大回复长度选项完全从设置界面及注册表移除");
            Check(!settings.AiModelBox.IsEditable && !settings.AiModelBox.IsEnabled && !settings.TestAiModelButton.IsEnabled, "尚未连接时不能手填或选择未验证的模型列表");
            settings.AiApiKeyBox.Password = TestApiKey;
            Complete(settings.ConnectAiModelsAsync());
            Check(settings.AiModelBox.IsEnabled && settings.AiModelBox.Items.Count == 2 && settings.TestAiModelButton.IsEnabled
                && settings.AiConnectionStatus.Text.Contains("已连接"), "AI 开关关闭时也可连接并自动填充可用模型");
            settings.AiModelBox.SelectedItem = "model-b";
            var current = (IslandOptions)typeof(AlphaIsland.Plugins.ApiChat.ApiSettingsView).GetField("_options", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(settings)!;
            Check(SettingsRegistry.GetBool(current, "AiEnabled", false) && settings.AiEnabledToggle.IsChecked == true
                && settings.AiApiKeyBox.Password == "" && AiSecretStore.Load(System.IO.Path.Combine(root, "picker", "key.dat")) == TestApiKey,
                "输入完整密钥并连接成功后自动加密保存且启用对话");
            Check(SettingsRegistry.GetText(current, "AiModel", "") == "model-b", "从下拉选择模型即时更新配置");
            Complete(settings.TestAiModelAsync());
            Check(settings.AiConnectionStatus.Text.Contains("测试成功：model-b"), "模型测试显示所选模型和成功结果");
            settings.LoadFrom(current);
            Check(settings.AiModelBox.SelectedItem as string == "model-b" && settings.TestAiModelButton.IsEnabled && settings.AiEnabledToggle.IsChecked == true,
                "连接后重新打开设置保留密钥、模型和自动开启状态");
            settings.AiApiKeyBox.Password = TestApiKey; Complete(settings.ConnectAiModelsAsync());
            settings.SaveAiKeyButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            settings.LoadFrom(current);
            Check(settings.AiModelBox.IsEnabled && settings.AiModelBox.SelectedItem as string == "model-b", "保存密钥后重新打开保持对应的模型选择");
            settings.AiBaseUrlBox.Text = "https://another.invalid/v1";
            Check(!settings.AiModelBox.IsEnabled && !settings.TestAiModelButton.IsEnabled, "更换接口地址立即禁用旧模型列表");
            Check(settings.AiEnabledToggle.IsChecked == false, "更换接口后自动关闭 AI 对话");
            Complete(settings.ConnectAiModelsAsync()); settings.AiApiKeyBox.Password = "different-test-key";
            Check(!settings.AiModelBox.IsEnabled && !settings.TestAiModelButton.IsEnabled, "更换输入密钥立即使旧列表失效");
            Check(settings.AiEnabledToggle.IsChecked == false, "更换输入密钥后自动关闭 AI 对话");
            Complete(settings.ConnectAiModelsAsync());
            Check(settings.AiEnabledToggle.IsChecked == true, "新密钥重新连接成功后自动恢复对话");
            failConnection = true; Complete(settings.TestAiModelAsync());
            Check(settings.AiEnabledToggle.IsChecked == false && settings.AiConnectionStatus.Text.Contains("测试失败"), "模型测试连接失败后自动关闭对话");
            failConnection = false; Complete(settings.ConnectAiModelsAsync());
            string savedKey = System.IO.Path.Combine(root, "picker", "key.dat");
            settings.AiApiKeyBox.Password = "unsaved-replacement";
            using (var locked = new System.IO.FileStream(savedKey, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.None))
            {
                Complete(settings.ConnectAiModelsAsync());
                Check(settings.AiEnabledToggle.IsChecked == false && settings.AiConnectionStatus.Text.Contains("保存失败")
                    && settings.AiApiKeyBox.Password == "unsaved-replacement", "接口连接成功但密钥无法保存时不启用对话并保留输入");
            }
            Complete(settings.ConnectAiModelsAsync());
            settings.ClearAiKeyButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(settings.AiEnabledToggle.IsChecked == false, "删除密钥立即关闭对话");
            Complete(settings.ConnectAiModelsAsync());
            Check(settings.AiEnabledToggle.IsChecked == false, "接口允许匿名连接时没有密钥也保持对话关闭");
            using var slowHttp = new HttpClient(new AiTestHandler(async (_, token) =>
                { await Task.Delay(Timeout.Infinite, token); return ModelResponse("{}"); }));
            using var slowService = new ChatService(slowHttp, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
            var pendingSettings = new AlphaIsland.Plugins.ApiChat.ApiSettingsView(false, null, slowService);
            try
            {
                pendingSettings.LoadFrom(new IslandOptions().Normalized());
                Task pending = pendingSettings.ConnectAiModelsAsync();
                Check(!pending.IsCompleted && pendingSettings.ConnectAiModelsButton.Content as string == "取消测试", "连接期间按钮提供取消操作且不阻塞界面");
                pendingSettings.ConnectAiModelsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Complete(pending);
                Check(pendingSettings.AiConnectionStatus.Text == "已取消测试" && !pendingSettings.AiModelBox.IsEnabled, "点击取消后恢复界面，旧请求不覆盖取消状态");
                pending = pendingSettings.ConnectAiModelsAsync();
                pendingSettings.AiBaseUrlBox.Text = "https://changed.invalid/v1"; Complete(pending);
                Check(!pendingSettings.AiModelBox.IsEnabled && pendingSettings.AiConnectionStatus.Text.Contains("请先连接"), "请求中更换地址会取消旧请求且不回填旧结果");
                pending = pendingSettings.ConnectAiModelsAsync(); pendingSettings.Dispose(); Complete(pending);
                Check(pending.IsCompleted, "关闭设置会及时取消未完成的模型连接");
            }
            finally { pendingSettings.Dispose(); }
        }
        finally { settings.Dispose(); SynchronizationContext.SetSynchronizationContext(previous); }
    }
}
