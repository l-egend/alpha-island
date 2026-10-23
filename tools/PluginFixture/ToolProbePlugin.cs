using System.Text.Json;
using AlphaIsland.PluginSdk;
namespace AlphaIsland.PluginFixture;
public sealed class ToolProbePlugin : IIslandPlugin
{
    public void Initialize(IPluginContext context)
    {
        context.RegisterTool(new Echo());
        context.RegisterAction(new("probe", "双向工具测试", () => _ = Probe(context)));
    }
    private static async Task Probe(IPluginContext context)
    {
        try
        {
            var tools = await context.Tools.GetToolsAsync(context.Lifetime);
            var snapshot = await context.GetDesktopContextAsync("电脑状态", context.Lifetime);
            var request = new ToolRequest("worker-once", "set_mute", JsonSerializer.SerializeToElement(new { muted = true }), "worker-session");
            var first = await context.Tools.ExecuteAsync(request, context.Lifetime);
            var second = await context.Tools.ExecuteAsync(request, context.Lifetime);
            context.WriteConfiguration("toolProbeResult", tools.Length + ":" + snapshot.Fields.GetValueOrDefault("cpu") + ":" + first.Success + ":" + second.Success);
        }
        catch (Exception) { context.WriteConfiguration("toolProbeResult", "failed"); }
    }
    private sealed class Echo : IToolHandler
    {
        public ToolDefinition Definition { get; } = new("echo", "回传工作进程标识", JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }), ToolRiskLevel.ReadOnly);
        public ValueTask<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult(ToolResult.Ok("工具在工作进程执行", new { process = Environment.ProcessId }));
    }
    public void Dispose() { }
}
