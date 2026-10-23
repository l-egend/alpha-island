using AlphaIsland.PluginSdk;

namespace alpha_island.Plugins;

internal sealed partial class PluginHost
{
    internal bool AllowPluginTools { get; set; }
    internal IToolDispatcher? ToolDispatcher { get; set; }
    internal Func<string, CancellationToken, ValueTask<DesktopContextSnapshot>>? ContextReader { get; set; }
}
