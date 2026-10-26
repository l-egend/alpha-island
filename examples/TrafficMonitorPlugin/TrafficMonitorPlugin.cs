using AlphaIsland.PluginSdk;

namespace AlphaIsland.Plugins.TrafficMonitor;

public sealed class TrafficMonitorPlugin : IIslandPlugin, IPluginSuspendGuard
{
    public bool CanSuspend => true;
    private BridgeReader? _reader;
    private IPluginContext? _context;
    private string _mapping = "";

    public void Initialize(IPluginContext context)
    {
        _context = context;
        context.RegisterSetting(new("network", "使用 TrafficMonitor 数据", PluginSettingKind.Boolean, true));
        context.RegisterNetworkSource(new("network", "TrafficMonitor", () =>
            context.GetSetting("network", true) ? _reader?.Read() : null));
        context.RegisterService(new("reader", _ => OpenReader(), StopReader));
    }

    private void StopReader() { _reader?.Dispose(); _reader = null; }
    private void OpenReader()
    {
        string mapping = _context?.ReadConfiguration("plugin/alpha.trafficmonitor/mapping-name", "") ?? "";
        if (_reader != null && mapping == _mapping) return;
        StopReader(); _mapping = mapping; _reader = new BridgeReader(mapping.Length == 0 ? null : mapping);
    }
    public void OnSettingsChanged() => OpenReader();
    public void Dispose() => StopReader();
}
