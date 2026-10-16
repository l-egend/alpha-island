using System.Windows.Controls;
using AlphaIsland.PluginSdk;

namespace AlphaIsland.PluginFixture;

public sealed class ProbePlugin : IIslandPlugin
{
    private IPluginContext? _context;
    public bool Started { get; private set; }
    public bool Stopped { get; private set; }
    public bool Disposed { get; private set; }
    public int Updates { get; private set; }
    public CancellationToken Token { get; private set; }
    public int NetworkMode { get; set; }
    public void Initialize(IPluginContext context)
    {
        _context = context;
        if (context.PluginId.StartsWith("fixture.isolation", StringComparison.Ordinal))
        {
            var input = new TextBox { Name = "IsolationInput", Text = "初始内容", Height = 30 };
            var label = new TextBlock { Name = "IsolationLabel", Text = "正常", FontSize = 16 };
            var button = new Button { Name = "IsolationButton", Content = "提交", Height = 30 };
            button.Click += (_, _) => label.Text = input.Text;
            Token = context.Lifetime;
            context.RegisterService(new("lifecycle", _ => WriteLifecycle("start;"), () => WriteLifecycle("stop;")));
            var panel = new StackPanel { Width = 350 }; panel.Children.Add(input); panel.Children.Add(button); panel.Children.Add(label);
            context.RegisterPage(new("probe", "隔离测试", () => panel));
            context.RegisterAction(new("failfast", "进程故障", () => Environment.FailFast("isolated fixture failure")));
            context.RegisterAction(new("hang", "挂起测试", () => Thread.Sleep(Timeout.Infinite)));
            context.RegisterAction(new("child", "子进程回收", () =>
            {
                var info = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
                info.ArgumentList.Add("/c"); info.ArgumentList.Add("ping -n 90 127.0.0.1 > nul");
                using var child = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("fixture child failed");
                context.WriteConfiguration("fixtureChildPid", child.Id);
            }));
            return;
        }
        if (context.PluginId.StartsWith("fixture.action", StringComparison.Ordinal) || context.PluginId == "fixture.available-fails")
            context.RegisterAction(new(context.PluginId, "测试操作", () =>
            { if (context.PluginId == "fixture.action-fails") throw new InvalidOperationException("action failed"); }, () =>
            { if (context.PluginId == "fixture.available-fails") throw new InvalidOperationException("availability failed"); return true; }));
        if (context.PluginId == "fixture.network")
        {
            context.RegisterNetworkSource(new("primary", "高优先级测试数据", () =>
            {
                var sample = new PluginNetworkSample(DateTimeOffset.UtcNow, 77, 55, 100, 200, "test");
                return context.ReadConfiguration("plugin/fixture.network/mode", NetworkMode) switch
                {
                    1 => null, 2 => sample with { DownBytesPerSecond = -1 }, 3 => sample with { UpBytesPerSecond = double.NaN },
                    4 => sample with { Timestamp = sample.Timestamp.AddSeconds(-6) }, 5 => sample with { Timestamp = sample.Timestamp.AddSeconds(2) },
                    6 => sample with { DownloadCounterBytes = -1 }, 7 => throw new InvalidOperationException("network source failed"), _ => sample
                };
            }, Priority: 200));
            context.RegisterNetworkSource(new("secondary", "备用测试数据", () => new PluginNetworkSample(DateTimeOffset.UtcNow, 11, 22), Priority: 150));
        }
        context.RegisterSetting(new("number", "数值", PluginSettingKind.Number, 4d, Minimum: 0, Maximum: 10));
        context.RegisterSetting(new("choice", "选择", PluginSettingKind.Choice, "A", Choices: new[] { "A", "B" }));
        context.RegisterPage(new("probe", "测试组件", () => new TextBlock { Text = "测试" }, Activated: () =>
        { if (context.PluginId == "fixture.callback-fails") throw new InvalidOperationException("activation failed"); }));
        context.RegisterService(new("probe", token =>
        {
            Token = token; Started = true;
            if (context.PluginId == "fixture.start-fails") throw new InvalidOperationException("start failed");
        }, () => { Stopped = true; }));
        if (context.PluginId == "fixture.init-fails") throw new InvalidOperationException("initialize failed");
    }
    public void OnSettingsChanged()
    {
        Updates++;
        if (_context?.PluginId == "fixture.settings-fails") throw new InvalidOperationException("settings failed");
    }
    private void WriteLifecycle(string text)
    {
        if (_context?.PluginId.StartsWith("fixture.isolation", StringComparison.Ordinal) != true) return;
        System.IO.Directory.CreateDirectory(_context.DataDirectory);
        System.IO.File.AppendAllText(System.IO.Path.Combine(_context.DataDirectory, _context.PluginId + ".txt"), text);
    }
    public void Dispose() { Disposed = true; WriteLifecycle("dispose:" + Token.IsCancellationRequested + ";"); }
}
