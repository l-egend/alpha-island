using System.Diagnostics;
using System.Windows;
using L = AlphaIsland.PluginSdk.Localization.UiText;

namespace alpha_island;

public partial class MainWindow
{
    private void RestartForLanguage()
    {
        if (!_runtimeEnabled || _exiting || Environment.ProcessPath is not { } executable) return;
        // ExitApplication saves settings and releases the single-instance mutex before Exit fires.
        System.Windows.Application.Current.Exit += RestartAfterExit;
        ExitApplication();

        void RestartAfterExit(object? sender, ExitEventArgs args)
        {
            System.Windows.Application.Current.Exit -= RestartAfterExit;
            try { Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true }); }
            catch (Exception error) { Core.IslandLog.Error("LanguageRestart", error); }
        }
    }
}
