using System.Buffers.Binary;

namespace AlphaIsland.Speech;

/// <summary>One independent mono PCM16 WAV utterance. Recognizers never own a microphone or retain history.</summary>
public sealed record SpeechAudio(ReadOnlyMemory<byte> Wave)
{
    public const int SampleRate = 16000;
    public static SpeechAudio FromPcm(ReadOnlySpan<short> pcm)
    {
        if (pcm.Length > SampleRate * 120) throw new ArgumentException("单段音频不能超过 120 秒。");
        var data = new byte[44 + pcm.Length * 2];
        "RIFF"u8.CopyTo(data); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), data.Length - 8);
        "WAVEfmt "u8.CopyTo(data.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(34), 16);
        "data"u8.CopyTo(data.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40), pcm.Length * 2);
        for (int i = 0; i < pcm.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(44 + i * 2), pcm[i]);
        return new(data);
    }
}

public sealed record SpeechRecognitionOptions(string Provider, string BaseUrl, string Model, string ApiKey, string Language = "auto")
{
    public override string ToString() => $"SpeechRecognitionOptions {{ Provider = {Provider}, Model = {Model}, ApiKey = [hidden] }}";
    public Uri Endpoint(string suffix)
    {
        string value = BaseUrl.Trim().TrimEnd('/');
        if (!value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) value += suffix;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            throw new SpeechRecognitionException("识别接口地址无效。云端请使用 HTTPS；本地服务可使用 http://localhost。");
        if (string.IsNullOrWhiteSpace(Model)) throw new SpeechRecognitionException("请填写语音识别模型名称。");
        return uri;
    }
}

public sealed record SpeechRecognitionResult(string Text);

/// <summary>Adapter seam for cloud and local recognizers. Partial text replaces the current utterance, never appends it.
/// Cancellation must stop the request; implementations must not retry paid requests automatically.</summary>
public interface ISpeechRecognizer
{
    Task<SpeechRecognitionResult> RecognizeAsync(SpeechAudio audio, CancellationToken token, IProgress<string>? partial = null);
}

public sealed class SpeechRecognitionException(string message) : InvalidOperationException(message);

/// <summary>Register a local engine or a provider-specific protocol without changing voice/caption callers.</summary>
public sealed class SpeechRecognizerRegistry
{
    public static SpeechRecognizerRegistry Default { get; } = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Func<SpeechRecognitionOptions, ISpeechRecognizer>> _factories = new(StringComparer.OrdinalIgnoreCase);
    public SpeechRecognizerRegistry(HttpClient? http = null)
    {
        Register("mimo", options => new ChatAudioSpeechRecognizer(options, http, mimo: true));
        Register("chat-audio", options => new ChatAudioSpeechRecognizer(options, http, mimo: false));
        Register("transcription", options => new TranscriptionSpeechRecognizer(options, http));
        Register("local-endpoint", options => new TranscriptionSpeechRecognizer(options, http));
    }
    public void Register(string id, Func<SpeechRecognitionOptions, ISpeechRecognizer> factory)
    { ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentNullException.ThrowIfNull(factory); lock (_gate) _factories[id] = factory; }
    public ISpeechRecognizer Create(SpeechRecognitionOptions options)
    {
        Func<SpeechRecognitionOptions, ISpeechRecognizer>? factory;
        lock (_gate) _factories.TryGetValue(options.Provider, out factory);
        return factory != null ? factory(options) : throw new SpeechRecognitionException("尚未安装所选语音识别适配器，请在智能助手设置中选择可用的识别引擎。");
    }
}
