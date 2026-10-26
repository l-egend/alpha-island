using System.Windows.Controls;
using AlphaIsland.PluginSdk;

namespace AlphaIsland.PluginFixture;

public sealed class GameDraftProbePlugin : IIslandPlugin, IPluginSuspendGuard
{
    private TextBox? _input;
    public bool CanSuspend => _input == null || _input.Text.Length == 0;
    public void Initialize(IPluginContext context) => context.RegisterPage(new("probe", "Draft probe",
        () => _input = new TextBox { Name = "AiInputBox", Width = 300, Height = 32 }));
    public void Dispose() { }
}
