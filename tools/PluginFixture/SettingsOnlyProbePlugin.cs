using System.IO;
using System.Windows.Controls;
using AlphaIsland.PluginSdk;

namespace AlphaIsland.PluginFixture;

public sealed class SettingsOnlyProbePlugin : IIslandPlugin, IPluginSuspendGuard
{
    private IPluginContext _context = null!;
    public bool CanSuspend => true;
    public void Initialize(IPluginContext context)
    {
        _context = context;
        context.RegisterService(new("background", _ =>
        {
            Mark("service-start");
            if (context.ReadConfiguration("FixtureFailService", false)) throw new InvalidOperationException("fixture service failure");
        }, () => Mark("service-stop")));
        context.RegisterNetworkSource(new("network", "配置测试", () => { Mark("network-read"); return null; }));
        context.RegisterPage(new("probe", "后台页面", () => { Mark("page-created"); return new TextBlock { Text = "runtime" }; }));
        context.RegisterSettingsView(() =>
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "接口地址" });
            string key = "plugin/" + context.PluginId + "/endpoint";
            var editor = new TextBox { Name = "ProbeEndpointBox", Text = context.ReadConfiguration(key, "initial"), Width = 300, Height = 32 };
            editor.TextChanged += (_, _) => context.WriteConfiguration(key, editor.Text);
            panel.Children.Add(editor);
            var state = new TextBlock { Name = "ProbeToolsState", Text = "pending" };
            var tools = new Button { Name = "ProbeToolsButton", Content = "检查工具", Width = 120, Height = 32 };
            tools.Click += async (_, _) =>
            {
                var definitions = await context.Tools.GetToolsAsync(context.Lifetime);
                var snapshot = await context.GetDesktopContextAsync("fixture", context.Lifetime);
                var result = await context.Tools.ExecuteAsync(new("fixture", "unused", System.Text.Json.JsonSerializer.SerializeToElement(new { })), context.Lifetime);
                state.Text = $"{definitions.Length}/{snapshot.Fields.Count}/{result.Success}";
            };
            panel.Children.Add(tools); panel.Children.Add(state); return panel;
        });
    }
    private void Mark(string name)
    {
        Directory.CreateDirectory(_context.DataDirectory);
        File.AppendAllText(Path.Combine(_context.DataDirectory, name + ".txt"), "1;");
    }
    public void Dispose() => Mark("disposed");
}
