using AlphaIsland.PluginSdk;

namespace AlphaIsland.Plugins.ApiChat;

internal sealed class ApiToolDispatcher(IPluginContext context, Action<ToolResult>? stateRead = null) : IToolDispatcher
{
    public async ValueTask<ToolDefinition[]> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        if (!context.ReadConfiguration("AiToolsEnabled", true)) return [];
        ToolDefinition[] tools;
        try { tools = await context.Tools.GetToolsAsync(cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return []; }
        return context.ReadConfiguration("AiDesktopContextEnabled", true) ? tools : tools.Where(t => !IsStateTool(t.Name)).ToArray();
    }
    public async ValueTask<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        if (!context.ReadConfiguration("AiToolsEnabled", true) || IsStateTool(request.Name) && !context.ReadConfiguration("AiDesktopContextEnabled", true))
            return ToolResult.Fail("此对话的工具或系统状态访问已关闭");
        var result = await context.Tools.ExecuteAsync(request, cancellationToken);
        if (result.Success && IsStateTool(request.Name)) stateRead?.Invoke(result);
        return result;
    }
    private static bool IsStateTool(string name) => name is "get_desktop_state" or "get_island_info";
}
