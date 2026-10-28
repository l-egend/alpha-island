using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AlphaIsland.Speech;

internal static class SpeechHttp
{
    internal static readonly HttpClient Client = new(new SocketsHttpHandler
    { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    { Timeout = Timeout.InfiniteTimeSpan };
    internal static void Validate(SpeechAudio audio)
    {
        if (audio.Wave.Length < 46 || audio.Wave.Length > 7_000_000 || !audio.Wave.Span[..4].SequenceEqual("RIFF"u8))
            throw new SpeechRecognitionException("语音样本为空、过大或不是 WAV 格式。");
    }
    internal static void Authenticate(HttpRequestMessage request, SpeechRecognitionOptions options, bool mimo = false)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            if (options.Provider != "local-endpoint") throw new SpeechRecognitionException("请先在设置 → 智能助手 → 语音识别中保存 API Key。");
            return;
        }
        if (mimo) request.Headers.Add("api-key", options.ApiKey);
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
    }
    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken token)
    {
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
        catch (HttpRequestException) { throw new SpeechRecognitionException("无法连接语音识别服务，请检查网络与识别接口地址。"); }
        if (response.IsSuccessStatusCode) return response;
        int status = (int)response.StatusCode; response.Dispose();
        // Never surface a remote response body: it can echo audio, credentials or private transcripts.
        throw new SpeechRecognitionException(status switch
        {
            401 or 403 => "语音识别鉴权失败，请检查 API Key、模型权限及账户余额。",
            429 => "语音识别额度不足或请求过于频繁。已停止，本次不会自动重试。",
            413 => "语音样本超过接口允许大小，请缩短单次录音。",
            _ => $"语音识别服务返回 HTTP {status}，本次没有自动重试。"
        });
    }
    internal static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var result = new StringBuilder(); var buffer = new char[4096]; int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        { if (result.Length + read > 65536) throw new SpeechRecognitionException("语音识别返回内容过长，已停止。"); result.Append(buffer, 0, read); }
        return result.ToString();
    }
}

/// <summary>MiMo chat/completions input_audio, also usable by compatible audio-chat providers.</summary>
public sealed class ChatAudioSpeechRecognizer(SpeechRecognitionOptions options, HttpClient? http = null, bool mimo = true) : ISpeechRecognizer
{
    public async Task<SpeechRecognitionResult> RecognizeAsync(SpeechAudio audio, CancellationToken token, IProgress<string>? partial = null)
    {
        token.ThrowIfCancellationRequested(); SpeechHttp.Validate(audio);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(35));
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint("/chat/completions"));
        SpeechHttp.Authenticate(request, options, mimo);
        bool qwen = !mimo && options.Model.StartsWith("qwen3-asr", StringComparison.OrdinalIgnoreCase);
        string audioData = "data:audio/wav;base64," + Convert.ToBase64String(audio.Wave.Span);
        object inputAudio = qwen ? new { data = audioData } : new { data = audioData, format = "wav" };
        var body = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["messages"] = new[] { new { role = "user", content = new[] { new { type = "input_audio", input_audio = inputAudio } } } },
            ["stream"] = true
        };
        if (mimo) body["asr_options"] = new { language = options.Language is "zh" or "en" ? options.Language : "auto" };
        else if (qwen)
        {
            var hints = new Dictionary<string, object> { ["enable_itn"] = true };
            if (options.Language is "zh" or "en") hints["language"] = options.Language;
            body["asr_options"] = hints;
        }
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await SpeechHttp.SendAsync(http ?? SpeechHttp.Client, request, timeout.Token).ConfigureAwait(false);
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
        {
            using var document = JsonDocument.Parse(await SpeechHttp.ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false));
            ValidateFinish(document.RootElement);
            string text = ReadContent(document.RootElement, "message"); partial?.Report(text); return new(text.Trim());
        }
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream); var textBuilder = new StringBuilder(); bool finished = false;
        while (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
        {
            if (line.Length > 65536) throw new SpeechRecognitionException("语音识别返回内容过长，已停止。");
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            string data = line[5..].Trim(); if (data == "[DONE]") { finished = true; break; }
            if (data.Length == 0) continue;
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.TryGetProperty("error", out _)) throw new SpeechRecognitionException("语音识别流返回错误，本次未自动重试。");
            textBuilder.Append(ReadContent(document.RootElement, "delta"));
            if (textBuilder.Length > 32000) throw new SpeechRecognitionException("单段识别文字过长，已停止。");
            if (textBuilder.Length > 0) partial?.Report(textBuilder.ToString());
            ValidateFinish(document.RootElement);
            if (document.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String) finished = reason.GetString() == "stop";
        }
        if (!finished) throw new SpeechRecognitionException("语音识别连接提前中断，未提交不完整的指令。");
        return new(textBuilder.ToString().Trim());
    }
    private static void ValidateFinish(JsonElement root)
    {
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String && reason.GetString() != "stop")
            throw new SpeechRecognitionException("语音识别未完整结束，本次不会提交不完整的指令。");
    }
    private static string ReadContent(JsonElement root, string property)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return "";
        var choice = choices[0];
        return choice.TryGetProperty(property, out var message) && message.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : "";
    }
}

/// <summary>Standard multipart /audio/transcriptions; local whisper.cpp/faster-whisper servers can use the same seam.</summary>
public sealed class TranscriptionSpeechRecognizer(SpeechRecognitionOptions options, HttpClient? http = null) : ISpeechRecognizer
{
    public async Task<SpeechRecognitionResult> RecognizeAsync(SpeechAudio audio, CancellationToken token, IProgress<string>? partial = null)
    {
        token.ThrowIfCancellationRequested(); SpeechHttp.Validate(audio);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(35));
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint("/audio/transcriptions"));
        SpeechHttp.Authenticate(request, options);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio.Wave.ToArray()); file.Headers.ContentType = new("audio/wav");
        form.Add(file, "file", "speech.wav"); form.Add(new StringContent(options.Model), "model");
        form.Add(new StringContent("json"), "response_format");
        if (options.Language is "zh" or "en")
            form.Add(new StringContent(options.Language), options.Model.Equals("gpt-transcribe", StringComparison.OrdinalIgnoreCase) ? "languages[]" : "language");
        request.Content = form;
        using var response = await SpeechHttp.SendAsync(http ?? SpeechHttp.Client, request, timeout.Token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await SpeechHttp.ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String)
            throw new SpeechRecognitionException("语音接口没有返回 text，请检查模型和协议是否匹配。");
        string text = value.GetString()?.Trim() ?? ""; partial?.Report(text); return new(text);
    }
}
