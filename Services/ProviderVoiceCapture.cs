using System.Text;
using System.Threading.Channels;
using AlphaIsland.Speech;

namespace alpha_island.Services;

/// <summary>Shared microphone/loopback pipeline for voice commands and continuous captions.</summary>
internal sealed class ProviderVoiceCapture : IVoiceCapture
{
    private readonly ISpeechRecognizer _recognizer;
    private readonly IPcmAudioSource _source;
    private readonly SpeechSegmenter _segmenter;
    private readonly MicrophoneSpectrum _spectrum = new();
    private readonly Channel<SpeechAudio> _pending = Channel.CreateBounded<SpeechAudio>(new BoundedChannelOptions(3) { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly object _transcriptGate = new();
    private readonly StringBuilder _confirmed = new();
    private readonly bool _continuous;
    private CancellationTokenRegistration _startCancellation;
    private Task? _worker;
    private volatile bool _disposed, _finishing, _failed;
    private long _lastFrame;
    private long _lastAudio;
    private System.Threading.Timer? _idleFlush;
    public TimeSpan FinishTimeout => TimeSpan.FromSeconds(40);
    public event Action<string>? TranscriptChanged;
    public event Action<float[]>? SpectrumChanged;
    public event Action<bool>? VoiceActivity;
    public event Action<string>? Failed;
    public event Action? Completed { add { } remove { } }
    internal event Action<string>? SegmentRecognized;
    internal ProviderVoiceCapture(ISpeechRecognizer recognizer, IPcmAudioSource source, bool continuous = false)
    {
        _recognizer = recognizer; _source = source; _continuous = continuous;
        _segmenter = new(continuous ? 4 : 10, continuous ? .4 : .55);
        _segmenter.Ready += audio => { if (!_pending.Writer.TryWrite(audio)) Fail("语音识别处理跟不上音频，已停止以避免积压。请检查网络后重试。"); };
        _source.Frame += OnFrame; _source.Failed += Fail;
        _spectrum.Frame += frame =>
        { long now = Environment.TickCount64; if (now - _lastFrame >= 40) { _lastFrame = now; SpectrumChanged?.Invoke(frame); } };
    }
    public async Task StartAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); _startCancellation = token.Register(_lifetime.Cancel);
        _worker = ProcessAsync(_lifetime.Token);
        await _source.StartAsync(token).ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed || _failed || token.IsCancellationRequested || _lifetime.IsCancellationRequested)
                throw new OperationCanceledException(token);
            _idleFlush = new(_ =>
            {
                if (_disposed || _finishing || _failed || Environment.TickCount64 - Interlocked.Read(ref _lastAudio) < 600) return;
                lock (_gate) { if (!_disposed && !_finishing && !_failed) _segmenter.Flush(); }
            }, null, 200, 200);
        }
    }
    private void OnFrame(ReadOnlyMemory<short> pcm)
    {
        if (_disposed || _finishing || _failed) return;
        lock (_gate)
        {
            if (_disposed || _finishing || _failed) return;
            Interlocked.Exchange(ref _lastAudio, Environment.TickCount64);
            bool active = _segmenter.Push(pcm.Span); VoiceActivity?.Invoke(active);
            if (!_continuous) foreach (short sample in pcm.Span) _spectrum.Push(sample / 32768f, SpeechAudio.SampleRate);
        }
    }
    private async Task ProcessAsync(CancellationToken token)
    {
        try
        {
            await foreach (var audio in _pending.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                string prefix; lock (_transcriptGate) prefix = _continuous ? "" : _confirmed.ToString();
                var partial = new ImmediateProgress(text =>
                { if (!_disposed && !_failed && !token.IsCancellationRequested) TranscriptChanged?.Invoke(Join(prefix, text)); });
                var result = await _recognizer.RecognizeAsync(audio, token, partial).ConfigureAwait(false);
                token.ThrowIfCancellationRequested(); if (_disposed || _failed) return;
                string text = result.Text.Trim(); if (text.Length == 0) continue;
                string confirmed;
                lock (_transcriptGate)
                {
                    if (_disposed || _failed || token.IsCancellationRequested) return;
                    if (!_continuous) { string joined = Join(prefix, text); _confirmed.Clear(); _confirmed.Append(joined); }
                    confirmed = _continuous ? text : _confirmed.ToString();
                }
                TranscriptChanged?.Invoke(confirmed); SegmentRecognized?.Invoke(text);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (OperationCanceledException) { Fail("语音识别请求超时，已停止，本次不会自动重试。"); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Fail(error is SpeechRecognitionException ? error.Message : "语音识别返回异常，已停止，本次不会自动重试。"); }
    }
    private static string Join(string prefix, string text)
    {
        string result = prefix.Length > 0 && text.Length > 0 ? prefix + " " + text : prefix + text;
        return result.Length > 4000 ? result[..4000] : result;
    }
    private void Fail(string message)
    {
        if (_disposed || _failed) return; _failed = true; _lifetime.Cancel();
        Failed?.Invoke(message);
    }
    public async Task<string> FinishAsync(bool cancel, CancellationToken token)
    {
        _finishing = true; _source.Stop();
        if (cancel) _lifetime.Cancel();
        lock (_gate) { if (!cancel && !_failed) _segmenter.Flush(); else _segmenter.Clear(); _pending.Writer.TryComplete(); }
        if (_worker != null) await _worker.WaitAsync(token).ConfigureAwait(false);
        if (!cancel && _failed) throw new SpeechRecognitionException("本次语音未完整识别，没有提交指令。请检查语音识别设置。");
        lock (_transcriptGate) return cancel ? "" : _confirmed.ToString().Trim();
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _lifetime.Cancel(); _pending.Writer.TryComplete();
        lock (_gate) { _idleFlush?.Dispose(); _idleFlush = null; }
        _source.Frame -= OnFrame; _source.Failed -= Fail; _source.Dispose(); _startCancellation.Dispose();
        lock (_gate) _segmenter.Clear();
        lock (_transcriptGate) _confirmed.Clear();
        // The in-flight worker observes cancellation before publishing any late result.
        _ = (_worker ?? Task.CompletedTask).ContinueWith(_ => _lifetime.Dispose(), TaskScheduler.Default);
    }
    private sealed class ImmediateProgress(Action<string> action) : IProgress<string>
    { public void Report(string value) => action(value); }
}
