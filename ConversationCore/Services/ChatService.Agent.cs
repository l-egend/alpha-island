using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AlphaIsland.PluginSdk;

namespace alpha_island.Services;

public sealed record ChatToolCall(string Id, string Name, string Arguments);
public sealed record AgentChatEvent(string Kind, string Text);
public sealed record ChatUsage(int InputTokens, int OutputTokens, int CachedTokens);

public sealed partial class ChatService
{
    public ChatUsage? LastUsage { get; private set; }
    private sealed class ToolStreamState(ToolDefinition[] tools)
    {
        public ToolDefinition[] Tools { get; } = tools;
        public bool Completed;
        public bool RequestAccepted;
        public bool HasCalls => _calls.Count > 0;
        public StringBuilder Reasoning { get; } = new();
        public ChatUsage? Usage;
        private readonly SortedDictionary<int, (string Id, string Name, string Args)> _calls = new();
        public ChatToolCall[] Calls => _calls.Values.Select(c => new ChatToolCall(c.Id, c.Name, c.Args)).ToArray();
        public void Observe(JsonElement root)
        {
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                int Count(string name) => usage.TryGetProperty(name, out var n) && n.TryGetInt32(out int value) ? Math.Max(0, value) : 0;
                int cached = Count("prompt_cache_hit_tokens");
                if (usage.TryGetProperty("prompt_tokens_details", out var details) && details.TryGetProperty("cached_tokens", out var hit) && hit.TryGetInt32(out int count)) cached = Math.Max(0, count);
                Usage = new(Count("prompt_tokens"), Count("completion_tokens"), cached);
            }
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return;
            var first = choices[0];
            if (first.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                Completed = reason.GetString() is "tool_calls" or "stop";
            if (!first.TryGetProperty("delta", out var delta) && !first.TryGetProperty("message", out delta)) return;
            if (delta.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
            {
                if (Reasoning.Length + reasoning.GetString()!.Length > 131072) throw new ChatException("模型推理上下文超过限制");
                Reasoning.Append(reasoning.GetString());
            }
            if (!delta.TryGetProperty("tool_calls", out var calls)) return;
            foreach (var call in calls.EnumerateArray())
            {
                int index = call.TryGetProperty("index", out var n) ? n.GetInt32() : 0;
                if (index is < 0 or > 7) throw new ChatException("单轮工具数量超过限制");
                var previous = _calls.GetValueOrDefault(index, ("", "", ""));
                string id = call.TryGetProperty("id", out var i) ? i.GetString() ?? "" : previous.Item1;
                string name = previous.Item2, args = previous.Item3;
                if (call.TryGetProperty("function", out var function))
                {
                    if (function.TryGetProperty("name", out var key)) name += key.GetString();
                    if (function.TryGetProperty("arguments", out var value)) args += value.GetString();
                }
                if (id.Length > 128 || name.Length > 64 || args.Length > 8192) throw new ChatException("工具请求超过容量限制");
                _calls[index] = (id, name, args);
            }
        }
    }
    /// <summary>Compatible entry point for existing consumers without a first-request override.</summary>
    public IAsyncEnumerable<AgentChatEvent> RunAgentAsync(IReadOnlyList<ChatMessage> history, ChatConfig config,
        IToolDispatcher dispatcher, string sessionId, CancellationToken cancellationToken = default) =>
        RunAgentAsync(history, config, dispatcher, sessionId, cancellationToken, null, null);

    /// <summary>The initial prompt replaces the base prompt only until the first HTTP success. Rejected requests can retry it.</summary>
    /// <remarks>The acceptance callback runs on the iterator consumer's context. It must not throw. No fallback can repeat an executed action.</remarks>
    public async IAsyncEnumerable<AgentChatEvent> RunAgentAsync(IReadOnlyList<ChatMessage> history, ChatConfig config,
        IToolDispatcher dispatcher, string sessionId, [EnumeratorCancellation] CancellationToken cancellationToken,
        string? initialSystemPrompt, Action? onFirstRequestAccepted)
    {
        LastError = "";
        var messages = history.ToList();
        string turnId = Guid.NewGuid().ToString("N");
        var offered = await dispatcher.GetToolsAsync(cancellationToken);
        string prompt = history.LastOrDefault(m => m.Role == ChatMessage.RoleUser)?.Content ?? "";
        bool Has(params string[] words) => words.Any(w => prompt.Contains(w, StringComparison.OrdinalIgnoreCase));
        var tools = offered.Where(t => t.Name switch
        {
            "set_volume" or "set_mute" => Has("音量", "静音", "声音", "volume", "mute"),
            "media_control" => Has("歌曲", "切歌", "播放", "暂停", "上一首", "下一首", "音乐", "music", "song", "play", "pause", "track"),
            "pomodoro_control" => Has("番茄", "专注", "计时", "pomodoro", "focus", "timer"),
            "get_desktop_state" => !offered.Any(t => t.Name == "get_island_info") && DesktopContextPolicy.RequestedSections(prompt).Length > 0,
            // Keep read-only capabilities discoverable for device aliases and follow-up questions.
            // Definitions do not read state; actual values are returned only after a tool call.
            "get_island_info" => !DesktopContextPolicy.ExcludesContext(prompt),
            _ => true
        }).ToArray();
        var executed = new HashSet<string>(StringComparer.Ordinal);
        LastUsage = null; int inputs = 0, outputs = 0, cached = 0;
        bool plainFallback = false;
        bool firstRequestPending = true;
        for (int round = 0; round < 5; round++)
        {
            var state = new ToolStreamState(round < 4 ? tools : []); var text = new StringBuilder();
            bool retryPlain = false;
            var requestConfig = firstRequestPending && !string.IsNullOrWhiteSpace(initialSystemPrompt)
                ? config with { SystemPrompt = initialSystemPrompt } : config;
            await using (var stream = StreamCoreAsync(messages, requestConfig, cancellationToken, state).GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    bool next;
                    try { next = await stream.MoveNextAsync(); }
                    catch (ChatException ex) when (round == 0 && !plainFallback && text.Length == 0 && !state.HasCalls && tools.Length > 0
                        && (ex.Message.Contains("HTTP 400", StringComparison.Ordinal) || ex.Message.Contains("HTTP 422", StringComparison.Ordinal)))
                    { retryPlain = true; break; }
                    finally
                    {
                        if (firstRequestPending && state.RequestAccepted)
                        {
                            firstRequestPending = false;
                            onFirstRequestAccepted?.Invoke();
                        }
                    }
                    if (!next) break;
                    text.Append(stream.Current); yield return new("text", stream.Current);
                }
            }
            if (retryPlain)
            { tools = []; plainFallback = true; round--; yield return new("result", "接口不接受工具参数，本次继续普通对话。"); continue; }
            if (state.Usage is { } usage)
            { inputs += usage.InputTokens; outputs += usage.OutputTokens; cached += usage.CachedTokens; LastUsage = new(inputs, outputs, cached); }
            if (!state.HasCalls) yield break;
            if (tools.Length == 0) throw new ChatException("模型返回了未开放的工具请求");
            messages.Add(new("assistant", text.ToString()) { ToolCalls = state.Calls, ReasoningContent = state.Reasoning.ToString() });
            foreach (var call in state.Calls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ToolResult result;
                if (!executed.Add(call.Id) || call.Id.Length == 0 || !tools.Any(t => t.Name == call.Name)) result = ToolResult.Fail("工具标识重复或工具未开放，未执行");
                else
                {
                    yield return new("tool", "正在执行：" + call.Name);
                    try
                    {
                        using var arguments = JsonDocument.Parse(call.Arguments);
                        result = await dispatcher.ExecuteAsync(new ToolRequest(call.Id, call.Name, arguments.RootElement.Clone(), sessionId) { TurnId = turnId }, cancellationToken);
                    }
                    catch (JsonException) { result = ToolResult.Fail("工具参数格式无效，未执行"); }
                }
                messages.Add(new("tool", JsonSerializer.Serialize(result)) { ToolCallId = call.Id });
                if (!result.Success) tools = tools.Where(t => t.Name != call.Name).ToArray();
                yield return new("result", result.Message);
            }
        }
        throw new ChatException("已达到本次工具调用轮次上限，已停止继续调用");
    }
}
