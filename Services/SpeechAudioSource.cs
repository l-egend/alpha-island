using AlphaIsland.Speech;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace alpha_island.Services;

internal interface IPcmAudioSource : IDisposable
{
    // Frame memory is reused after the event. Consumers process it synchronously.
    event Action<ReadOnlyMemory<short>>? Frame;
    event Action<string>? Failed;
    Task StartAsync(CancellationToken token);
    void Stop();
}

/// <summary>One WASAPI stream supplies recognition, energy detection and the microphone spectrum.</summary>
internal sealed class WasapiSpeechAudioSource(bool loopback = false) : IPcmAudioSource
{
    private readonly object _gate = new();
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private PcmSpeechConverter? _converter;
    private volatile bool _stopped, _disposed;
    public event Action<ReadOnlyMemory<short>>? Frame;
    public event Action<string>? Failed;
    public async Task StartAsync(CancellationToken token)
    {
        try
        {
            await Task.Run(() =>
            {
                WasapiCapture? capture = null; MMDevice? device = null;
                try
                {
                token.ThrowIfCancellationRequested();
                using var devices = new MMDeviceEnumerator();
                device = devices.GetDefaultAudioEndpoint(loopback ? DataFlow.Render : DataFlow.Capture, Role.Console);
                if (!loopback && device.AudioEndpointVolume.Mute)
                    throw new InvalidOperationException("当前默认麦克风已静音，请在 Windows 中取消静音后重试。");
                capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device, false, 40);
                var converter = new PcmSpeechConverter(capture.WaveFormat);
                capture.DataAvailable += OnAudio; capture.RecordingStopped += OnStopped;
                lock (_gate)
                {
                    token.ThrowIfCancellationRequested();
                    if (_disposed) throw new OperationCanceledException(token);
                    _converter = converter; capture.StartRecording();
                    _device = device; _capture = capture; device = null; capture = null;
                }
                }
                finally
                {
                    if (capture != null) { capture.DataAvailable -= OnAudio; capture.RecordingStopped -= OnStopped; capture.Dispose(); }
                    device?.Dispose();
                }
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { Dispose(); throw; }
        catch (InvalidOperationException) { Dispose(); throw; }
        catch { Dispose(); throw new InvalidOperationException(loopback ? "无法读取系统播放声音，请检查默认输出设备。" : "无法打开默认麦克风，请检查设备与桌面应用麦克风权限。"); }
    }
    private void OnAudio(object? sender, WaveInEventArgs args)
    {
        if (_stopped || _disposed || _converter == null) return;
        try { _converter.Convert(args.Buffer, args.BytesRecorded, frame => Frame?.Invoke(frame)); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (!_stopped && !_disposed) Failed?.Invoke("音频设备格式发生变化，请停止后重新启动识别。"); }
    }
    private void OnStopped(object? sender, StoppedEventArgs args)
    { if (!_stopped && !_disposed) Failed?.Invoke(loopback ? "系统音频设备已断开或切换，请重新启动语音识别。" : "麦克风已断开或被独占，本次录音已停止。"); }
    public void Stop()
    {
        _stopped = true;
        WasapiCapture? capture; lock (_gate) capture = _capture;
        try { capture?.StopRecording(); } catch (InvalidOperationException) { }
    }
    public void Dispose()
    {
        WasapiCapture? capture; MMDevice? device;
        lock (_gate) { if (_disposed) return; _disposed = _stopped = true; capture = _capture; device = _device; _capture = null; _device = null; }
        if (capture != null)
        {
            capture.DataAvailable -= OnAudio; capture.RecordingStopped -= OnStopped;
            try { capture.StopRecording(); } catch (InvalidOperationException) { }
            capture.Dispose();
        }
        device?.Dispose();
    }
}

internal sealed class PcmSpeechConverter
{
    private readonly WaveFormat _format;
    private readonly bool _floating;
    private readonly WdlResampler _resampler = new();
    private readonly float[] _output = new float[8192];
    private readonly short[] _pcm = new short[8192];
    private long _inputFrames, _outputFrames;
    internal PcmSpeechConverter(WaveFormat format)
    {
        _format = format;
        _floating = format.Encoding == WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible ext
            && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        if (format.SampleRate < 8000 || format.Channels < 1 || format.BitsPerSample is not (8 or 16 or 24 or 32))
            throw new InvalidOperationException("音频设备格式不受支持。");
        _resampler.SetMode(true, 2, false); _resampler.SetFeedMode(true); _resampler.SetRates(format.SampleRate, SpeechAudio.SampleRate);
    }
    internal void Convert(byte[] bytes, int count, Action<ReadOnlyMemory<short>> consume)
    {
        int stride = _format.BlockAlign, sampleBytes = _format.BitsPerSample / 8;
        for (int offset = 0; offset + stride <= count;)
        {
            int frames = Math.Min(2048, (count - offset) / stride);
            int available = _resampler.ResamplePrepare(frames, 1, out float[] input, out int start);
            frames = Math.Min(frames, available);
            for (int frame = 0; frame < frames; frame++, offset += stride)
            {
                float mono = 0;
                for (int channel = 0; channel < _format.Channels; channel++)
                {
                    int index = offset + channel * sampleBytes;
                    float value = _floating ? BitConverter.ToSingle(bytes, index) : sampleBytes switch
                    {
                        1 => (bytes[index] - 128) / 128f,
                        2 => BitConverter.ToInt16(bytes, index) / 32768f,
                        3 => ((bytes[index] | bytes[index + 1] << 8 | bytes[index + 2] << 16) << 8 >> 8) / 8388608f,
                        _ => BitConverter.ToInt32(bytes, index) / 2147483648f
                    };
                    if (float.IsFinite(value)) mono += Math.Clamp(value, -1, 1);
                }
                input[start + frame] = mono / _format.Channels;
            }
            _inputFrames += frames;
            // Limit cumulative duration: rounding every input packet up causes long-running caption drift.
            int requested = (int)Math.Min(_output.Length, _inputFrames * SpeechAudio.SampleRate / _format.SampleRate - _outputFrames);
            int written = _resampler.ResampleOut(_output, 0, frames, Math.Max(0, requested), 1);
            _outputFrames += written;
            for (int i = 0; i < written; i++) _pcm[i] = (short)Math.Clamp((int)Math.Round(_output[i] * 32767), short.MinValue, short.MaxValue);
            if (written > 0) consume(_pcm.AsMemory(0, written));
        }
    }
}
