using AlphaIsland.PluginSdk;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace alpha_island.Services;

internal sealed record BluetoothConnectionSnapshot(string Id, string Address, string Name, bool? Connected);

public sealed partial class BluetoothService
{
    private readonly object _connectionRefreshGate = new();
    private Task _connectionRefreshTask = Task.CompletedTask;

    internal Task RefreshConnectionStateAsync()
    {
        lock (_connectionRefreshGate)
        {
            if (_disposed) return Task.CompletedTask;
            return _connectionRefreshTask.IsCompleted
                ? _connectionRefreshTask = RefreshConnectionsCoreAsync() : _connectionRefreshTask;
        }
    }

    private async Task RefreshConnectionsCoreAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var devices = await ReadConnectionSnapshotAsync(timeout.Token).ConfigureAwait(false);
            if (_disposed || _dispatcher.HasShutdownStarted) return;
            await _dispatcher.InvokeAsync(() => ApplyConnectionSnapshot(devices));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { System.Diagnostics.Debug.WriteLine("[Bluetooth] 连接核验暂不可用: " + error.GetType().Name); }
    }

    internal static async Task<BluetoothConnectionSnapshot[]> ReadConnectionSnapshotAsync(CancellationToken token)
    {
        string[] properties = ["System.Devices.Aep.IsConnected", "System.Devices.Aep.DeviceAddress"];
        async Task<DeviceInformationCollection> Read(string selector) => await DeviceInformation.FindAllAsync(selector,
            properties, DeviceInformationKind.AssociationEndpoint).AsTask(token);
        var groups = await Task.WhenAll(Read(BluetoothDevice.GetDeviceSelectorFromPairingState(true)),
            Read(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true))).ConfigureAwait(false);
        return groups.SelectMany(g => g).Take(256).Select(device => new BluetoothConnectionSnapshot(device.Id,
            device.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var address)
                ? (address?.ToString() ?? "").Replace(":", "").Replace("-", "").ToUpperInvariant() : ExtractMacAddress(device.Id),
            CleanDeviceName(device.Name), device.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var connected)
                && connected is bool value ? value : null)).ToArray();
    }

    private void ApplyConnectionSnapshot(IReadOnlyList<BluetoothConnectionSnapshot> devices)
    {
        if (_disposed) return;
        foreach (var pair in _batterySnapshots.ToArray())
        {
            var sample = pair.Value.Device;
            string address = pair.Key.StartsWith("mac:", StringComparison.OrdinalIgnoreCase) ? pair.Key[4..] : "";
            var matches = devices.Where(d => address.Length > 0 && d.Address.Equals(address, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) matches = devices.Where(d => d.Name.Equals(sample.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0 || matches.Any(d => d.Connected == null)) continue;
            if (matches.Any(d => d.Connected == true))
            {
                if (!sample.Connected)
                {
                    _batterySnapshots.TryRemove(pair.Key, out _);
                    var peer = matches.First(d => d.Connected == true);
                    _ = HandleConnectedAsync(peer.Id, peer.Name, Category(sample.Kind), peer.Address, announce: false);
                }
                continue;
            }
            _connectionEpochs.TryRemove(pair.Key, out _);
            foreach (var alias in _connectionKeys.Where(p => p.Value == pair.Key)) _preExistingConnected[alias.Key] = false;
            var info = new BluetoothDeviceInfo(sample.Id, sample.Name, Category(sample.Kind), null, IsConnected: false);
            StoreBatterySnapshot(pair.Key, info);
        }
        // Also publish explicit offline peers not previously seen by a watcher:
        // their plugin may have connected before the main service started.
        foreach (var peer in devices.Where(d => d.Connected == false))
        {
            string key = peer.Address.Length > 0 ? "mac:" + peer.Address : peer.Id;
            if (_batterySnapshots.ContainsKey(key) || devices.Any(d => d.Address.Length > 0 && d.Address == peer.Address && d.Connected == true)) continue;
            StoreBatterySnapshot(key, new(peer.Id, peer.Name, BluetoothDeviceCategory.Other, null, IsConnected: false));
        }
    }

    private static BluetoothDeviceCategory Category(DeviceBatteryKind kind) => kind switch
    { DeviceBatteryKind.Audio => BluetoothDeviceCategory.Audio, DeviceBatteryKind.Keyboard => BluetoothDeviceCategory.Keyboard,
        DeviceBatteryKind.Mouse => BluetoothDeviceCategory.Mouse, DeviceBatteryKind.Gamepad => BluetoothDeviceCategory.Gamepad,
        _ => BluetoothDeviceCategory.Other };

    private void OnDeviceRemoved(string id)
    {
        if (_disposed) return;
        if (_classicDevices.TryRemove(id, out var classic)) classic.Dispose();
        if (_bleDevices.TryRemove(id, out var ble)) ble.Dispose();
        if (_connectionKeys.TryGetValue(id, out var key) && _connectionEpochs.ContainsKey(key)
            && _batterySnapshots.TryGetValue(key, out var sample) && sample.Device.Connected)
            HandleDisconnected(id, sample.Device.Name, Category(sample.Device.Kind));
        _ = RefreshConnectionStateAsync();
    }
}
