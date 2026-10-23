using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.IO;
using AlphaIsland.Speech;
using alpha_island.Controls;
using alpha_island.Services;
using alpha_island.Core;
using Button = System.Windows.Controls.Button;

internal static partial class Program
{
    private static void SpeechCaptionUiChecks(alpha_island.MainWindow main, string root)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(main.Dispatcher));
        try
        {
        int picks = 0;
        var tools = new ScreenToolsWindow(() => new ChatConfig("https://example.invalid/v1", "", "test", "", .1, 1024),
            pickRegion: _ => { picks++; return null; });
        try
        {
            tools.Show();
            var button = Descendants<Button>((FrameworkElement)tools.Content).Single(b => b.Content as string == "开始字幕");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpAnimations(300);
            Check(picks == 1, "首次点击实时字幕直接进入区域框选，无需先执行一次 OCR");
        }
        finally { tools.Close(); }
        SpeechCaptionFlowChecks(main, root); SpeechSettingsUiChecks(root); ScreenRegionGestureChecks();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void SpeechCaptionFlowChecks(alpha_island.MainWindow main, string root)
    {
        var source = new FakeSpeechAudioSource(); int captures = 0, translations = 0, transitions = 0; bool loopback = false;
        var config = new ChatConfig("https://example.invalid/v1", "", "test", "", .1, 1024);
        var speech = new SpeechRecognitionOptions("mimo", SpeechSettings.DefaultUrl, SpeechSettings.DefaultModel, TestApiKey);
        var tools = new ScreenToolsWindow(() => config,
            pickRegion: _ => new System.Drawing.Rectangle(200, 350, 800, 140), speechConfiguration: () => speech,
            subtitleStateChanged: active => { transitions++; if (active) PrivateCall(main, "CollapseToBar", true); },
            createSpeechCapture: (_, system) => { captures++; loopback = system; return new ProviderVoiceCapture(new EchoSpeechRecognizer("原始英文字幕"), source, continuous: true); },
            translateCaption: (text, _, token, language) =>
            { token.ThrowIfCancellationRequested(); translations++; Check(language == "zh-CN" && text == "原始英文字幕", "音频字幕将识别文字发送给指定目标语言的翻译流程"); return Task.FromResult("这是翻译后的字幕，主岛收起后也可以继续显示更多内容。"); });
        void Complete(Task task) { AwaitPlugin(task.ContinueWith(_ => true)); task.GetAwaiter().GetResult(); }
        try
        {
            tools.Show(); Complete(tools.StartOrStopSubtitlesAsync());
            Check(tools.SubtitlesRunning && source.Started && captures == 1 && loopback && !tools.IsVisible,
                "首次字幕框选完成后只启动一个系统声音采集，并隐藏工具窗口");
            Check(AgentField<IslandState>(main, "_state") == IslandState.Bar, "启用字幕会收起上方主岛，字幕不占用主岛常驻图层");
            var pcm = Enumerable.Range(0, 16000).Select(i => (short)(Math.Sin(i * 2 * Math.PI * 400 / 16000) * 10000)).ToArray();
            source.Push(pcm); source.Push(new short[8000]);
            PumpUntilState(() => AgentField<SubtitleIslandWindow?>(tools, "_subtitle")?.IsVisible == true);
            var subtitle = AgentField<SubtitleIslandWindow>(tools, "_subtitle");
            Check(translations == 1 && subtitle.Translation.Contains("翻译后的字幕") && !subtitle.Translation.Contains("原始英文"),
                "下方独立翻译岛只显示译文，原文不会占用字幕空间");
            Check(subtitle.WindowStyle == WindowStyle.None && subtitle.AllowsTransparency && subtitle.Topmost && !subtitle.ShowActivated,
                "翻译岛无窗口边框、圆角透明且不抢输入焦点");
            PrivateCall(main, "CollapseToBar", true); PumpAnimations(100);
            Check(subtitle.IsVisible && subtitle.ActualWidth >= 360 && subtitle.ActualHeight > 40,
                "主岛最小化后译文继续可见，字幕区域可以多行自适应");
            subtitle.UpdateText("这是第一行翻译字幕，保留完整句子和标点。\n这是第二行翻译字幕，原文不会占用翻译岛的显示空间。\n第三行也可以正常显示，字幕与上方主岛独立。");
            subtitle.UpdateLayout(); PumpAnimations(200);
            Check(subtitle.ActualHeight > 100, "多行译文会增加字幕岛高度，不会挤在固定的一行里");
            SaveConversationPreview((FrameworkElement)subtitle.Content, "docs/screens/mimo-subtitle-island.png", (int)subtitle.ActualWidth, (int)subtitle.ActualHeight);
            var stop = subtitle.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Header as string == "停止字幕");
            stop.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); PumpAnimations(30);
            Check(!tools.SubtitlesRunning && source.Disposed && !subtitle.IsVisible && transitions == 2,
                "翻译岛右键停止释放音频源并关闭字幕，没有隐藏后台录音");
        }
        finally { tools.Close(); }

        source = new FakeSpeechAudioSource();
        var waiting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        tools = new ScreenToolsWindow(() => config, pickRegion: _ => new System.Drawing.Rectangle(200, 350, 800, 140), speechConfiguration: () => speech,
            createSpeechCapture: (_, _) => new ProviderVoiceCapture(new EchoSpeechRecognizer("第二条"), source, continuous: true),
            translateCaption: async (_, _, token, _) => await waiting.Task.WaitAsync(token));
        try
        {
            Complete(tools.StartOrStopSubtitlesAsync()); source.Push(Enumerable.Repeat((short)5000, 16000).ToArray()); source.Push(new short[8000]);
            PumpUntilState(() => AgentField<bool>(tools, "_translatingCaption"));
            tools.StopSubtitles(); waiting.SetResult("迟到的译文"); PumpAnimations(100);
            Check(source.Disposed && AgentField<SubtitleIslandWindow?>(tools, "_subtitle") == null && !tools.SubtitlesRunning,
                "停止时取消正在等待的翻译，迟到结果不能重新弹出字幕岛");
        }
        finally { tools.Close(); }
    }
    private static void ScreenRegionGestureChecks()
    {
        var cursor = new System.Drawing.Point(-300, 400);
        using var picker = new ScreenRegionPicker(() => cursor);
        picker.CreateControl();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var down = typeof(System.Windows.Forms.Control).GetMethod("OnMouseDown", flags)!;
        var up = typeof(System.Windows.Forms.Control).GetMethod("OnMouseUp", flags)!;
        down.Invoke(picker, new object[] { new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, 10, 10, 0) });
        cursor = new(-100, 650);
        up.Invoke(picker, new object[] { new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, 210, 260, 0) });
        Check(picker.SelectedRegion == new System.Drawing.Rectangle(-300, 400, 200, 250),
            "快速框选即使没有中间 MouseMove 也使用松手坐标，多屏负坐标保持正确");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Check(ScreenRegionPicker.Pick(cancelled.Token) == null, "取消后的框选不会遗留拦截鼠标的窗口");
    }
    private static void SpeechSettingsUiChecks(string root)
    {
        var settings = new alpha_island.Settings.SettingsWindow(false) { SpeechSecretRoot = root };
        try
        {
            var options = new IslandOptions(); settings.LoadFrom(options); settings.OpenSpeechSettings();
            var provider = (System.Windows.Controls.ComboBox)settings.FindName("SpeechProviderBox");
            Check(provider.SelectedValue as string == "mimo" && provider.Items.Count >= 5,
                "设置默认选中 MiMo，并提供可替换协议、Windows 和本地服务入口");
            var password = (PasswordBox)settings.FindName("SpeechApiKeyBox");
            password.Password = TestApiKey;
            var save = (Button)settings.FindName("SpeechSaveKeyButton");
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(AiSecretStore.Load(SpeechSettings.SecretPath(root)) == TestApiKey && password.Password.Length == 0,
                "语音密钥独立 DPAPI 加密保存且保存后清空输入框");
            Check(!File.ReadAllText(SpeechSettings.SecretPath(root)).Contains(TestApiKey) && !options.Extras.Any(pair => pair.Value.ToString().Contains(TestApiKey)),
                "语音密钥不以明文进入配置或加密文件");
            var clear = (Button)settings.FindName("SpeechClearKeyButton");
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!File.Exists(SpeechSettings.SecretPath(root)), "清除语音密钥不依赖或删除对话 API 的密钥");
        }
        finally { settings.ForceClose = true; settings.Close(); }
    }
}
