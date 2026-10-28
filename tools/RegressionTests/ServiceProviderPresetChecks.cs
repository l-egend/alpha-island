using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Controls;
using AlphaIsland.PluginSdk;
using AlphaIsland.Speech;
using alpha_island.Core;
using alpha_island.Conversations;
using alpha_island.Services;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

internal static partial class Program
{
    private static async Task ServiceProviderProtocolChecks()
    {
        foreach (var preset in AiServicePresets.Speech.Where(item => item.BaseUrl.StartsWith("https://")))
        {
            var settings = new IslandOptions(); SettingsRegistry.SetExtra(settings, "SpeechProvider", preset.Id);
            SettingsRegistry.SetExtra(settings, "SpeechBaseUrl", preset.BaseUrl); SettingsRegistry.SetExtra(settings, "SpeechModel", preset.Model);
            SettingsRegistry.SetExtra(settings, "SpeechLanguage", "zh");
            var options = SpeechSettings.Read(settings, Path.Combine(Path.GetTempPath(), "missing-speech-key")) with { ApiKey = TestApiKey };
            int requests = 0;
            using var http = new HttpClient(new AiTestHandler(async (request, token) =>
            {
                requests++;
                bool chat = preset.Protocol is "mimo" or "chat-audio";
                Check(request.RequestUri!.AbsoluteUri == preset.BaseUrl + (chat ? "/chat/completions" : "/audio/transcriptions"), preset.Name + " 使用正确的识别协议与端点");
                Check(preset.Id == "mimo" ? request.Headers.GetValues("api-key").Single() == TestApiKey : request.Headers.Authorization?.Parameter == TestApiKey,
                    preset.Name + " 使用对应鉴权方式");
                string body = await request.Content!.ReadAsStringAsync(token);
                if (chat)
                {
                    using var json = JsonDocument.Parse(body);
                    var audio = json.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("input_audio");
                    Check(json.RootElement.GetProperty("model").GetString() == preset.Model && audio.GetProperty("data").GetString()!.StartsWith("data:audio/wav;base64,"), preset.Name + " 提交当前模型及 WAV 数据");
                    if (preset.Id == "dashscope-asr") Check(!audio.TryGetProperty("format", out _) && json.RootElement.GetProperty("asr_options").GetProperty("language").GetString() == "zh", "百炼使用其音频字段和语言选项");
                    return ModelResponse("{\"choices\":[{\"message\":{\"content\":\"识别成功\"},\"finish_reason\":\"stop\"}]}");
                }
                Check(body.Contains(preset.Model) && body.Contains("speech.wav"), preset.Name + " 提交模型和独立音频文件");
                if (preset.Id == "openai-asr") Check(body.Contains("languages[]"), "OpenAI 当前转写模型使用 languages 语言提示");
                return ModelResponse("{\"text\":\"识别成功\"}");
            }));
            var result = await new SpeechRecognizerRegistry(http).Create(options).RecognizeAsync(SpeechAudio.FromPcm(new short[16000]), CancellationToken.None);
            Check(result.Text == "识别成功" && requests == 1, preset.Name + " 接入现有语音链路且不自动重试");
        }
        foreach (string provider in new[] { "mimo", "deepseek" })
        {
            var preset = AiServicePresets.Chat.Single(item => item.Id == provider);
            using var http = new HttpClient(new AiTestHandler((request, _) =>
            {
                Check(provider == "mimo" ? request.Headers.Contains("api-key") && request.Headers.Authorization == null
                    : request.Headers.Authorization?.Parameter == TestApiKey, provider + " 对话服务的模型查询与鉴权匹配");
                return Task.FromResult(ModelResponse("{\"data\":[{\"id\":\"fixture-model\"}]}"));
            }));
            using var service = new ChatService(http, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            Check((await service.ListModelsAsync(new ChatConfig(preset.BaseUrl, TestApiKey, "", "", .7, 256))).Single() == "fixture-model", provider + " 可使用标准模型查询");
        }
    }

    private static void ServiceProviderUiChecks(string root)
    {
        int requests = 0;
        using var http = new HttpClient(new AiTestHandler((request, _) =>
        {
            requests++;
            return Task.FromResult(request.Method == HttpMethod.Get ? ModelResponse("{}", HttpStatusCode.NotFound)
                : ModelResponse("{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}"));
        }));
        var options = new IslandOptions(); SettingsRegistry.SetExtra(options, "SpeechProvider", "chat-audio");
        SettingsRegistry.SetExtra(options, "SpeechBaseUrl", "https://example.invalid/speech/v1"); SettingsRegistry.SetExtra(options, "SpeechModel", "custom-asr");
        string speechRoot = Path.Combine(root, "speech"); SpeechSettings.SaveKey(TestApiKey, speechRoot);
        var speech = new alpha_island.Settings.SettingsWindow(false) { SpeechSecretRoot = speechRoot };
        try
        {
            speech.LoadFrom(options);
            var provider = (ComboBox)speech.FindName("SpeechProviderBox"); var address = (TextBox)speech.FindName("SpeechBaseUrlBox");
            var model = (ComboBox)speech.FindName("SpeechModelBox");
            Check(address.Text == "https://example.invalid/speech/v1" && model.Text == "custom-asr", "打开设置保留已有自定义语音配置");
            Check(provider.Items.Count >= 9, "语音选择框包含常用服务商和原有自定义、本地选项");
            foreach (var preset in AiServicePresets.Speech.Where(item => item.BaseUrl.Length > 0))
            {
                provider.SelectedValue = preset.Id;
                Check(address.Text == preset.BaseUrl && model.Text == preset.Model, preset.Name + " 一次选择填入地址与模型");
            }
            provider.SelectedValue = "groq-asr";
            var current = AgentField<IslandOptions>(speech, "_options");
            Check(SpeechSettings.Read(current, speechRoot).ApiKey == "" && AiSecretStore.Load(SpeechSettings.SecretPath(speechRoot)) == TestApiKey,
                "更换语音服务不复用旧接口的密钥，同时保留原密文");
            provider.SelectedValue = "transcription"; address.Text = "https://custom.invalid/v1"; model.Text = "manual-asr";
            Check(SpeechSettings.Read(AgentField<IslandOptions>(speech, "_options"), speechRoot).Model == "manual-asr", "自定义语音接口仍可编辑模型");
        }
        finally { speech.ForceClose = true; speech.Close(); }

        string keyFile = Path.Combine(root, "chat", "secret.dat"); AiSecretStore.TrySave(keyFile, TestApiKey);
        using var service = new ChatService(http, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        using var chat = new AlphaIsland.Plugins.ApiChat.ApiSettingsView(false, keyFile, service);
        var chatOptions = new IslandOptions(); SettingsRegistry.SetExtra(chatOptions, "AiBaseUrl", "https://example.invalid/v1");
        SettingsRegistry.SetExtra(chatOptions, "AiModel", "old-model"); chat.LoadFrom(chatOptions);
        var presets = (ComboBox)chat.FindName("AiProviderPresetBox");
        Check((string)presets.SelectedValue == "custom" && chat.AiBaseUrlBox.Text == "https://example.invalid/v1", "API 对话识别自定义地址且不会覆盖原配置");
        Check(presets.Items.Count >= 10, "API 对话提供常用服务商预设");
        foreach (var preset in AiServicePresets.Chat.Where(item => item.BaseUrl.Length > 0))
        {
            presets.SelectedValue = preset.Id;
            Check(chat.AiBaseUrlBox.Text == preset.BaseUrl, preset.Name + " 预填对话接口地址");
        }
        Check(requests == 0, "打开或切换服务商不会发送请求");
        var config = (ChatConfig)PrivateCall(chat, "AiConnectionConfig")!;
        Check(!config.HasKey && AiSecretStore.Load(keyFile) == TestApiKey, "API 服务商切换后不会向新接口发送旧密钥");
        chat.AiApiKeyBox.Password = TestApiKey; chat.AiModelBox.Text = "manual-provider-model";
        AwaitPlugin(chat.ConnectAiModelsAsync().ContinueWith(_ => true));
        Check(chat.AiModelBox.IsEditable && chat.TestAiModelButton.IsEnabled && chat.AiEnabledToggle.IsChecked != true,
            "模型列表不受支持时保留手动输入与测试，但不提前启用对话");
        AwaitPlugin(chat.TestAiModelAsync().ContinueWith(_ => true));
        Check(chat.AiConnectionStatus.Text.Contains("测试成功：manual-provider-model") && chat.AiEnabledToggle.IsChecked == true && requests == 2,
            "手动模型测试成功后才启用，模型列表失败不自动重复请求");
    }
}
