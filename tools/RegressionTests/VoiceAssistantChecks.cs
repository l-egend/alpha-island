using System.Windows.Input;
using System.Windows.Threading;
using AlphaIsland.PluginSdk;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void VoiceSessionChecks()
    {
        var defaults = new IslandOptions();
        SettingsRegistry.Normalize(defaults);
        Check(SettingsRegistry.GetText(defaults, "VoiceWakeShortcut", "") == "Ctrl+Alt+V"
            && SettingsRegistry.GetText(defaults, "VoiceWakeShortcut", "") != SettingsRegistry.GetText(defaults, "LauncherShortcut", ""),
            "语音默认快捷键与现有快速启动器互不冲突");
        var gesture = new VoiceWakeGesture();
        gesture.KeyChanged(Key.RightCtrl, true, ModifierKeys.Control, 0);
        Check(!gesture.Tick(599) && gesture.Tick(600) && !gesture.Tick(2000), "右 Ctrl 长按 600ms 只唤醒一次，重复键不反复触发");
        gesture.KeyChanged(Key.RightCtrl, false, ModifierKeys.None, 2100);
        gesture.KeyChanged(Key.RightCtrl, true, ModifierKeys.Control, 2200);
        gesture.KeyChanged(Key.C, true, ModifierKeys.Control, 2250);
        gesture.KeyChanged(Key.RightCtrl, true, ModifierKeys.Control, 2300);
        Check(!gesture.Tick(4000), "Ctrl 组合键及其长按重复不会被误识别为语音唤醒");
        gesture.Reset(); gesture.KeyChanged(Key.RightCtrl, true, ModifierKeys.Control, 0, injected: true);
        Check(!gesture.Tick(1000), "程序注入的按键不能启动麦克风");
        gesture.KeyChanged(Key.RightCtrl, true, ModifierKeys.Control | ModifierKeys.Alt, 0);
        Check(!gesture.Tick(1000), "已有其他修饰键按下时不触发单键长按");

        float[] SpectrumAt(int frequency)
        {
            var spectrum = new MicrophoneSpectrum(); float[] result = [];
            spectrum.Frame += frame => result = frame.ToArray();
            for (int i = 0; i < 8192; i++) spectrum.Push((float)Math.Sin(2 * Math.PI * frequency * i / 48000) * .4f, 48000);
            Check(result.Length == 16 && result.All(x => float.IsFinite(x) && x is >= 0 and <= 1), "麦克风 FFT 输出有界的真实 16 段频谱");
            return result;
        }
        var low = SpectrumAt(180); var high = SpectrumAt(2000);
        Check(Array.IndexOf(high, high.Max()) - Array.IndexOf(low, low.Max()) >= 5, "录音频谱能区分低频与高频输入，未复用系统播放动画");

        long now = 0; int created = 0, sent = 0, chimes = 0; string sentText = "", sentId = "", cancelledId = "";
        FakeVoiceCapture? capture = null;
        var reply = new TaskCompletionSource<PluginTextInputResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var voice = new VoiceAssistantSession(Dispatcher.CurrentDispatcher,
            () => { created++; return capture = new FakeVoiceCapture(); },
            (id, text) => { Check(capture!.Disposed, "调用 API 前麦克风及录音频谱资源已经释放"); sent++; sentId = id; sentText = text; return reply.Task; },
            id => { cancelledId = id; return Task.CompletedTask; }, () => true,
            () => { Check(capture?.Started == true, "识别器就绪后才播放唤醒音，提示音不会诱使用户过早开口"); chimes++; return Task.CompletedTask; }, () => now);
        void Complete(Task task) { AwaitPlugin(task.ContinueWith(_ => true)); task.GetAwaiter().GetResult(); }
        Check(created == 0, "语音助手待机时不创建麦克风或识别器");
        Complete(voice.WakeAsync()); Complete(voice.WakeAsync());
        Check(created == 1 && chimes == 1 && voice.Snapshot.State == VoiceAssistantState.Recording, "连续唤醒只建立一个录音会话并播放一次提示音");
        capture!.Say("把音量调到百分之三十"); PumpAnimations(10);
        now = 1500; PrivateCall(voice, "CheckSilence");
        Check(sent == 0 && voice.Snapshot.Text.Contains("百分之三十"), "说话文字实时显示，短暂停顿不会提前发送");
        capture.Activity(); now = 1700; PrivateCall(voice, "CheckSilence");
        Check(sent == 0, "继续说话会延后自动发送，避免在一句话中间截断");
        now = 3101; PrivateCall(voice, "CheckSilence"); PumpUntilState(() => sent == 1);
        PrivateCall(voice, "CheckSilence");
        Check(sentText == "把音量调到百分之三十" && sent == 1 && voice.Snapshot.State == VoiceAssistantState.Processing,
            "约 1.6 秒静音后自动发送且只提交一次");
        Complete(voice.CancelAsync()); reply.SetResult(new(true, "迟到的回复")); PumpAnimations(35);
        Check(cancelledId == sentId && voice.Snapshot.State == VoiceAssistantState.Idle, "处理中取消只针对本次请求，迟到回复不能重新弹出小岛");
        Complete(voice.WakeAsync()); capture!.Say("不应发送"); PumpAnimations(10); Complete(voice.ToggleAsync());
        now += 5000; PrivateCall(voice, "CheckSilence");
        Check(sent == 1 && capture.Disposed && capture.Cancelled, "再次按唤醒键取消录音，已识别的文字也不自动发送");
        Complete(voice.WakeAsync()); Complete(voice.FinishAsync());
        Check(sent == 1 && capture!.Disposed && voice.Snapshot.State == VoiceAssistantState.Error, "空录音关闭麦克风并提示，不消耗 API 请求");
        Complete(voice.WakeAsync()); capture!.Error("麦克风断开"); PumpUntilState(() => voice.Snapshot.State == VoiceAssistantState.Error);
        Check(capture.Disposed && sent == 1, "麦克风中断会释放资源并提示，未识别内容不会提交");
        var starting = new FakeVoiceCapture { StartGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var slow = new VoiceAssistantSession(Dispatcher.CurrentDispatcher, () => starting,
            (_, _) => throw new Exception("不应调用 API"), _ => Task.CompletedTask, () => true, () => Task.CompletedTask);
        Task start = slow.WakeAsync(); Complete(slow.CancelAsync()); Complete(start);
        Check(starting.Disposed && slow.Snapshot.State == VoiceAssistantState.Idle, "识别器初始化期间也能取消，不遗留麦克风或稍后重新弹窗");
        int unavailableCapture = 0;
        using var unavailable = new VoiceAssistantSession(Dispatcher.CurrentDispatcher, () => { unavailableCapture++; return new FakeVoiceCapture(); },
            (_, _) => throw new Exception("不应调用 API"), _ => Task.CompletedTask, () => false, () => Task.CompletedTask);
        Complete(unavailable.WakeAsync());
        Check(unavailableCapture == 0 && unavailable.Snapshot.State == VoiceAssistantState.Error, "API 不可用、锁屏或游戏门禁拒绝唤醒时不打开麦克风");
    }

    private sealed class FakeVoiceCapture : IVoiceCapture
    {
        public event Action<string>? TranscriptChanged;
        public event Action<float[]>? SpectrumChanged;
        public event Action<bool>? VoiceActivity;
        public event Action<string>? Failed;
        public event Action? Completed;
        internal bool Disposed, Cancelled, Started;
        internal TaskCompletionSource<bool>? StartGate;
        internal string Text = "";
        internal void Say(string text) { Text = text; TranscriptChanged?.Invoke(text); }
        internal void Activity() => VoiceActivity?.Invoke(true);
        internal void Frame(float[] values) => SpectrumChanged?.Invoke(values);
        internal void Error(string message) => Failed?.Invoke(message);
        internal void End() => Completed?.Invoke();
        public async Task StartAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (StartGate != null) await StartGate.Task.WaitAsync(token); Started = true; }
        public Task<string> FinishAsync(bool cancel, CancellationToken token) { Cancelled = cancel; return Task.FromResult(cancel ? "" : Text); }
        public void Dispose() => Disposed = true;
    }
}
