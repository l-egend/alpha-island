using AlphaIsland.PluginSdk;

namespace alpha_island.Core;

internal sealed record DeviceBatteryAlert(string Name, bool Full, string Detail, bool Charging = false,
    DeviceBatteryKind Kind = DeviceBatteryKind.Other);

/// <summary>Observed charge transitions only; connection at 100% never arms a full reminder.</summary>
internal sealed class DeviceBatteryReminderPolicy
{
    private sealed class Charge { internal bool Armed, Completed, Charging; internal int? Percent; }
    private sealed class State
    {
        internal readonly HashSet<string> Aliases = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<DeviceBatteryPart, Charge> Parts = new();
        internal int LowBand;
        internal DateTimeOffset Seen;
    }
    private readonly List<State> _states = new();
    internal IReadOnlyList<DeviceBatteryAlert> Observe(IReadOnlyList<DeviceBatteryGroup> groups, DateTimeOffset now)
    {
        _states.RemoveAll(state => now - state.Seen > TimeSpan.FromDays(1));
        var active = new HashSet<State>();
        var alerts = new List<DeviceBatteryAlert>();
        foreach (var group in groups)
        {
            var aliases = group.Readings.Select(r => r.Sample.Device.PhysicalId.Length > 0
                ? r.Sample.Device.Transport + "/" + DeviceBatteryCatalog.Parent(r.Sample)
                : r.Sample.Source + "/" + DeviceBatteryCatalog.Parent(r.Sample)).Append(group.Key).ToArray();
            var matches = _states.Where(s => aliases.Any(s.Aliases.Contains)).ToArray();
            var state = matches.FirstOrDefault() ?? new State();
            if (matches.Length == 0) _states.Add(state);
            foreach (var other in matches.Skip(1))
            {
                state.LowBand = Math.Max(state.LowBand, other.LowBand);
                state.Aliases.UnionWith(other.Aliases);
                foreach (var part in other.Parts) state.Parts.TryAdd(part.Key, part.Value);
                _states.Remove(other);
            }
            state.Aliases.UnionWith(aliases); state.Seen = now; active.Add(state);
            var components = group.Components;
            foreach (var removed in state.Parts.Keys.Where(p => !components.Any(r => r.Part == p)).ToArray()) state.Parts.Remove(removed);
            var full = new List<string>();
            var starting = new List<string>();
            foreach (var reading in components)
            {
                if (!state.Parts.TryGetValue(reading.Part, out var charge)) state.Parts[reading.Part] = charge = new();
                var d = reading.Sample.Device;
                if (d.Charging && !charge.Charging && d.Percent != 100)
                    starting.Add(DeviceBatteryGroup.PartName(reading.Part) + (d.Percent is int value ? $" {value}%" : ""));
                charge.Charging = d.Charging;
                if (d.Percent is not int percent || d.Estimated) { charge.Armed = false; charge.Percent = null; continue; }
                if (percent <= 95 || (percent < 100 && !d.Charging)) charge.Completed = false;
                if (percent == 100 && charge.Armed && charge.Percent is < 100)
                { full.Add(DeviceBatteryGroup.PartName(reading.Part)); charge.Armed = false; charge.Completed = true; }
                else if (percent < 100) charge.Armed = d.Charging && !charge.Completed;
                charge.Percent = percent;
            }
            if (full.Count > 0) alerts.Add(new(group.Name, true, string.Join("、", full) + "已充满（100%）"));
            if (starting.Count > 0) alerts.Add(new(group.Name, false, string.Join("、", starting) + "，正在充电",
                Charging: true, Kind: group.Display.Device.Kind));
            var low = components.Where(r => !r.Sample.Device.Charging && r.Sample.Device.Percent is >= 0 and <= 20).ToArray();
            int band = low.Any(r => r.Sample.Device.Percent <= 10) ? 2 : low.Length > 0 ? 1 : 0;
            if (band > state.LowBand)
            {
                state.LowBand = band;
                alerts.Add(new(group.Name, false, string.Join("、", low.Select(r => DeviceBatteryGroup.PartName(r.Part) + " " + (r.Sample.Device.Estimated ? "约 " : "") + r.Sample.Device.Percent + "%")) + "，请及时充电"));
            }
            else if (components.Count > 0 && components.All(r => r.Sample.Device.Percent is > 25)) state.LowBand = 0;
        }
        foreach (var missing in _states.Where(s => !active.Contains(s))) missing.Parts.Clear();
        while (_states.Count > 128) _states.Remove(_states.MinBy(s => s.Seen)!);
        return alerts;
    }
}
