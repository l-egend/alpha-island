using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Windows.Threading;
using AlphaIsland.PluginSdk;

namespace alpha_island.Services;

internal enum VoiceAssistantState { Idle, Starting, Recording, Finishing, Processing, Complete, Error }
internal sealed record VoiceAssistantSnapshot(VoiceAssistantState State, string Text, string Hint, VoiceRecognitionProblem? Problem = null);

/// <summary>Owns one explicit microphone session and one cancellable plugin request.</summary>
internal sealed class VoiceAssistantSession : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<IVoiceCapture> _createCapture;
    private readonly Func<string, string, Task<PluginTextInputResult>> _submit;
    private readonly Func<string, Task> _cancel;
    private readonly Func<bool> _available;
    private readonly Func<Task> _chime;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly DispatcherTimer _limit, _feedback, _silence;
    private readonly Func<long> _clock;
    private long _lastSpeech, _lastTranscript;
    private bool _heardAudio;
    private CancellationTokenSource? _session;
    private IVoiceCapture? _capture;
    private string _requestId = "";
    private int _generation;
    private bool _disposed;
    internal VoiceAssistantSnapshot Snapshot { get; private set; } = new(VoiceAssistantState.Idle, "", "");
    internal bool Visible => Snapshot.State != VoiceAssistantState.Idle;
    internal bool Active => Snapshot.State is VoiceAssistantState.Starting or VoiceAssistantState.Recording or VoiceAssistantState.Finishing or VoiceAssistantState.Processing;
    internal event Action<VoiceAssistantSnapshot>? Changed;
    internal event Action<float[]>? Spectrum;
    internal VoiceAssistantSession(Dispatcher dispatcher, Func<IVoiceCapture> createCapture,
        Func<string, string, Task<PluginTextInputResult>> submit, Func<string, Task> cancel,
        Func<bool> available, Func<Task> chime, Func<long>? clock = null)
    {
        _dispatcher = dispatcher; _createCapture = createCapture; _submit = submit; _cancel = cancel; _available = available; _chime = chime;
        _clock = clock ?? (() => Environment.TickCount64);
        _silence = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _silence.Tick += (_, _) => CheckSilence();
        _limit = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromSeconds(60) };
        _limit.Tick += (_, _) => { _limit.Stop(); _ = FinishAsync(); };
        _feedback = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(7) };
        _feedback.Tick += (_, _) => { _feedback.Stop(); if (!Active) Publish(VoiceAssistantState.Idle, "", ""); };
    }
    internal Task ToggleAsync() => Snapshot.State switch
    {
        VoiceAssistantState.Starting or VoiceAssistantState.Recording or VoiceAssistantState.Finishing or VoiceAssistantState.Processing => CancelAsync(),
        _ => WakeAsync()
    };
    internal async Task WakeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed || Active) return;
            if (!_available()) { Publish(VoiceAssistantState.Error, "", L.T("请先配置并启用 API 对话；锁屏、游戏或审批期间无法唤醒。")); return; }
            _feedback.Stop(); _generation++; _heardAudio = false; _lastSpeech = _lastTranscript = _clock();
            _requestId = Guid.NewGuid().ToString("N");
            _session?.Dispose(); _session = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var capture = _capture = _createCapture();
            capture.TranscriptChanged += text => Dispatch(() =>
            {
                if (!ReferenceEquals(_capture, capture) || Snapshot.State is not (VoiceAssistantState.Recording or VoiceAssistantState.Finishing)) return;
                _lastTranscript = _clock();
                if (!_heardAudio) Interlocked.Exchange(ref _lastSpeech, _lastTranscript);
                Publish(Snapshot.State, text, L.T("停顿约 1.6 秒自动发送 · 右侧按钮或 Esc 取消"));
            });
            capture.VoiceActivity += active =>
            {
                if (!active || !ReferenceEquals(_capture, capture) || Snapshot.State != VoiceAssistantState.Recording) return;
                _heardAudio = true; Interlocked.Exchange(ref _lastSpeech, _clock());
            };
            capture.SpectrumChanged += frame => { if (ReferenceEquals(_capture, capture)) Spectrum?.Invoke(frame); };
            capture.Failed += message => Dispatch(() => { if (ReferenceEquals(_capture, capture)) _ = FailCaptureAsync(message); });
            capture.Completed += () => Dispatch(() => { if (ReferenceEquals(_capture, capture) && Snapshot.State == VoiceAssistantState.Recording) _ = FinishAsync(); });
            Publish(VoiceAssistantState.Starting, "", L.T("正在准备麦克风与语音识别…"));
            await capture.StartAsync(_session.Token);
            _session.Token.ThrowIfCancellationRequested(); await _chime();
            _session.Token.ThrowIfCancellationRequested(); _session.CancelAfter(Timeout.InfiniteTimeSpan);
            Publish(VoiceAssistantState.Recording, "", L.T("请说出指令 · 停顿自动发送，右侧按钮取消")); _limit.Start(); _silence.Start();
        }
        catch (OperationCanceledException) { ReleaseCapture(); Publish(VoiceAssistantState.Idle, "", ""); }
        catch (Exception error)
        {
            ReleaseCapture();
            Publish(VoiceAssistantState.Error, "", error is InvalidOperationException ? error.Message : L.T("无法唤醒语音助手，请检查麦克风与语音设置。"),
                (error as VoiceRecognitionException)?.Problem);
        }
        finally { _gate.Release(); }
    }
    private void CheckSilence()
    {
        long now = _clock();
        if (Snapshot.State == VoiceAssistantState.Recording && !string.IsNullOrWhiteSpace(Snapshot.Text)
            && now - Interlocked.Read(ref _lastSpeech) >= 1600 && now - _lastTranscript >= 600)
        { _silence.Stop(); _ = FinishAsync(); }
    }
    internal async Task FinishAsync()
    {
        Task<PluginTextInputResult>? pending = null;
        int generation = 0;
        await _gate.WaitAsync();
        try
        {
            if (_disposed || Snapshot.State != VoiceAssistantState.Recording || _capture == null) return;
            _limit.Stop(); _silence.Stop(); Publish(VoiceAssistantState.Finishing, Snapshot.Text, L.T("正在结束录音…"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_session!.Token);
            timeout.CancelAfter(_capture.FinishTimeout);
            string text = await _capture.FinishAsync(false, timeout.Token);
            _session.Token.ThrowIfCancellationRequested(); ReleaseCapture();
            if (string.IsNullOrWhiteSpace(text)) { Publish(VoiceAssistantState.Error, "", L.T("没有识别到内容，未发送任何 API 请求。")); return; }
            generation = _generation;
            Publish(VoiceAssistantState.Processing, text, L.T("正在处理指令 · 点停止或按 Esc 中止"));
            pending = _submit(_requestId, text);
        }
        catch (OperationCanceledException) { ReleaseCapture(); Publish(VoiceAssistantState.Idle, "", ""); }
        catch (Exception error) { ReleaseCapture(); Publish(VoiceAssistantState.Error, Snapshot.Text, error is AlphaIsland.Speech.SpeechRecognitionException ? error.Message : L.T("语音提交失败，请重新唤醒；本次未自动重试。")); }
        finally { _gate.Release(); }
        if (pending == null) return;
        try
        {
            var result = await pending;
            if (!_disposed && generation == _generation && Snapshot.State == VoiceAssistantState.Processing)
                Publish(result.Accepted && !result.Failed ? VoiceAssistantState.Complete : VoiceAssistantState.Error,
                    Snapshot.Text, result.Message.Length > 0 ? result.Message : L.T("已完成，可打开 AI 对话查看。"));
        }
        catch (Exception)
        { if (!_disposed && generation == _generation) Publish(VoiceAssistantState.Error, Snapshot.Text, L.T("助手连接中断，没有自动重发指令。")); }
    }
    internal async Task CancelAsync()
    {
        if (_disposed) return;
        _session?.Cancel();
        string? cancelId = null;
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            if (Snapshot.State == VoiceAssistantState.Processing) cancelId = _requestId;
            _generation++; _limit.Stop(); _silence.Stop(); _feedback.Stop();
            if (_capture != null)
            {
                try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await _capture.FinishAsync(true, timeout.Token); }
                catch (Exception) { }
            }
            ReleaseCapture(); Publish(VoiceAssistantState.Idle, "", "");
        }
        finally { _gate.Release(); }
        if (cancelId != null) { try { await _cancel(cancelId).WaitAsync(TimeSpan.FromSeconds(4)); } catch (Exception) { } }
    }
    private async Task FailCaptureAsync(string message)
    {
        string text = Snapshot.Text;
        await CancelAsync();
        if (!_disposed && Snapshot.State == VoiceAssistantState.Idle) Publish(VoiceAssistantState.Error, text, message);
    }
    internal void HoldFeedback() { if (!Active) _feedback.Stop(); }
    private void Publish(VoiceAssistantState state, string text, string hint, VoiceRecognitionProblem? problem = null)
    {
        if (_disposed) return;
        Snapshot = new(state, text, hint, state == VoiceAssistantState.Error ? problem ?? VoiceRecognitionProblem.FromMessage(hint) : null);
        Changed?.Invoke(Snapshot);
        if (state is VoiceAssistantState.Complete or VoiceAssistantState.Error)
        { _feedback.Interval = TimeSpan.FromSeconds(state == VoiceAssistantState.Error ? 20 : 7); _feedback.Start(); }
    }
    private void Dispatch(Action action)
    { if (!_disposed && !_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(action); }
    private void ReleaseCapture() { _silence.Stop(); var capture = _capture; _capture = null; capture?.Dispose(); }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _generation++; _limit.Stop(); _silence.Stop(); _feedback.Stop();
        _session?.Cancel(); ReleaseCapture(); _session?.Dispose();
        // A submitted request has its own ID; never cancel an unrelated typed turn.
        if (Snapshot.State == VoiceAssistantState.Processing) _ = _cancel(_requestId);
    }
}
