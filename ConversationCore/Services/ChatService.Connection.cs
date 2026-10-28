using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace alpha_island.Services;

public sealed record ModelConnectionResult(string Model, long ElapsedMilliseconds);

public sealed partial class ChatService
{
    /// <summary>仅用接口地址及鉴权获取模型，不依赖已填写的模型名。</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(ChatConfig config, CancellationToken cancellationToken = default)
    {
        using var document = await ConnectionRequestAsync(config, HttpMethod.Get, config.ModelsEndpoint, null, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array) throw new ChatException("接口未返回有效模型列表（需要支持 /models）");
        var models = data.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
            .Select(x => x.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null)
            .Where(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 256 && !x.Any(char.IsControl))
            .Select(x => x!).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).Take(1000).ToArray();
        if (models.Length == 0) throw new ChatException("连接成功，但该密钥没有返回可选模型");
        return models;
    }

    /// <summary>实际发送一条固定短请求验证所选模型，绝不发送用户对话历史。</summary>
    public async Task<ModelConnectionResult> TestModelAsync(ChatConfig config, CancellationToken cancellationToken = default)
    {
        if (!config.IsUsable) throw new ChatException("请先连接接口并选择模型");
        string payload = JsonSerializer.Serialize(new { model = config.Model, stream = false, max_tokens = 256,
            messages = new[] { new { role = "user", content = "请只回复 OK。" } } });
        var timer = Stopwatch.StartNew();
        using var document = await ConnectionRequestAsync(config, HttpMethod.Post, config.Endpoint, payload, cancellationToken).ConfigureAwait(false);
        bool responded = document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
            && choices.EnumerateArray().Any(choice => choice.ValueKind == JsonValueKind.Object
                && choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                && (HasText(message, "content") || HasText(message, "reasoning_content")));
        if (!responded) throw new ChatException("接口已响应，但模型没有返回有效内容，连接测试未通过");
        return new ModelConnectionResult(config.Model, timer.ElapsedMilliseconds);
    }

    private static bool HasText(JsonElement value, string key) => value.TryGetProperty(key, out var text)
        && text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString());

    private async Task<JsonDocument> ConnectionRequestAsync(ChatConfig config, HttpMethod method, string endpoint,
        string? payload, CancellationToken cancellationToken)
    {
        if (!config.HasValidEndpoint) throw new ChatException("接口地址无效，请填写 HTTP 或 HTTPS 接口地址");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_firstTimeout);
        try
        {
            using var request = new HttpRequestMessage(method, endpoint);
            config.Authenticate(request);
            if (payload != null) request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            using var body = new MemoryStream();
            byte[] buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false)) > 0)
            {
                if (body.Length + count > 1024 * 1024) throw new ChatException("接口响应过大，无法读取模型信息");
                body.Write(buffer, 0, count);
            }
            string json = Encoding.UTF8.GetString(body.ToArray());
            if (!response.IsSuccessStatusCode)
                throw new ChatException(response.StatusCode == System.Net.HttpStatusCode.NotFound && method == HttpMethod.Get
                    ? "该接口不提供模型列表，请检查地址是否支持 /models"
                    : DescribeHttpError(response.StatusCode, Redact(json, config.ApiKey)));
            JsonDocument document;
            try { document = JsonDocument.Parse(json); }
            catch (JsonException) { throw new ChatException("接口返回的内容不是有效 JSON，请检查接口地址"); }
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error))
            {
                string detail = Redact(error.ToString(), config.ApiKey);
                document.Dispose();
                throw new ChatException("接口返回错误：" + detail[..Math.Min(detail.Length, 400)]);
            }
            return document;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ChatException("连接测试超时，请检查网络或接口地址"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        { throw new ChatException("连接测试失败：" + Redact(ex.Message, config.ApiKey)); }
    }
}
