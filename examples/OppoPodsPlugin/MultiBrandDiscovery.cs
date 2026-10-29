using System.Globalization;
using AlphaIsland.EarbudsV2.Communication.Abstractions;
using AlphaIsland.EarbudsV2.Communication.Windows;
using AlphaIsland.EarbudsV2.Control.Brands.Apple;

namespace AlphaIsland.Plugins.OppoPods;
internal sealed class MultiBrandDiscovery : IDisposable
{
    private readonly object _gate = new();
    private BleAdvertisementHub? _hub;
    private int _disposed;
    internal IReadOnlyList<EarbudDevice> Discover()
    {
        if (Volatile.Read(ref _disposed) != 0) return [];
        var devices = new Dictionary<ulong, EarbudDevice>();
        var connected = OppoPodsManager.DeviceDiscovery.ListConnected();
        foreach (var item in OppoPodsManager.DeviceDiscovery.ListCandidates())
            devices[item.addr] = new(item.addr, item.name, connected.Any(x => x.addr == item.addr));
        using var classic = new WindowsBluetoothDiscovery();
        foreach (var candidate in classic.DiscoverAsync(CancellationToken.None).GetAwaiter().GetResult())
        {
            if (MultiBrandSupport.Find(candidate.DisplayName, candidate.ServiceIds) == null) continue;
            if (ulong.TryParse(candidate.BluetoothAddress, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address) && address != 0)
                devices[address] = new(address, candidate.DisplayName, true, candidate);
        }
        lock (_gate)
        {
            if (_disposed != 0) return [];
            _hub ??= new BleAdvertisementHub(); _hub.Start();
            foreach (var entry in _hub.Snapshot())
            {
                if (!AppleAdvertisementParser.TryParse(entry.Value.Data, out _)) continue;
                string hex = entry.Key.StartsWith("BLE:", StringComparison.Ordinal) ? entry.Key[4..] : entry.Key;
                if (!ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address) || address == 0) continue;
                var candidate = new DeviceCandidate(entry.Key, entry.Key, hex, entry.Value.LocalName ?? "AirPods", [], [TransportNames.BleAdvertisement]);
                devices[address] = new(address, candidate.DisplayName, connected.Any(x => x.addr == address), candidate);
            }
        }
        return devices.Values.OrderByDescending(x => x.Connected).ThenBy(x => x.Name).ToArray();
    }
    internal Task<IRawConnection> OpenAsync(DeviceCandidate candidate, ConnectionOptions options, CancellationToken token)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(MultiBrandDiscovery));
        if (options.Transport != TransportNames.BleAdvertisement)
            return new WindowsRfcommConnectionFactory().OpenAsync(candidate, options, token);
        lock (_gate)
        {
            if (_disposed != 0) throw new ObjectDisposedException(nameof(MultiBrandDiscovery));
            _hub ??= new BleAdvertisementHub();
            return new BleAdvertisementConnectionFactory(_hub).OpenAsync(candidate, options, token);
        }
    }
    public void Dispose()
    {
        lock (_gate) { if (Interlocked.Exchange(ref _disposed, 1) != 0) return; _hub?.Dispose(); _hub = null; }
    }
}
