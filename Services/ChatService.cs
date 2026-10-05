using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using alpha_island.Core;

namespace alpha_island.Services
{
    /// <summary>一条对话消息（OpenAI 格式的 role/content）。</summary>
    public sealed record ChatMessage(string Role, string Content)
    {
        public const string RoleSystem = "system";
        public const string RoleUser = "user";
        public const string RoleAssistant = "assistant";
    }

    /// <summary>
    /// 一次请求要用的全部配置（从设置注册表 + DPAPI 密钥仓读一份快照）。
    /// 做成快照的理由：请求过程中用户可能改设置，不该让同一个请求前后用两套参数。
    /// </summary>
    public sealed record ChatConfig(
        string BaseUrl,
        string ApiKey,
        string Model,
        string SystemPrompt,
        double Temperature,
        int MaxTokens)
    {
        public bool IsUsable => !string.IsNullOrWhiteSpace(Model) && HasValidEndpoint;
        public bool HasValidEndpoint => Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

        public override string ToString() => $"ChatConfig {{ Host = {HostForLog}, Model = {Model}, ApiKey = [已隐藏] }}";

        /// <summary>是否有密钥（本地模型如 Ollama 可以没有）。</summary>
        public bool HasKey => !string.IsNullOrWhiteSpace(ApiKey);

        public static ChatConfig From(IslandOptions options) => new(
            (SettingsRegistry.GetText(options, "AiBaseUrl", "https://api.deepseek.com/v1") ?? "").Trim(),
            AiSecretStore.Load(),
            SettingsRegistry.GetText(options, "AiModel", "deepseek-chat"),
            SettingsRegistry.GetText(options, "AiSystemPrompt", ""),
            0.7,
            1024);

        public string ModelsEndpoint => Endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? Endpoint[..^"/chat/completions".Length] + "/models" : "";

        /// <summary>接口地址 → 真正的 chat completions 端点（兼容用户直接填完整 URL）。</summary>
        public string Endpoint
        {
            get
            {
                string b = (BaseUrl ?? "").Trim().TrimEnd('/');
                if (b.Length == 0) return "";
                if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return b;
                return b + "/chat/completions";
            }
        }

        /// <summary>只用于日志：把地址压成主机名，绝不带查询串。</summary>
        public string HostForLog
        {
            get
            {
                try { return new Uri(Endpoint).Host; }
                catch { return "(地址无法解析)"; }
            }
        }
    }

    /// <summary>对话失败（网络、鉴权、被中断）。消息已经是能直接显示给用户的中文。</summary>
    public sealed class ChatException : Exception
    {
        public ChatException(string message) : base(message) { }
    }

    /// <summary>
    /// B 组①：OpenAI 兼容的对话客户端。
    ///
    /// 与 <see cref="LyricsService"/> 里的 HttpClient 用法不同，这里**必须流式**：
    /// 桌面小卡片等不起"整段读完再显示"，所以用 <c>stream: true</c> + SSE 逐行读
    /// （<see cref="HttpCompletionOption.ResponseHeadersRead"/>，边到边 yield）。
    /// 一次请求的节奏：先等首 token（60s），之后每收到一段就把空闲超时重置（30s），
    /// 所以"长回答慢慢吐"不会被掐，而"连上后彻底没声了"会超时。
    ///
    /// 密钥来自 <see cref="AiSecretStore"/>（DPAPI 加密文件），**不进 options.json、不进日志**。
    /// </summary>
    public sealed partial class ChatService : IDisposable
    {
        /// <summary>等第一个 token 的上限。</summary>
        private static readonly TimeSpan FirstTokenTimeout = TimeSpan.FromSeconds(60);

        /// <summary>已有输出后，两段之间的空闲上限。</summary>
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

        private static readonly HttpClient Http = CreateHttpClient();
        private readonly HttpClient _http;
        private readonly TimeSpan _firstTimeout;
        private readonly TimeSpan _idleTimeout;
        public ChatService() : this(Http, FirstTokenTimeout, IdleTimeout) { }
        internal ChatService(HttpClient http, TimeSpan firstTimeout, TimeSpan idleTimeout)
        {
            _http = http;
            _firstTimeout = firstTimeout;
            _idleTimeout = idleTimeout;
        }

        /// <summary>最近一次失败的中文原因（卡片页显示它）。</summary>
        public string LastError { get; private set; } = "";

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
            // 超时全部交给 CancellationToken（流式响应不能有整体超时）
            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AlphaIsland/0.1");
            return client;
        }

        /// <summary>
        /// 流式对话：边收边吐增量文本。抛 <see cref="ChatException"/> 时 <see cref="LastError"/> 已填好。
        /// </summary>
        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatMessage> history,
            ChatConfig config,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastError = "";

            if (config == null || !config.IsUsable)
            {
                LastError = "接口地址或模型尚未配置（设置 → AI，连接接口后选择模型）";
                throw new ChatException(LastError);
            }
            if (!double.IsFinite(config.Temperature) || config.Temperature is < 0 or > 2 || config.MaxTokens <= 0)
            {
                LastError = "温度或最大回复长度无效（设置 → AI）";
                throw new ChatException(LastError);
            }

            var messages = new List<object>();
            if (!string.IsNullOrWhiteSpace(config.SystemPrompt))
            {
                messages.Add(new { role = ChatMessage.RoleSystem, content = config.SystemPrompt });
            }
            foreach (var m in history)
            {
                if (string.IsNullOrWhiteSpace(m.Content)) continue;
                messages.Add(new { role = m.Role, content = m.Content });
            }

            var payload = new Dictionary<string, object?>
            {
                ["model"] = config.Model,
                ["stream"] = true,
                ["temperature"] = config.Temperature,
                ["max_tokens"] = config.MaxTokens,
                ["messages"] = messages
            };
            string json = JsonSerializer.Serialize(payload, IslandOptions.JsonOptions);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(_firstTimeout);

            HttpResponseMessage? response = null;
            Stream? stream = null;
            StreamReader? reader = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, config.Endpoint)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (config.HasKey)
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + config.ApiKey);
                }

                IslandLog.Debug("Chat", $"请求 {config.Model} → {config.HostForLog}（历史 {history.Count} 条，流式，温度 {config.Temperature:0.##}）");

                try
                {
                    response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    LastError = "连接超时（60 秒内没有响应，检查网络或接口地址）";
                    IslandLog.Warn("Chat", LastError);
                    throw new ChatException(LastError);
                }
                catch (HttpRequestException ex)
                {
                    LastError = "连接失败：" + Redact(ex.Message, config.ApiKey);
                    IslandLog.Warn("Chat", LastError);
                    throw new ChatException(LastError);
                }

                if (!response.IsSuccessStatusCode)
                {
                    string body = await ReadBodySafeAsync(response, linked.Token).ConfigureAwait(false);
                    LastError = DescribeHttpError(response.StatusCode, Redact(body, config.ApiKey));
                    IslandLog.Warn("Chat", "请求失败: " + LastError);
                    throw new ChatException(LastError);
                }

                stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
                reader = new StreamReader(stream, Encoding.UTF8);

                while (true)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        LastError = "响应超时或中断（等待内容超过限制）";
                        IslandLog.Warn("Chat", LastError);
                        throw new ChatException(LastError);
                    }

                    if (line == null) break;                       // 服务端关了流
                    if (line.Length == 0) continue;
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;   // 注释行/event 行

                    string data = line.Substring(5).Trim();
                    if (data.Length == 0) continue;
                    if (data == "[DONE]") break;

                    string? delta = null;
                    string? failure = null;
                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("error", out var err))
                        {
                            failure = err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var em)
                                ? em.GetString()
                                : err.ToString();
                        }
                        else if (root.TryGetProperty("choices", out var choices) &&
                                 choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                        {
                            var first = choices[0];
                            // 流式是 delta.content，个别兼容实现直接回 message.content
                            if (first.TryGetProperty("delta", out var reasoning) && reasoning.ValueKind == JsonValueKind.Object
                                && reasoning.TryGetProperty("reasoning_content", out var thought) && thought.ValueKind == JsonValueKind.String
                                && !string.IsNullOrEmpty(thought.GetString())) linked.CancelAfter(_idleTimeout);
                            if (first.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.Object &&
                                d.TryGetProperty("content", out var dc) && dc.ValueKind == JsonValueKind.String)
                            {
                                delta = dc.GetString();
                            }
                            else if (first.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object &&
                                     msg.TryGetProperty("content", out var mc) && mc.ValueKind == JsonValueKind.String)
                            {
                                delta = mc.GetString();
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        continue;   // 心跳/非 JSON 行，跳过
                    }

                    if (!string.IsNullOrEmpty(failure))
                    {
                        LastError = "服务端返回错误：" + Redact(failure, config.ApiKey);
                        IslandLog.Warn("Chat", LastError);
                        throw new ChatException(LastError);
                    }

                    if (!string.IsNullOrEmpty(delta))
                    {
                        linked.CancelAfter(_idleTimeout);   // 有内容就把空闲计时重置
                        yield return delta;
                    }
                }

                IslandLog.Debug("Chat", "流式响应结束");
            }
            finally
            {
                reader?.Dispose();
                stream?.Dispose();
                response?.Dispose();
            }
        }

        private static async Task<string> ReadBodySafeAsync(HttpResponseMessage response, CancellationToken token)
        {
            try
            {
                string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                return body;
            }
            catch
            {
                return "";
            }
        }

        /// <summary>把 HTTP 状态码翻成用户看得懂的中文原因。</summary>
        private static string DescribeHttpError(HttpStatusCode status, string body)
        {
            string hint = (int)status switch
            {
                401 => "API Key 无效或没填（HTTP 401）",
                403 => "这个 Key 没有权限访问该模型（HTTP 403）",
                404 => "接口地址不对（HTTP 404，检查 Base URL 是否要带 /v1）",
                422 => "请求参数被拒绝（HTTP 422，检查模型名）",
                429 => "触发限流或余额不足（HTTP 429）",
                >= 500 => $"服务端错误（HTTP {(int)status}）",
                _ => $"请求失败（HTTP {(int)status}）"
            };
            if (body.Length > 400) body = body[..400] + "…";
            return string.IsNullOrWhiteSpace(body) ? hint : hint + "｜" + body.Trim();
        }

        private static string Redact(string value, string key) => string.IsNullOrEmpty(key)
            ? value : value.Replace(key, "[密钥已隐藏]", StringComparison.Ordinal);

        public void Dispose() { }
    }

    /// <summary>
    /// B 组①：会话历史落盘（<c>%LOCALAPPDATA%\alpha-island\ai\sessions\&lt;id&gt;.json</c>）。
    /// 只留最近 40 条，避免文件无限长大、也避免把 token 预算喂给很旧的上下文。
    /// </summary>
    public static class ChatHistoryStore
    {
        public const string DefaultSessionId = "default";
        private const int MaxMessages = 40;

        private sealed class SessionFile
        {
            public string Session { get; set; } = DefaultSessionId;
            public string Updated { get; set; } = "";
            public List<ChatMessage> Messages { get; set; } = new();
        }

        private static string PathFor(string sessionId)
        {
            string id = string.IsNullOrWhiteSpace(sessionId) ? DefaultSessionId : sessionId;
            foreach (char bad in Path.GetInvalidFileNameChars()) id = id.Replace(bad, '_');
            return Path.Combine(AppPaths.AiSessionsDir, id + ".json");
        }

        public static List<ChatMessage> Load(string sessionId = DefaultSessionId)
        {
            foreach (string path in new[] { PathFor(sessionId), PathFor(sessionId) + ".bak" })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var data = AtomicJsonFile.Read<SessionFile>(path, IslandOptions.JsonOptions);
                    if (data?.Messages == null) continue;
                    return data.Messages.Where(m => m != null &&
                        (m.Role == ChatMessage.RoleUser || m.Role == ChatMessage.RoleAssistant) &&
                        !string.IsNullOrWhiteSpace(m.Content)).TakeLast(MaxMessages).ToList();
                }
                catch (Exception ex)
                {
                    IslandLog.Warn("Chat", "会话历史读取失败: " + ex.Message);
                }
            }
            return new List<ChatMessage>();
        }

        public static void Save(string sessionId, IReadOnlyList<ChatMessage> messages)
        {
            try
            {
                AppPaths.EnsureDirectory(AppPaths.AiSessionsDir);
                var list = new List<ChatMessage>(messages);
                if (list.Count > MaxMessages) list = list.GetRange(list.Count - MaxMessages, MaxMessages);

                var file = new SessionFile
                {
                    Session = string.IsNullOrWhiteSpace(sessionId) ? DefaultSessionId : sessionId,
                    Updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Messages = list
                };
                AtomicJsonFile.Write(PathFor(sessionId), JsonSerializer.Serialize(file, IslandOptions.JsonOptions));
            }
            catch (Exception ex)
            {
                IslandLog.Warn("Chat", "会话历史保存失败: " + ex.Message);
            }
        }

        public static void Clear(string sessionId = DefaultSessionId)
        {
            try
            {
                string path = PathFor(sessionId);
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
            }
            catch (Exception ex)
            {
                IslandLog.Warn("Chat", "会话历史清除失败: " + ex.Message);
            }
        }
    }
}
