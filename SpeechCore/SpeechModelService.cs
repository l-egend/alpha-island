using System.Text.Json;

namespace AlphaIsland.Speech;

/// <summary>Explicit, cancellable setup checks. Opening settings never sends a request or records audio.</summary>
public sealed class SpeechModelService(HttpClient? http = null)
{
    public async Task<IReadOnlyList<string>> ListModelsAsync(SpeechRecognitionOptions options, CancellationToken token)
    {
        string root = options.BaseUrl.Trim().TrimEnd('/');
        foreach (string suffix in new[] { "/chat/completions", "/audio/transcriptions", "/models" })
            if (root.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { root = root[..^suffix.Length]; break; }
        var query = options with { BaseUrl = root, Model = string.IsNullOrWhiteSpace(options.Model) ? "model-list" : options.Model };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, query.Endpoint("/models"));
        SpeechHttp.Authenticate(request, query, query.Provider == "mimo");
        using var response = await SpeechHttp.SendAsync(http ?? SpeechHttp.Client, request, timeout.Token).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(await SpeechHttp.ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false));
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new SpeechRecognitionException("服务未提供标准模型列表，仍可手动输入模型名称。");
            var models = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in data.EnumerateArray())
            {
                string? id = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
                if (!string.IsNullOrWhiteSpace(id) && id.Length <= 128 && !id.Any(char.IsControl)) models.Add(id.Trim());
                if (models.Count >= 256) break;
            }
            if (models.Count == 0) throw new SpeechRecognitionException("接口已连接，但未返回可用模型；仍可手动输入模型名称。");
            return models.OrderByDescending(IsRecognitionModel).ThenBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (JsonException) { throw new SpeechRecognitionException("模型列表格式无法读取，仍可手动输入模型名称。"); }
    }

    public async Task<SpeechRecognitionResult> TestRecognitionAsync(SpeechRecognitionOptions options, CancellationToken token)
    {
        // A valid one-second WAV checks the selected endpoint, model and authentication.
        // It contains silence, never microphone audio or anything from the user's history.
        var audio = SpeechAudio.FromPcm(new short[SpeechAudio.SampleRate]);
        return await new SpeechRecognizerRegistry(http).Create(options).RecognizeAsync(audio, token).ConfigureAwait(false);
    }

    private static bool IsRecognitionModel(string id) => new[] { "asr", "whisper", "transcri", "speech-to-text", "stt" }
        .Any(word => id.Contains(word, StringComparison.OrdinalIgnoreCase));
}
