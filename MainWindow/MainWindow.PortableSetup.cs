using alpha_island.Core;
using alpha_island.Services;

namespace alpha_island;

public partial class MainWindow
{
    private async Task InitializePortableIntegrationsAsync()
    {
        if (!_runtimeEnabled || _exiting) return;
        int port = SettingsRegistry.GetInt(_options, "AgentBridgePort", 8765);
        var report = await AgentIntegrationService.RunAsync("Auto", port);
        if (_exiting) return;
        if (report.Error != null) IslandLog.Warn("Integrations", report.Error);
        // Fresh defaults are established when options are loaded, before services start.
        // Completing asynchronous detection must not override a switch changed by the user.
    }
}
