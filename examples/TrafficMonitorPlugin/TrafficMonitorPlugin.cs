using AlphaIsland.PluginSdk;

namespace AlphaIsland.Plugins.TrafficMonitor;

public sealed class TrafficMonitorPlugin : IIslandPlugin
{
    private BridgeReader? _reader;

    public void Initialize(IPluginContext context)
    {
        context.RegisterSetting(new("network", "使用 TrafficMonitor 数据", PluginSettingKind.Boolean, true));
        context.RegisterNetworkSource(new("network", "TrafficMonitor", () =>
            context.GetSetting("network", true) ? _reader?.Read() : null));
        context.RegisterService(new("reader", _ => _reader ??= new BridgeReader(), StopReader));
    }

    private void StopReader() { _reader?.Dispose(); _reader = null; }
    public void OnSettingsChanged() { }
    public void Dispose() => StopReader();
}
