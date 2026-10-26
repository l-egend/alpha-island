using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AlphaIsland.Speech;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;

internal static partial class Program
{
    private static void CaptionModesChecks(alpha_island.MainWindow main, string testRoot)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(main.Dispatcher));
        var source = new FakeSpeechAudioSource(); int picks = 0, translations = 0; bool? loopback = null;
        var speech = new SpeechRecognitionOptions("mimo", SpeechSettings.DefaultUrl, SpeechSettings.DefaultModel, TestApiKey);
        var tools = new ScreenToolsWindow(() => new ChatConfig("", "", "", "", .1, 1024),
            pickRegion: _ => { picks++; return null; }, speechConfiguration: () => speech,
            createSpeechCapture: (_, system) => { loopback = system; return new ProviderVoiceCapture(new EchoSpeechRecognizer("独立语音识别结果"), source, continuous: true); },
            translateCaption: (_, _, _, _) => { translations++; return Task.FromResult("不应调用翻译"); });
        try
        {
            Directory.CreateDirectory("bin/CaptionModes166/previews");
            tools.Show(); CompleteCaptionTask(tools.StartOrStopSpeechRecognitionAsync());
            Check(tools.SubtitlesRunning && source.Started && picks == 0,
                "语音识别不需要框选区域，也不依赖翻译接口配置");
            Check(loopback == true, "系统声音来源使用回环音频采集");
            tools.Show(); tools.Close(); PumpAnimations(40);
            Check(tools.SubtitlesRunning && !tools.IsVisible && !source.Disposed,
                "关闭工具设置窗口后录音会话继续运行");
            source.Push(Enumerable.Range(0, 16000).Select(i => (short)(Math.Sin(i * 2 * Math.PI * 400 / 16000) * 10000)).ToArray());
            source.Push(new short[8000]);
            PumpUntilState(() => AgentField<SubtitleIslandWindow?>(tools, "_subtitle")?.Translation.Contains("独立语音识别结果") == true);
            Check(translations == 0, "纯语音识别直接显示原文，不产生额外翻译请求");
            var subtitle = AgentField<SubtitleIslandWindow>(tools, "_subtitle");
            Check(subtitle.IsVisible && subtitle.Owner == null, "语音识别悬浮岛独立显示，不依赖工具设置窗口");
            SaveConversationPreview((FrameworkElement)subtitle.Content, "bin/CaptionModes166/previews/speech-island.png", (int)subtitle.ActualWidth, (int)subtitle.ActualHeight);
            tools.Show(); tools.UpdateLayout();
            var panel = (FrameworkElement)tools.Content;
            var help = Descendants<TextBlock>(panel).Single(b => b.Text.StartsWith("语音识别："));
            var helpBounds = help.TransformToAncestor(panel).TransformBounds(new Rect(help.RenderSize));
            Check(helpBounds.Bottom <= panel.ActualHeight + 1 && help.ActualHeight >= 36, "工具窗口的三行模式说明完整显示，没有被底部裁切");
            SaveThemePreview(panel, "bin/CaptionModes166/previews/tools-window.png"); tools.Close();
            var stop = Descendants<Button>((FrameworkElement)subtitle.Content).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "停止语音识别");
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpAnimations(30);
            Check(!tools.SubtitlesRunning && source.Disposed && !subtitle.IsVisible,
                "停止后释放音频设备并关闭悬浮岛");
            var options = AgentField<ComboBox>(tools, "_captionSource");
            Check(options.Items.Count == 2, "语音来源只包含系统声音和麦克风，不再混入 OCR 模式");
            source = new FakeSpeechAudioSource(); options.SelectedIndex = 1;
            CompleteCaptionTask(tools.StartOrStopSpeechRecognitionAsync());
            Check(tools.SpeechRecognitionRunning && loopback == false && picks == 0,
                "麦克风识别同样无需框选，并使用正确的音频输入");
            tools.StopSubtitles();
            RegionCaptionIndependenceChecks(speech);
            CaptionSwitchAndCancellationChecks(speech);
            CaptionStartupCancellationChecks(speech);
            CaptionCardEntryChecks(main, speech);
        }
        finally
        {
            tools.Shutdown();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static void CompleteCaptionTask(Task task)
    { AwaitPlugin(task.ContinueWith(_ => true)); task.GetAwaiter().GetResult(); }

    private static void RegionCaptionIndependenceChecks(SpeechRecognitionOptions speech)
    {
        int reads = 0, translations = 0, picks = 0; string text = "第一条屏幕文字";
        var selections = new List<System.Drawing.Rectangle>(); var languages = new List<string>();
        var region = new System.Drawing.Rectangle(-700, 200, 600, 140);
        var config = new ChatConfig("https://example.invalid/v1", "", "fixture", "", .1, 1024);
        var tools = new ScreenToolsWindow(() => config, pickRegion: _ => { picks++; return region; },
            speechConfiguration: () => throw new Exception("区域翻译不应读取语音配置"),
            createSpeechCapture: (_, _) => throw new Exception("区域翻译不应创建音频源"),
            readCaptionRegion: (selection, token) =>
            { selections.Add(selection); token.ThrowIfCancellationRequested(); reads++; return Task.FromResult(text); },
            translateCaption: (value, _, token, language) =>
            { token.ThrowIfCancellationRequested(); languages.Add(language); translations++; return Task.FromResult("译文：" + value); });
        try
        {
            tools.Show(); CompleteCaptionTask(tools.StartOrStopRegionTranslationAsync());
            Check(tools.RegionTranslationRunning && !tools.SpeechRecognitionRunning && picks == 1 && reads == 1 && translations == 1,
                "区域翻译无需语音模型或语音密钥，首次启动直接框选并翻译");
            var subtitle = AgentField<SubtitleIslandWindow>(tools, "_subtitle");
            Check(subtitle.Translation == "译文：第一条屏幕文字" && subtitle.Title.Contains("区域翻译"), "区域浮岛展示译文并标识当前模式");
            tools.Show(); tools.Close();
            AgentField<ComboBox>(tools, "_captionSource").SelectedIndex = 1;
            Check(tools.RegionTranslationRunning && subtitle.IsVisible && !tools.IsVisible, "关闭工具窗口或调整声音来源不会停止区域翻译");
            text = "第二条屏幕文字";
            PumpUntilState(() => subtitle.Translation == "译文：第二条屏幕文字");
            Check(reads >= 2 && subtitle.Translation == "译文：第二条屏幕文字", "工具窗口关闭后仍持续读取，译文不会堆积过期画面");
            CompleteCaptionTask((Task)PrivateCall(tools, "ReadAndTranslateAsync")!);
            Check(translations == 2, "画面文字不变时不会重复请求翻译");
            Check(selections.All(selection => selection == region), "OCR 只读取实际选择的区域，包括负坐标显示器");
            Check(languages.All(language => language == "zh-CN"), "区域翻译保留目标语言设置");
            SaveConversationPreview((FrameworkElement)subtitle.Content, "bin/CaptionModes166/previews/region-island.png", (int)subtitle.ActualWidth, (int)subtitle.ActualHeight);
            var stop = Descendants<Button>((FrameworkElement)subtitle.Content).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "停止区域翻译");
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!tools.SubtitlesRunning && !AgentField<DispatcherTimer>(tools, "_timer").IsEnabled && !subtitle.IsVisible,
                "浮岛停止按钮停止屏幕轮询并关闭译文");
        }
        finally { tools.Shutdown(); }
        int cancelledReads = 0;
        tools = new ScreenToolsWindow(() => config, pickRegion: _ => null, speechConfiguration: () => speech,
            readCaptionRegion: (_, _) => { cancelledReads++; return Task.FromResult("不应读取"); });
        try
        {
            CompleteCaptionTask(tools.StartOrStopRegionTranslationAsync());
            Check(!tools.SubtitlesRunning && cancelledReads == 0 && AgentField<SubtitleIslandWindow?>(tools, "_subtitle") == null,
                "取消框选不会开始区域翻译或留下浮岛");
        }
        finally { tools.Shutdown(); }
    }

    private static void CaptionSwitchAndCancellationChecks(SpeechRecognitionOptions speech)
    {
        var waiting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSpeechAudioSource(); int translations = 0;
        var tools = new ScreenToolsWindow(() => new ChatConfig("https://example.invalid/v1", "", "fixture", "", .1, 1024),
            pickRegion: _ => new System.Drawing.Rectangle(100, 300, 700, 120), speechConfiguration: () => speech,
            createSpeechCapture: (_, _) => new ProviderVoiceCapture(new EchoSpeechRecognizer("当前语音文字"), source, continuous: true),
            readCaptionRegion: (_, _) => waiting.Task,
            translateCaption: (_, _, _, _) => { translations++; return Task.FromResult("迟到的屏幕译文"); });
        try
        {
            tools.Show(); var oldStart = tools.StartOrStopRegionTranslationAsync();
            PumpUntilState(() => tools.RegionTranslationRunning);
            CompleteCaptionTask(tools.StartOrStopSpeechRecognitionAsync());
            var current = AgentField<SubtitleIslandWindow>(tools, "_subtitle");
            Check(tools.SpeechRecognitionRunning && source.Started && current.Title.Contains("语音识别"), "区域读取等待中也可切换到独立语音识别");
            waiting.SetResult("迟到的屏幕文字"); CompleteCaptionTask(oldStart); PumpAnimations(50);
            Check(translations == 0 && ReferenceEquals(current, AgentField<SubtitleIslandWindow?>(tools, "_subtitle")) && !tools.IsVisible,
                "旧区域结果不能污染新语音会话，也不能重新打开已关闭的工具窗口");
            tools.Shutdown(); PumpAnimations(30);
            Check(source.Disposed && !tools.SubtitlesRunning && !current.IsVisible, "退出应用强制结束后台会话，不会被关闭窗口的保活逻辑拦截");
        }
        finally { tools.Shutdown(); }
        waiting = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        tools = new ScreenToolsWindow(() => new ChatConfig("https://example.invalid/v1", "", "fixture", "", .1, 1024),
            pickRegion: _ => new System.Drawing.Rectangle(100, 300, 700, 120),
            readCaptionRegion: (_, _) => Task.FromResult("等待翻译的屏幕文字"), translateCaption: (_, _, _, _) => waiting.Task);
        try
        {
            var pending = tools.StartOrStopRegionTranslationAsync(); PumpUntilState(() => AgentField<bool>(tools, "_translatingCaption"));
            tools.StopSubtitles(); waiting.SetResult("迟到的翻译文字"); CompleteCaptionTask(pending); PumpAnimations(30);
            Check(!tools.SubtitlesRunning && AgentField<SubtitleIslandWindow?>(tools, "_subtitle") == null && !tools.IsVisible,
                "停止后忽略不响应取消的迟到翻译，保持窗口与浮岛关闭");
        }
        finally { tools.Shutdown(); }
    }

    private static void CaptionStartupCancellationChecks(SpeechRecognitionOptions speech)
    {
        var source = new DelayedCaptionAudioSource(); ProviderVoiceCapture? capture = null;
        var tools = new ScreenToolsWindow(() => new ChatConfig("", "", "", "", .1, 1024), speechConfiguration: () => speech,
            createSpeechCapture: (_, _) => capture = new ProviderVoiceCapture(new EchoSpeechRecognizer("迟到录音"), source, continuous: true));
        try
        {
            tools.Show(); var pending = tools.StartOrStopSpeechRecognitionAsync();
            Check(source.Started && !tools.SubtitlesRunning, "音频设备启动期间保留可取消状态");
            tools.Close(); source.Ready.SetResult(true); CompleteCaptionTask(pending); PumpAnimations(50);
            Check(source.Disposed && !tools.SubtitlesRunning && !tools.IsVisible && AgentField<SubtitleIslandWindow?>(tools, "_subtitle") == null,
                "启动设备时关闭窗口会取消录音，迟到启动不能重新出现");
            Check(capture != null && AgentField<object?>(capture, "_idleFlush") == null, "取消中的设备启动不会留下音频刷新计时器");
        }
        finally { tools.Shutdown(); }
    }

    private static void CaptionCardEntryChecks(alpha_island.MainWindow main, SpeechRecognitionOptions speech)
    {
        var previous = AgentField<ScreenToolsWindow?>(main, "_screenTools");
        var source = new FakeSpeechAudioSource(); int picks = 0;
        var tools = new ScreenToolsWindow(() => new ChatConfig("https://example.invalid/v1", "", "fixture", "", .1, 1024),
            speechConfiguration: () => speech, pickRegion: _ => { picks++; return new System.Drawing.Rectangle(150, 400, 700, 150); },
            createSpeechCapture: (_, _) => new ProviderVoiceCapture(new EchoSpeechRecognizer("入口语音"), source, continuous: true),
            readCaptionRegion: (_, _) => Task.FromResult("入口屏幕文字"), translateCaption: (_, _, _, _) => Task.FromResult("入口区域译文"));
        try
        {
            SetAgentField(main, "_screenTools", tools);
            PrivateCall(main, "SetState", IslandState.Card, true, "screen-tools"); PumpAnimations(300);
            var card = AgentField<Grid>(main, "_screenToolsCard");
            var speechButton = Descendants<Button>(card).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "语音识别");
            var regionButton = Descendants<Button>(card).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "区域翻译");
            Check(speechButton.IsVisible && regionButton.IsVisible, "屏幕工具卡片提供两个分别可见的入口");
            SaveThemePreview(main.IslandBody, "bin/CaptionModes166/previews/screen-tools-card.png");
            speechButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpUntilState(() => tools.SpeechRecognitionRunning);
            Check(!tools.IsVisible && picks == 0 && source.Started, "卡片入口可直接启动语音识别，无需打开设置或工具控制窗口");
            regionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntilState(() => tools.RegionTranslationRunning && AgentField<SubtitleIslandWindow?>(tools, "_subtitle")?.Translation == "入口区域译文");
            Check(picks == 1 && source.Disposed && !tools.IsVisible, "区域入口直接框选并切换模式，释放之前的音频设备");
        }
        finally { tools.Shutdown(); SetAgentField(main, "_screenTools", previous); }
    }

    private sealed class DelayedCaptionAudioSource : IPcmAudioSource
    {
        internal readonly TaskCompletionSource<bool> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Started, Disposed;
        public event Action<ReadOnlyMemory<short>>? Frame { add { } remove { } }
        public event Action<string>? Failed { add { } remove { } }
        public Task StartAsync(CancellationToken token) { Started = true; return Ready.Task; }
        public void Stop() { }
        public void Dispose() { Disposed = true; }
    }
}
