using AlphaIsland.PluginSdk;

namespace alpha_island.Core;

internal static class DeviceBatteryCatalog
{
    internal static IReadOnlyList<DeviceBatteryGroup> Group(IEnumerable<DeviceBatterySample> samples, DateTimeOffset now)
    {
        var entries = Merge(samples, now).Select(s => new Entry(s, Part(s))).ToArray();
        var parts = entries.Where(e => e.PluginAudio).GroupBy(e => e.Sample.Source + "/" + e.Parent, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DeviceBatteryGroup(g.Key, g.First().BaseName, g.Select(e => new DeviceBatteryReading(e.Sample, e.Part ?? DeviceBatteryPart.Whole)).ToArray())).ToList();
        var wholes = entries.Where(e => !e.PluginAudio).ToList();
        var result = new List<DeviceBatteryGroup>();
        foreach (var part in parts)
        {
            var matches = wholes.Where(e => e.Sample.Device.Transport == DeviceBatteryTransport.Bluetooth
                && e.Sample.Device.Kind == DeviceBatteryKind.Audio
                && part.Readings.Any(r => r.Sample.Device.PhysicalId.Length > 0
                    && Parent(r.Sample).Equals(e.Sample.Device.PhysicalId, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (matches.Length == 0)
            {
                var names = wholes.Where(e => e.Sample.Source == "bluetooth" && e.Sample.Device.Kind == DeviceBatteryKind.Audio
                    && e.Sample.Device.Transport == DeviceBatteryTransport.Bluetooth && e.Sample.Device.Name.Trim().Equals(part.Name.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
                if (names.Length == 1 && parts.Count(p => p.Name.Trim().Equals(part.Name.Trim(), StringComparison.OrdinalIgnoreCase)) == 1) matches = names;
            }
            if (matches.Length == 1)
            {
                var native = matches[0]; wholes.Remove(native);
                result.Add(part with { Key = Key(native.Sample), Readings = part.Readings.Append(new(native.Sample, DeviceBatteryPart.Whole)).ToArray() });
            }
            else result.Add(part);
        }
        result.AddRange(wholes.Select(e => new DeviceBatteryGroup(Key(e.Sample), e.Sample.Device.Name, [new(e.Sample, DeviceBatteryPart.Whole)])));
        return result.OrderByDescending(g => g.Readings.Any(r => r.Sample.Device.Connected &&
                (r.Sample.Device.Percent.HasValue || r.Sample.Device.Charging || !string.IsNullOrWhiteSpace(r.Sample.Device.Level))))
            .ThenBy(g => g.Display.Device.Transport).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private sealed record Entry(DeviceBatterySample Sample, DeviceBatteryPart? Part)
    {
        internal string Parent => DeviceBatteryCatalog.Parent(Sample);
        internal bool PluginAudio => Sample.Source != "bluetooth" && Sample.Device.Kind == DeviceBatteryKind.Audio && Sample.Device.Transport == DeviceBatteryTransport.Bluetooth;
        internal string BaseName => Part != null && Sample.Device.Name.Contains(" · ") ? Sample.Device.Name[..Sample.Device.Name.LastIndexOf(" · ", StringComparison.Ordinal)] : Sample.Device.Name;
    }
    internal static DeviceBatteryPart? Part(DeviceBatterySample sample)
    {
        if (sample.Device.Transport != DeviceBatteryTransport.Bluetooth || sample.Device.Kind != DeviceBatteryKind.Audio || sample.Source == "bluetooth") return null;
        return sample.Device.Id[(sample.Device.Id.LastIndexOf('/') + 1)..].ToLowerInvariant() switch
        {
            "left" => sample.Device.Name.EndsWith("耳机电量", StringComparison.Ordinal) ? DeviceBatteryPart.Whole : DeviceBatteryPart.Left,
            "right" => DeviceBatteryPart.Right, "case" => DeviceBatteryPart.Case, _ => null
        };
    }
    internal static string Parent(DeviceBatterySample sample)
    {
        string value = sample.Device.PhysicalId.Length > 0 ? sample.Device.PhysicalId : sample.Device.Id;
        if (Part(sample) == null) return value;
        int slash = value.LastIndexOf('/');
        return slash >= 0 && value[(slash + 1)..].ToLowerInvariant() is "left" or "right" or "case" ? value[..slash] : value;
    }
    internal static IReadOnlyList<DeviceBatterySample> Merge(IEnumerable<DeviceBatterySample> samples, DateTimeOffset now)
    {
        var current = samples.GroupBy(s => (s.Source.ToUpperInvariant(), s.Device.Id.ToUpperInvariant(), s.Device.PhysicalId.ToUpperInvariant()))
            .Select(g => g.OrderByDescending(s => s.UpdatedAt).First()).ToArray();
        var windows = current.Where(s => s.Source == "bluetooth" && s.Device.Transport == DeviceBatteryTransport.Bluetooth).ToArray();
        bool Disconnected(DeviceBatterySample sample)
        {
            if (sample.Device.Transport != DeviceBatteryTransport.Bluetooth) return false;
            var matches = windows.Where(s => sample.Device.PhysicalId.Length > 0
                && Parent(s).Equals(Parent(sample), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0)
            {
                string name = new Entry(sample, Part(sample)).BaseName.Trim();
                matches = windows.Where(s => s.Device.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            }
            return matches.Length > 0 && matches.All(s => !s.Device.Connected);
        }
        return current.Where(s => s.Device.Connected && !Disconnected(s))
            .Select(s => s.Expires && now - s.UpdatedAt > TimeSpan.FromMinutes(3)
                ? s with { Device = s.Device with { Percent = null, Charging = false, Level = "", Millivolts = null,
                    Detail = "电量读数已过期，等待设备重新上报" } } : s)
            .GroupBy(s => Key(s), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(s => s.Device.Percent.HasValue)
                .ThenByDescending(s => s.Device.Transport == DeviceBatteryTransport.Bluetooth && s.Device.Kind == DeviceBatteryKind.Audio && s.Source != "bluetooth")
                .ThenByDescending(s => s.UpdatedAt).First())
            .OrderBy(s => s.Device.Transport).ThenBy(s => s.Device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static string Key(DeviceBatterySample sample) => sample.Device.PhysicalId.Length > 0
        ? sample.Device.Transport + "/" + sample.Device.PhysicalId + (Part(sample) is { } part ? "/" + part : "")
        : sample.Source + "/" + sample.Device.Id;
}
