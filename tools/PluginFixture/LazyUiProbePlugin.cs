using System.IO;
using System.Windows.Controls;
using AlphaIsland.PluginSdk;

namespace AlphaIsland.PluginFixture;

public sealed class LazyUiProbePlugin : IIslandPlugin, IPluginSuspendGuard
{
    public bool CanSuspend => _input == null || _input.Text.Length == 0;
    private IPluginContext _context = null!;
    private TextBox? _input;
    public void Initialize(IPluginContext context)
    {
        _context = context;
        context.RegisterService(new("background", _ => Mark("started"), () => Mark("stopped")));
        context.RegisterPage(new("probe", "Lazy UI", () =>
        {
            File.AppendAllText(Path.Combine(context.DataDirectory, "created.txt"), "created;");
            return _input = new TextBox { Name = "AiInputBox", Text = "initial", Width = 300, Height = 32 };
        }, Deactivated: () => Mark("deactivated")));
        context.RegisterAction(new("edit", "Edit", () => { if (_input != null) _input.Text = "action"; }));
    }
    private void Mark(string name) { Directory.CreateDirectory(_context.DataDirectory); File.WriteAllText(Path.Combine(_context.DataDirectory, name + ".txt"), "ok"); }
    public void Dispose() => Mark("disposed");
}
