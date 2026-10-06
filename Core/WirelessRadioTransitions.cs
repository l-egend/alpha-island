using Windows.Devices.Radios;

namespace alpha_island.Core;

internal sealed class WirelessRadioTransitions
{
    private readonly Dictionary<RadioKind, bool?> _previous = new();
    internal static bool? Aggregate(IEnumerable<RadioState> states)
    {
        var snapshot = states.ToArray();
        if (snapshot.Contains(RadioState.On)) return true;
        if (snapshot.Length == 0 || snapshot.Any(s => s == RadioState.Unknown)) return null;
        return snapshot.All(s => s is RadioState.Off or RadioState.Disabled) ? false : null;
    }
    internal bool Apply(RadioKind kind, bool? state)
    {
        _previous.TryGetValue(kind, out bool? old);
        _previous[kind] = state;
        return old.HasValue && state.HasValue && old != state;
    }
    internal void Reset() => _previous.Clear();
}
