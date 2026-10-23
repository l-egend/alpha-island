using System.Globalization;
using System.IO;
using System.Speech.Recognition;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace alpha_island.Services;

internal interface IVoiceCapture : IDisposable
{
    TimeSpan FinishTimeout => TimeSpan.FromSeconds(4);
    event Action<string>? TranscriptChanged;
    event Action<float[]>? SpectrumChanged;
    event Action<bool>? VoiceActivity;
    event Action<string>? Failed;
    event Action? Completed;
    Task StartAsync(CancellationToken token);
    Task<string> FinishAsync(bool cancel, CancellationToken token);
}

/// <summary>Desktop SAPI dictation and shared microphone FFT. Audio stays on this computer.</summary>
internal sealed class VoiceRecognitionService : IVoiceCapture
{
    private readonly object _gate = new();
    private readonly StringBuilder _confirmed = new();
    private readonly MicrophoneSpectrum _spectrum = new();
    private readonly Stream? _dictationInput;
    private readonly TaskCompletionSource<bool> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SpeechRecognitionEngine? _recognizer;
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private Exception? _recognitionError;
    private string _partial = "";
    private volatile bool _disposed, _finishing, _uncertain;
    private const float MinimumConfidence = .55f;
    private const string UnclearSpeech = "没有听清这条指令，未发送给 API。请靠近麦克风重新说一次，尤其是时间和数值。";
    private long _lastFrame;
    public event Action<string>? TranscriptChanged;
    public event Action<float[]>? SpectrumChanged;
    public event Action<bool>? VoiceActivity;
    public event Action<string>? Failed;
    public event Action? Completed;

    public VoiceRecognitionService(Stream? dictationInput = null)
    {
        _dictationInput = dictationInput;
        _spectrum.Frame += frame =>
        {
            long now = Environment.TickCount64;
            if (now - _lastFrame < 40) return;
            _lastFrame = now; SpectrumChanged?.Invoke(frame);
        };
    }
    internal static SpeechRecognitionEngine CreateRecognizer(CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        var installed = SpeechRecognitionEngine.InstalledRecognizers();
        var info = installed.FirstOrDefault(item => item.Culture.Name.Equals(culture.Name, StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault(item => item.Culture.TwoLetterISOLanguageName == culture.TwoLetterISOLanguageName);
        if (info == null) throw new VoiceRecognitionException(new("缺少本机语音组件", "点击安装系统语音组件",
            $"没有找到 {culture.DisplayName} 的桌面语音识别引擎。请在 Windows 设置的语言选项中安装对应的“语音识别”组件，然后重新唤醒。不需要开启联机语音识别。", VoiceRecognitionProblem.SpeechSettings));
        var recognizer = new SpeechRecognitionEngine(info);
        try
        {
            recognizer.LoadGrammar(new DictationGrammar());
            if (DesktopSpeechGrammar.Create(info.Culture) is { } commands) recognizer.LoadGrammar(commands);
            recognizer.InitialSilenceTimeout = TimeSpan.FromSeconds(60);
            recognizer.EndSilenceTimeout = TimeSpan.FromMilliseconds(500);
            recognizer.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(800);
            return recognizer;
        }
        catch { recognizer.Dispose(); throw; }
    }
    public async Task StartAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var culture = CultureInfo.CurrentUICulture;
        try { await Task.Run(() => StartCapture(culture, token), token); }
        catch (OperationCanceledException) { Dispose(); throw; }
        catch (VoiceRecognitionException) { Dispose(); throw; }
        catch (Exception error)
        {
            Dispose();
            var problem = error.HResult is unchecked((int)0x80070005) or unchecked((int)0x80070490)
                ? new VoiceRecognitionProblem("麦克风暂不可用", "点击检查麦克风与权限",
                    "请连接并启用默认麦克风，并在 Windows 隐私设置中允许桌面应用访问麦克风。", VoiceRecognitionProblem.MicrophoneSettings)
                : new VoiceRecognitionProblem("语音启动失败", "点击检查系统语音设置",
                    $"本机语音识别未能启动（0x{error.HResult:X8}）。请确认已安装当前语言的语音组件，默认麦克风可用且未被其他应用独占。", VoiceRecognitionProblem.SpeechSettings);
            throw new VoiceRecognitionException(problem, error);
        }
    }
    private void StartCapture(CultureInfo culture, CancellationToken token)
    {
        SpeechRecognitionEngine? recognizer = null; WasapiCapture? capture = null; MMDevice? device = null;
        try
        {
            token.ThrowIfCancellationRequested(); recognizer = CreateRecognizer(culture);
            recognizer.SpeechHypothesized += OnHypothesis; recognizer.SpeechRecognized += OnResult;
            recognizer.SpeechRecognitionRejected += OnRejected;
            recognizer.RecognizeCompleted += OnCompleted;
            if (_dictationInput != null) recognizer.SetInputToWaveStream(_dictationInput);
            else
            {
                recognizer.SetInputToDefaultAudioDevice();
                using var devices = new MMDeviceEnumerator();
                device = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                if (device.AudioEndpointVolume.Mute) throw new VoiceRecognitionException(new("麦克风已静音", "点击检查麦克风静音状态",
                    "当前默认麦克风已在系统中静音。请取消麦克风静音后重新唤醒，程序不会自动替你打开麦克风。", "ms-settings:sound"));
                capture = new WasapiCapture(device, false, 40);
                capture.DataAvailable += OnAudio; capture.RecordingStopped += OnRecordingStopped;
            }
            token.ThrowIfCancellationRequested();
            if (_disposed) throw new OperationCanceledException(token);
            capture?.StartRecording(); recognizer.RecognizeAsync(RecognizeMode.Multiple);
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (_disposed) throw new OperationCanceledException(token);
                _recognizer = recognizer; _capture = capture; _device = device;
                recognizer = null; capture = null; device = null;
            }
        }
        finally { ReleaseNative(recognizer, capture, device); }
    }
    private void OnHypothesis(object? sender, SpeechHypothesizedEventArgs args)
    {
        string text;
        lock (_gate) { if (_disposed || _finishing || _uncertain) return; _partial = args.Result.Text; text = Text(); }
        TranscriptChanged?.Invoke(text);
    }
    private void OnResult(object? sender, SpeechRecognizedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Result.Text)) return;
        // Confidence is engine-specific. Reject doubtful dictation rather than auto-send a guessed command.
        if (args.Result.Confidence < MinimumConfidence) { RejectUnclear(); return; }
        string text;
        lock (_gate)
        {
            if (_disposed || _uncertain) return;
            if (_confirmed.Length > 0 && _confirmed[^1] < 128 && args.Result.Text[0] < 128) _confirmed.Append(' ');
            _confirmed.Append(args.Result.Text);
            if (_confirmed.Length > 4000) _confirmed.Length = 4000;
            _partial = ""; text = Text();
        }
        TranscriptChanged?.Invoke(text);
    }
    private void OnRejected(object? sender, SpeechRecognitionRejectedEventArgs args)
    {
        lock (_gate) if (_disposed || _partial.Length == 0 && string.IsNullOrWhiteSpace(args.Result?.Text)) return;
        RejectUnclear();
    }
    private void RejectUnclear()
    {
        lock (_gate) { if (_disposed || _uncertain) return; _uncertain = true; _confirmed.Clear(); _partial = ""; }
        Failed?.Invoke(UnclearSpeech);
    }
    private string Text() => (_confirmed.ToString() + _partial)[..Math.Min(4000, _confirmed.Length + _partial.Length)];
    private void OnCompleted(object? sender, RecognizeCompletedEventArgs args)
    {
        _recognitionError = args.Error; _ended.TrySetResult(true);
        if (_disposed || _finishing) return;
        if (args.Error == null) Completed?.Invoke();
        else Failed?.Invoke("本机语音识别中断，请检查默认麦克风后重新唤醒。");
    }
    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    { if (!_disposed && !_finishing && args.Exception != null) Failed?.Invoke("麦克风已断开或被独占，本次录音已停止。"); }
    private void OnAudio(object? sender, WaveInEventArgs args)
    {
        var format = (sender as WasapiCapture)?.WaveFormat;
        if (format == null || _disposed || _finishing) return;
        int bytes = format.BitsPerSample / 8, channels = format.Channels;
        bool floating = format.Encoding == WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible ext
            && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        double energy = 0; int samples = 0;
        for (int offset = 0; offset + bytes * channels <= args.BytesRecorded; offset += bytes * channels)
        {
            float value = 0;
            for (int channel = 0; channel < channels; channel++)
            {
                int position = offset + channel * bytes;
                float sample = floating && bytes == 4 ? BitConverter.ToSingle(args.Buffer, position)
                    : bytes == 2 ? BitConverter.ToInt16(args.Buffer, position) / 32768f
                    : bytes == 3 ? ((args.Buffer[position] | args.Buffer[position + 1] << 8 | args.Buffer[position + 2] << 16) << 8 >> 8) / 8388608f
                    : bytes == 4 ? BitConverter.ToInt32(args.Buffer, position) / 2147483648f : 0;
                if (float.IsFinite(sample)) value += Math.Clamp(sample, -1, 1);
            }
            value /= channels; energy += value * value; samples++;
            _spectrum.Push(value, format.SampleRate);
        }
        if (samples > 0) VoiceActivity?.Invoke(Math.Sqrt(energy / samples) >= .012);
    }
    public async Task<string> FinishAsync(bool cancel, CancellationToken token)
    {
        _finishing = true;
        var recognizer = _recognizer;
        if (recognizer != null && !_disposed)
        {
            if (!_ended.Task.IsCompleted)
            {
                try { if (cancel) recognizer.RecognizeAsyncCancel(); else recognizer.RecognizeAsyncStop(); }
                catch (InvalidOperationException) when (_ended.Task.IsCompleted) { }
            }
            await _ended.Task.WaitAsync(token);
            if (!cancel && _recognitionError != null) throw _recognitionError;
        }
        lock (_gate)
        {
            if (!cancel && (_uncertain || _partial.Length > 0)) throw new VoiceRecognitionException(VoiceRecognitionProblem.FromMessage(UnclearSpeech));
            return cancel ? "" : _confirmed.ToString().Trim();
        }
    }
    private void ReleaseNative(SpeechRecognitionEngine? recognizer, WasapiCapture? capture, MMDevice? device)
    {
        if (capture != null)
        {
            capture.DataAvailable -= OnAudio; capture.RecordingStopped -= OnRecordingStopped;
            try { capture.StopRecording(); } catch { }
            capture.Dispose();
        }
        device?.Dispose();
        if (recognizer != null)
        {
            recognizer.SpeechHypothesized -= OnHypothesis; recognizer.SpeechRecognized -= OnResult;
            recognizer.SpeechRecognitionRejected -= OnRejected;
            recognizer.RecognizeCompleted -= OnCompleted; recognizer.Dispose();
        }
    }
    public void Dispose()
    {
        SpeechRecognitionEngine? recognizer; WasapiCapture? capture; MMDevice? device;
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            recognizer = _recognizer; _recognizer = null; capture = _capture; _capture = null; device = _device; _device = null;
            _confirmed.Clear(); _partial = "";
        }
        ReleaseNative(recognizer, capture, device);
    }
}
