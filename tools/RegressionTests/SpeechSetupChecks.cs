using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AlphaIsland.Speech;
using alpha_island.Core;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

internal static partial class Program
{
    private static async Task SpeechSetupProtocolChecks()
    {
        var options = new SpeechRecognitionOptions("mimo", SpeechSettings.DefaultUrl + "/chat/completions", "mimo-v2.5-asr", TestApiKey);
        int gets = 0, posts = 0;
        using var http = new HttpClient(new AiTestHandler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                gets++;
                Check(request.RequestUri!.AbsoluteUri == SpeechSettings.DefaultUrl + "/models"
                    && request.Headers.GetValues("api-key").Single() == TestApiKey, "模型列表从完整识别端点还原基础地址，MiMo 使用独立 api-key");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"chat-main\"},{\"id\":\"custom-asr\"},{\"id\":\"whisper-test\"},{\"id\":\"custom-asr\"}]}") };
            }
            posts++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            string data = body.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("input_audio").GetProperty("data").GetString()!;
            byte[] audio = Convert.FromBase64String(data[(data.IndexOf(',') + 1)..]);
            Check(body.RootElement.GetProperty("model").GetString() == "custom-asr" && audio.Length == 32044 && audio.Skip(44).All(b => b == 0),
                "接口测试使用当前所选模型和一秒静音 WAV，不录音、不携带历史内容");
            return SseSpeech("");
        }));
        var service = new SpeechModelService(http);
        var models = await service.ListModelsAsync(options, CancellationToken.None);
        Check(models.Count == 3 && models[0] == "custom-asr" && models[1] == "whisper-test" && models.Contains("chat-main"),
            "模型列表去重并优先排列识别模型，保留非标准名称供兼容服务选择");
        var result = await service.TestRecognitionAsync(options with { Model = "custom-asr" }, CancellationToken.None);
        Check(result.Text.Length == 0 && gets == 1 && posts == 1, "静音测试无识别文字仍验证成功，每次点击只发送一个识别请求");
        using var compatible = new HttpClient(new AiTestHandler((request, _) =>
        {
            Check(request.Headers.Authorization?.Parameter == TestApiKey && request.RequestUri!.AbsolutePath == "/v1/models", "兼容语音服务模型列表使用 Bearer 鉴权");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"custom-asr\"}]}") });
        }));
        await new SpeechModelService(compatible).ListModelsAsync(options with { Provider = "transcription", BaseUrl = "https://example.invalid/v1/audio/transcriptions", Model = "" }, CancellationToken.None);
        using var local = new HttpClient(new AiTestHandler((request, _) =>
        {
            Check(request.Headers.Authorization == null && !request.Headers.Contains("api-key") && request.RequestUri!.IsLoopback, "本地模型列表无需云端密钥");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"local-model\"}]}") });
        }));
        await new SpeechModelService(local).ListModelsAsync(options with { Provider = "local-endpoint", BaseUrl = "http://localhost:8080/v1", ApiKey = "" }, CancellationToken.None);
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests })
        {
            int calls = 0;
            using var failing = new HttpClient(new AiTestHandler((_, _) =>
            { calls++; return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(TestApiKey) }); }));
            try { await new SpeechModelService(failing).ListModelsAsync(options, CancellationToken.None); throw new Exception("Expected API error"); }
            catch (SpeechRecognitionException error) { Check(calls == 1 && !error.Message.Contains(TestApiKey), $"模型接口 HTTP {(int)status} 明确报错，不泄露响应或密钥、不自动重试"); }
        }
        using var malformed = new HttpClient(new AiTestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"models\":[]}") })));
        try { await new SpeechModelService(malformed).ListModelsAsync(options, CancellationToken.None); throw new Exception("Expected unsupported model list"); }
        catch (SpeechRecognitionException error) { Check(error.Message.Contains("手动输入"), "不支持模型列表的服务明确保留手动配置入口"); }
    }

    private static void SpeechSetupUiChecks(string root)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var options = new IslandOptions();
        SettingsRegistry.Write(options, "SpeechBaseUrl", "https://example.invalid/v1");
        SettingsRegistry.Write(options, "SpeechModel", "my-current-model");
        int gets = 0, posts = 0; bool delayed = false, canceled = false;
        using var http = new HttpClient(new AiTestHandler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                gets++;
                if (delayed) { try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { canceled = true; throw; } }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"new-asr\"},{\"id\":\"other-model\"}]}") };
            }
            posts++; return SseSpeech("");
        }));
        var settings = new alpha_island.Settings.SettingsWindow(false) { SpeechSecretRoot = root, SpeechEndpoints = new(http) };
        try
        {
            settings.LoadFrom(options);
            IslandOptions? changedOptions = null; settings.OptionsChanged += (_, value) => changedOptions = value;
            Check(gets == 0 && posts == 0, "打开语音设置不请求网络、不启动录音");
            var model = (ComboBox)settings.FindName("SpeechModelBox");
            Check(model.IsEditable && model.Text == "my-current-model", "语音模型为可编辑选择框，加载时完整保留手动填写的名称");
            ((PasswordBox)settings.FindName("SpeechApiKeyBox")).Password = TestApiKey;
            var fetch = (Button)settings.FindName("SpeechFetchModelsButton");
            var test = (Button)settings.FindName("SpeechTestButton");
            var cancel = (Button)settings.FindName("SpeechCancelTestButton");
            var status = (TextBlock)settings.FindName("SpeechSetupStatus");
            fetch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpAnimations(100);
            Check(gets == 1 && model.Items.Count == 2 && model.Text == "my-current-model", "获取模型只更新候选列表，不偷偷替换当前模型");
            model.SelectedIndex = 0; PumpAnimations(60);
            Check(changedOptions != null && SettingsRegistry.GetText(changedOptions, "SpeechModel", "") == "new-asr", "选择获取到的模型即时保存到语音配置");
            model.Text = "my-manual-asr"; PumpAnimations(60);
            Check(changedOptions != null && SettingsRegistry.GetText(changedOptions, "SpeechModel", "") == "my-manual-asr", "手动输入模型名同样即时保存，不依赖列表接口");
            test.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpAnimations(100);
            Check(posts == 1 && status.Text.Contains("测试通过"), "测试按钮调用实际识别适配器并在设置内显示结果");
            delayed = true;
            fetch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpAnimations(80);
            Check(!fetch.IsEnabled && !test.IsEnabled && cancel.Visibility == Visibility.Visible, "请求进行时防止重复点击，并显示取消按钮");
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpAnimations(100);
            Check(canceled && fetch.IsEnabled && test.IsEnabled && cancel.Visibility == Visibility.Collapsed && status.Text.Contains("取消"), "取消模型请求后恢复控件，不留下忙碌状态");
            canceled = false;
            fetch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpAnimations(60);
            settings.ForceClose = true; settings.Close(); PumpAnimations(100);
            Check(canceled, "关闭设置会停止仍在进行的语音接口请求");
        }
        finally { settings.ForceClose = true; settings.Close(); SynchronizationContext.SetSynchronizationContext(previous); }
    }
}
