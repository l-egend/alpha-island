using System;
using alpha_island.Core;
using alpha_island.Services;

namespace alpha_island;

public partial class MainWindow
{
    private readonly ExternalNetworkState _externalNetwork = new();
    private NetworkSpeedSample CurrentNetworkSample => _externalNetwork.Active ? _externalNetwork.Current : _netSpeed?.Current ?? default;
    private long NetworkSessionDown => _externalNetwork.Active ? _externalNetwork.SessionDown : _netSpeed?.SessionDownloadedBytes ?? 0;
    private long NetworkSessionUp => _externalNetwork.Active ? _externalNetwork.SessionUp : _netSpeed?.SessionUploadedBytes ?? 0;
    private double NetworkPeakDown => _externalNetwork.Active ? _externalNetwork.PeakDown : _netSpeed?.SessionPeakDownload ?? 0;
    private double NetworkPeakUp => _externalNetwork.Active ? _externalNetwork.PeakUp : _netSpeed?.SessionPeakUpload ?? 0;
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
        }
        service.Sample();
        return CurrentNetworkSample;
    }
}
