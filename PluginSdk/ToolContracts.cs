using System.Text.Json;

namespace AlphaIsland.PluginSdk;

public enum ToolRiskLevel { ReadOnly, DeviceControl, RequiresConfirmation }
public sealed record ToolDefinition(string Name, string Description, JsonElement Parameters,
    ToolRiskLevel Risk = ToolRiskLevel.RequiresConfirmation);
public sealed record ToolRequest(string CallId, string Name, JsonElement Arguments, string SessionId = "")
{
    public string TurnId { get; init; } = "";
}
public sealed record ToolResult(bool Success, string Message, JsonElement? Data = null)
{
    public static ToolResult Fail(string message) => new(false, message);
    public static ToolResult Ok(string message, object? data = null) => new(true, message,
        data == null ? null : JsonSerializer.SerializeToElement(data));
}
public interface IToolHandler
{
    ToolDefinition Definition { get; }
    ValueTask<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken);
}
public interface IToolDispatcher
{
    ValueTask<ToolDefinition[]> GetToolsAsync(CancellationToken cancellationToken = default);
    ValueTask<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default);
}
public sealed record DesktopContextSnapshot(DateTimeOffset CapturedAt, Dictionary<string, string> Fields)
{
    public static DesktopContextSnapshot Empty => new(DateTimeOffset.UtcNow, new());
}
public sealed class UnavailableTools : IToolDispatcher
{
    public static UnavailableTools Instance { get; } = new();
    public ValueTask<ToolDefinition[]> GetToolsAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Array.Empty<ToolDefinition>());
    public ValueTask<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ToolResult.Fail("当前宿主不支持工具调用"));
}
