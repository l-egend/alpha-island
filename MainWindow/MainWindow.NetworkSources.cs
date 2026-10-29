using System;
using alpha_island.Core;
using alpha_island.Services;

namespace alpha_island;

public partial class MainWindow
{
    private Action<NetworkSpeedService> _readNetworkCounters = static service => service.Sample();
    private readonly ExternalNetworkState _externalNetwork = new();
    private NetworkSpeedSample CurrentNetworkSample => _externalNetwork.Active ? _externalNetwork.Current : NativeNetworkSnapshot?.Current ?? _netSpeed?.Current ?? default;
    private long NetworkSessionDown => _externalNetwork.Active ? _externalNetwork.SessionDown : NativeNetworkSnapshot?.SessionDown ?? _netSpeed?.SessionDownloadedBytes ?? 0;
    private long NetworkSessionUp => _externalNetwork.Active ? _externalNetwork.SessionUp : NativeNetworkSnapshot?.SessionUp ?? _netSpeed?.SessionUploadedBytes ?? 0;
    private double NetworkPeakDown => _externalNetwork.Active ? _externalNetwork.PeakDown : NativeNetworkSnapshot?.PeakDown ?? _netSpeed?.SessionPeakDownload ?? 0;
    private double NetworkPeakUp => _externalNetwork.Active ? _externalNetwork.PeakUp : NativeNetworkSnapshot?.PeakUp ?? _netSpeed?.SessionPeakUpload ?? 0;
    private NetworkSpeedSample ReadNetworkSample(NetworkSpeedService service)
    {
        var plugin = _plugins?.ReadNetwork(DateTimeOffset.UtcNow);
        if (plugin.HasValue)
        {
            if (_externalNetwork.Apply(plugin.Value.Id, plugin.Value.Title, plugin.Value.Sample)) ClearNetSpeedHistory();
        }
        else if (_externalNetwork.Active)
        {
            _externalNetwork.Reset(); service.ResetSession(); ClearNetSpeedHistory();
            _networkSnapshotSource = service; _networkSnapshot = NetworkCounterSnapshot.Capture(service);
        }
        var daily = plugin?.Id.StartsWith("alpha.trafficmonitor/", StringComparison.OrdinalIgnoreCase) == true
            ? plugin : _plugins?.ReadNetwork(DateTimeOffset.UtcNow, "alpha.trafficmonitor");
        _dailyTraffic?.Observe(DateTimeOffset.Now, NativeObservedDown, NativeObservedUp,
            daily?.Id ?? "", daily?.Sample);
        return CurrentNetworkSample;
    }
}
