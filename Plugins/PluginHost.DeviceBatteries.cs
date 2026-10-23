using AlphaIsland.PluginSdk;

namespace alpha_island.Plugins;

internal sealed partial class PluginHost
{
    internal event Action? DeviceBatteriesChanged;
    internal IReadOnlyList<DeviceBatterySample> DeviceBatteries => _entries.Where(e => e.Running && e.Context != null)
        .SelectMany(e => e.Context!.BatterySamples).ToArray();

    internal sealed partial class Context
    {
        private DeviceBatterySample[] _batterySamples = [];
        private readonly Dictionary<string, DeviceBatterySample> _legacyBatterySamples = new(StringComparer.OrdinalIgnoreCase);
        internal IEnumerable<DeviceBatterySample> BatterySamples => _batterySamples.Concat(_legacyBatterySamples.Values);

        public void ReportDeviceBatteries(IReadOnlyList<PluginDeviceBattery> devices)
        {
            Dispatcher.VerifyAccess();
            if (_host._disposed || Cancellation.IsCancellationRequested || !ReferenceEquals(_entry.Context, this)) return;
            if (devices == null || devices.Count > 128) throw new ArgumentException("最多接受 128 项设备电池");
            var now = DateTimeOffset.UtcNow;
            _batterySamples = devices.Where(ValidBattery).DistinctBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
                .Select(d => new DeviceBatterySample(PluginId, d, now)).ToArray();
            _host.DeviceBatteriesChanged?.Invoke();
        }

        private static bool ValidBattery(PluginDeviceBattery? d) => d != null && !string.IsNullOrWhiteSpace(d.Id)
            && d.Id.Length <= 1024 && !string.IsNullOrWhiteSpace(d.Name) && d.Name.Length <= 256
            && d.Detail is { Length: <= 1024 } && d.PhysicalId is { Length: <= 1024 }
            && d.Level is { Length: <= 64 } && d.Percent is not (< 0 or > 100)
            && d.Millivolts is not (< 0 or > 20000) && Enum.IsDefined(d.Transport) && Enum.IsDefined(d.Kind);

        private void CaptureLegacyBattery(PluginBluetoothBattery battery)
        {
            if (!battery.Connected) _legacyBatterySamples.Remove(battery.Id);
            else _legacyBatterySamples[battery.Id] = new(PluginId, new(battery.Id, battery.Name,
                DeviceBatteryTransport.Bluetooth, DeviceBatteryKind.Audio, battery.Percent, true, battery.Charging,
                "来自耳机插件"), DateTimeOffset.UtcNow, Expires: false);
            _host.DeviceBatteriesChanged?.Invoke();
        }
    }
}
