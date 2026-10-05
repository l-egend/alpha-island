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
        if (context.PluginId == "fixture.network")
        {
            context.RegisterNetworkSource(new("primary", "高优先级测试数据", () =>
            {
                var sample = new PluginNetworkSample(DateTimeOffset.UtcNow, 77, 55, 100, 200, "test");
                return NetworkMode switch
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
    public void Dispose() => Disposed = true;
}
