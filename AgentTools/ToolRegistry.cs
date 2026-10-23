using System.Text.Json;
using System.Text.RegularExpressions;
using AlphaIsland.PluginSdk;

namespace AlphaIsland.AgentTools;

/// <summary>One execution policy for built-ins and process proxies. Never executes model text.</summary>
public sealed class ToolRegistry : IToolDispatcher, IDisposable
{
    private sealed record Entry(string Owner, IToolHandler Handler, CancellationToken Lifetime, Func<bool> Allowed);
    private readonly Dictionary<string, Entry> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Session, string Turn, string Call), (string Fingerprint, Task<ToolResult> Task)> _calls = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Func<bool> _enabled;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    public ToolRegistry(Func<bool> enabled) => _enabled = enabled;
    public static void Validate(ToolDefinition definition)
    {
        if (!Regex.IsMatch(definition.Name ?? "", "^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)
            || string.IsNullOrWhiteSpace(definition.Description) || definition.Description.Length > 1024
            || definition.Parameters.ValueKind != JsonValueKind.Object || definition.Parameters.GetRawText().Length > 8192
            || !Enum.IsDefined(definition.Risk)) throw new ArgumentException("工具定义无效");
    }
    public void Register(string owner, IToolHandler handler, CancellationToken lifetime = default, Func<bool>? allowed = null)
    {
        Validate(handler.Definition);
        lock (_gate)
        {
            if (_tools.Count >= 64 || _tools.ContainsKey(handler.Definition.Name)) throw new ArgumentException("工具数量超限或名称重复");
            _tools.Add(handler.Definition.Name, new(owner, handler, lifetime, allowed ?? (() => true)));
        }
    }
    public void RemoveOwner(string owner) { lock (_gate) foreach (string name in _tools.Where(p => p.Value.Owner == owner).Select(p => p.Key).ToArray()) _tools.Remove(name); }
    public ValueTask<ToolDefinition[]> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_enabled()) return ValueTask.FromResult(Array.Empty<ToolDefinition>());
            int budget = 16384; var available = new List<ToolDefinition>();
            foreach (var entry in _tools.Values.Where(e => !e.Lifetime.IsCancellationRequested && e.Allowed() && e.Handler.Definition.Risk != ToolRiskLevel.RequiresConfirmation))
            {
                var definition = entry.Handler.Definition;
                int length = definition.Name.Length + definition.Description.Length + definition.Parameters.GetRawText().Length;
                if (length > budget) continue;
                available.Add(definition); budget -= length; if (available.Count == 24) break;
            }
            return ValueTask.FromResult(available.ToArray());
        }
    }
    public ValueTask<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_enabled()) return ValueTask.FromResult(ToolResult.Fail("AI 桌面控制已关闭"));
        if (string.IsNullOrWhiteSpace(request.CallId) || string.IsNullOrWhiteSpace(request.Name) || request.SessionId == null || request.TurnId == null
            || request.CallId.Length > 128 || request.SessionId.Length > 128 || request.TurnId.Length > 64
            || request.Arguments.ValueKind != JsonValueKind.Object || request.Arguments.GetRawText().Length > 8192)
            return ValueTask.FromResult(ToolResult.Fail("工具请求无效"));
        var key = (request.SessionId, request.TurnId, request.CallId);
        string fingerprint = request.Name + ":" + request.Arguments.GetRawText();
        Task<ToolResult> operation;
        lock (_gate)
        {
            if (_calls.TryGetValue(key, out var prior))
                return prior.Fingerprint == fingerprint ? new(prior.Task.WaitAsync(cancellationToken))
                    : ValueTask.FromResult(ToolResult.Fail("重复调用标识的参数不一致"));
            if (!_tools.TryGetValue(request.Name, out var entry) || entry.Lifetime.IsCancellationRequested || !entry.Allowed())
                return ValueTask.FromResult(ToolResult.Fail("工具已停用或不存在"));
            if (entry.Handler.Definition.Risk == ToolRiskLevel.RequiresConfirmation)
                return ValueTask.FromResult(ToolResult.Fail("此工具需要独立授权，当前版本未开放执行"));
            foreach (var stale in _calls.Where(p => p.Value.Task.IsCompleted).Select(p => p.Key).Take(Math.Max(0, _calls.Count - 255)).ToArray()) _calls.Remove(stale);
            if (_calls.Count >= 256) return ValueTask.FromResult(ToolResult.Fail("工具请求过多，请稍后重试"));
            operation = ExecuteCore(entry, request, cancellationToken); _calls.Add(key, (fingerprint, operation));
        }
        return new(operation);
    }
    private async Task<ToolResult> ExecuteCore(Entry entry, ToolRequest request, CancellationToken token)
    {
        await Task.Yield();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, entry.Lifetime, _lifetime.Token);
        cancel.CancelAfter(TimeSpan.FromSeconds(12));
        bool entered = false;
        try
        {
            await _serial.WaitAsync(cancel.Token).ConfigureAwait(false); entered = true;
            cancel.Token.ThrowIfCancellationRequested();
            if (!_enabled() || !entry.Allowed()) return ToolResult.Fail("AI 桌面控制或此插件工具已关闭");
            ValidateArguments(entry.Handler.Definition.Parameters, request.Arguments);
            var result = await entry.Handler.ExecuteAsync(request, cancel.Token).AsTask().WaitAsync(cancel.Token).ConfigureAwait(false);
            if (result.Message.Length > 4096 || result.Data?.GetRawText().Length > 8192) return ToolResult.Fail("工具结果超过容量限制");
            return result;
        }
        catch (OperationCanceledException) { return ToolResult.Fail("工具调用已取消或超时；请查看实际状态后再发起新操作"); }
        catch (ArgumentException) { return ToolResult.Fail("工具参数不符合定义，未执行"); }
        catch (Exception) { return ToolResult.Fail("工具执行失败，未自动重试"); }
        finally { if (entered) _serial.Release(); }
    }
    private static void ValidateArguments(JsonElement schema, JsonElement args)
    {
        var properties = schema.TryGetProperty("properties", out var p) ? p : JsonSerializer.SerializeToElement(new { });
        if (schema.TryGetProperty("required", out var required))
            foreach (var name in required.EnumerateArray()) if (!args.TryGetProperty(name.GetString()!, out _)) throw new ArgumentException("缺少参数");
        foreach (var arg in args.EnumerateObject())
        {
            if (!properties.TryGetProperty(arg.Name, out var rule)) throw new ArgumentException("未知参数");
            string type = rule.GetProperty("type").GetString()!;
            bool valid = type switch { "boolean" => arg.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => arg.Value.TryGetInt32(out _), "string" => arg.Value.ValueKind == JsonValueKind.String, _ => false };
            if (!valid) throw new ArgumentException("参数类型无效");
            if (rule.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(v => v.GetRawText() == arg.Value.GetRawText())) throw new ArgumentException("参数选项无效");
            if (type == "integer" && ((rule.TryGetProperty("minimum", out var min) && arg.Value.GetInt32() < min.GetInt32())
                || (rule.TryGetProperty("maximum", out var max) && arg.Value.GetInt32() > max.GetInt32()))) throw new ArgumentException("参数超出范围");
        }
    }
    public void Dispose() { _lifetime.Cancel(); lock (_gate) _tools.Clear(); }
}
