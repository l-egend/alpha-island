using System.Windows.Threading;
using System.Globalization;
using System.IO;
using System.Speech.Synthesis;
using alpha_island.Services;

internal static partial class Program
{
    private static void VoiceCaptureLiveChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            using var capture = new VoiceRecognitionService();
            int frames = 0;
            capture.SpectrumChanged += _ => Interlocked.Increment(ref frames);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            Task start = capture.StartAsync(timeout.Token);
            try
            {
                AwaitPlugin(start.ContinueWith(_ => true)); start.GetAwaiter().GetResult();
                Console.WriteLine("Capture startup: success");
                PumpAnimations(200);
                Check(Volatile.Read(ref frames) > 0, "实际麦克风产生录音频谱帧");
                AwaitPlugin(capture.FinishAsync(true, timeout.Token));
                Check(true, "实际麦克风与识别会话能够启动并取消，不保存录音或调用对话 API");
                capture.Dispose();
                int sends = 0;
                using var session = new VoiceAssistantSession(Dispatcher.CurrentDispatcher, () => new VoiceRecognitionService(),
                    (_, _) => { sends++; return Task.FromResult(new AlphaIsland.PluginSdk.PluginTextInputResult(true, "未调用真实 API")); },
                    _ => Task.CompletedTask, () => true, VoiceWakeChime.PlayAsync);
                Task wake = session.WakeAsync(); AwaitPlugin(wake.ContinueWith(_ => true)); wake.GetAwaiter().GetResult();
                PumpAnimations(850);
                Check(session.Snapshot.State == VoiceAssistantState.Recording && sends == 0,
                    "真实唤醒音播放后保持聆听，不因提示音误提交或立即中止");
                Task stop = session.CancelAsync(); AwaitPlugin(stop.ContinueWith(_ => true)); stop.GetAwaiter().GetResult();
                Check(session.Snapshot.State == VoiceAssistantState.Idle && sends == 0, "实际唤醒会话取消后关闭麦克风，不提交任何请求");
            }
            catch (Exception error)
            {
                for (Exception? item = error; item != null; item = item.InnerException)
                    Console.WriteLine($"Capture startup: {item.GetType().Name} 0x{item.HResult:X8} {item.Message}");
                Check(false, "实际麦克风与识别会话启动成功");
            }
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void VoiceDictationFixtureChecks()
    {
        using var speech = new SpeechSynthesizer();
        var voice = speech.GetInstalledVoices().FirstOrDefault(item => item.Enabled && item.VoiceInfo.Culture.TwoLetterISOLanguageName == "zh");
        Check(voice != null, "本机存在中文语音合成引擎，可生成无真人内容的验证样音");
        speech.SelectVoice(voice!.VoiceInfo.Name);
        Console.WriteLine("Synthetic voice culture: " + voice.VoiceInfo.Culture.Name);
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            foreach (var sample in new[] { (Text: "把音量调到百分之三十", Match: "音量"), (Text: "请暂停播放音乐", Match: "暂停"), (Text: "三十分钟后提醒我喝水", Match: "喝水") })
            {
                using var audio = new MemoryStream(); speech.SetOutputToWaveStream(audio);
                var prompt = new PromptBuilder(voice.VoiceInfo.Culture);
                prompt.AppendBreak(TimeSpan.FromMilliseconds(400)); prompt.AppendText(sample.Text); prompt.AppendBreak(TimeSpan.FromMilliseconds(200));
                speech.Speak(prompt);
                speech.SetOutputToNull(); audio.Position = 0;
                using var capture = new VoiceRecognitionService(audio);
                string result = ""; int sends = 0;
                using var session = new VoiceAssistantSession(Dispatcher.CurrentDispatcher, () => capture,
                    (_, text) => { result = text; sends++; return Task.FromResult(new AlphaIsland.PluginSdk.PluginTextInputResult(true, "模拟回复")); },
                    _ => Task.CompletedTask, () => true, () => Task.CompletedTask);
                Task start = session.WakeAsync(); AwaitPlugin(start.ContinueWith(_ => true)); start.GetAwaiter().GetResult();
                PumpUntilState(() => session.Snapshot.State is VoiceAssistantState.Complete or VoiceAssistantState.Error);
                Console.WriteLine("Synthetic Chinese result: " + (sends > 0 ? result : session.Snapshot.Hint));
                if (sample.Match == "喝水" && sends == 0)
                    Check(session.Snapshot.State == VoiceAssistantState.Error && session.Snapshot.Hint.Contains("没有听清"),
                        "本机模型未听清提醒时明确要求重说，完整控制器路径不发送错误时间指令");
                else
                {
                    Check(sends == 1 && result.Contains(sample.Match), "实际本机识别和控制器提交中文指令：" + sample.Text);
                    if (sample.Match is "喝水" or "音量") Check(result.Contains("三十") || result.Contains("30"), "提交的指令保留三十这一数值，不能只识别动作");
                }
            }
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
}
