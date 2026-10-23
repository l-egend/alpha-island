using AlphaIsland.PluginSdk;

namespace alpha_island.Core;

internal sealed record DailyTrafficDay(DateOnly Date, long Down, long Up);
internal sealed record DailyTrafficState(DailyTrafficDay[] Days);

/// <summary>One byte ledger across source changes; baselines are never persisted as OS counters.</summary>
internal sealed class DailyTrafficLedger
{
    private readonly Dictionary<DateOnly, DailyTrafficDay> _days = new();
    private DateOnly _date;
    private bool _primed;
    private long _localDown, _localUp;
    private DateTimeOffset _externalAt;
    private string _externalId = "";
    internal string Source { get; private set; } = "本机采样";
    internal DailyTrafficDay Current => _days.TryGetValue(_date, out var row) ? row : new(_date, 0, 0);
    internal DailyTrafficLedger(DailyTrafficState? saved = null)
    {
        foreach (var day in saved?.Days?.Take(32) ?? [])
            if (day != null && day.Down >= 0 && day.Up >= 0) _days[day.Date] = day;
    }
    internal void Observe(DateTimeOffset now, long localDown, long localUp, string externalId = "", PluginNetworkSample? external = null)
    {
        if (localDown < 0 || localUp < 0) return;
        var date = DateOnly.FromDateTime(now.DateTime);
        if (date != _date)
        {
            _date = date; _primed = false; _externalId = ""; _externalAt = default;
            foreach (var old in _days.Keys.Where(day => day > date || day < date.AddDays(-31)).ToArray()) _days.Remove(old);
        }
        var row = Current;
        bool trusted = externalId.StartsWith("alpha.trafficmonitor/", StringComparison.OrdinalIgnoreCase)
            && PluginNetworkRules.IsUsable(external, now) && external?.TodayDownBytes is >= 0 && external.TodayUpBytes is >= 0
            && DateOnly.FromDateTime(external.Timestamp.ToOffset(now.Offset).DateTime) == date;
        if (trusted)
        {
            if (_externalId != externalId || external!.Timestamp > _externalAt)
            {
                _days[date] = new(date, external!.TodayDownBytes!.Value, external.TodayUpBytes!.Value);
                _localDown = localDown; _localUp = localUp; _primed = true;
                _externalAt = external.Timestamp; _externalId = externalId;
            }
            Source = "TrafficMonitor";
            return;
        }
        // Duplicate bridge frames keep the last local anchor. On disconnection
        // this includes bytes since the last fresh report, without a five-second gap.
        if (_primed)
            row = new(date, Add(row.Down, localDown >= _localDown ? localDown - _localDown : 0),
                Add(row.Up, localUp >= _localUp ? localUp - _localUp : 0));
        _days[date] = row; _localDown = localDown; _localUp = localUp; _primed = true;
        _externalId = ""; Source = "本机采样";
    }
    internal DailyTrafficState Export() => new(_days.Values.OrderByDescending(day => day.Date).Take(32).ToArray());
    private static long Add(long total, long delta) => delta > long.MaxValue - total ? long.MaxValue : total + delta;
}
