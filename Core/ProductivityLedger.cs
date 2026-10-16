namespace alpha_island.Core;

[Flags]
internal enum ProductivityActivity { None = 0, Music = 1, Game = 2, Focus = 4, Agent = 8 }
internal sealed record ProductivityDay(string Date, double MusicSeconds = 0, double GameSeconds = 0,
    double FocusSeconds = 0, double AgentSeconds = 0)
{
    internal double Seconds(ProductivityActivity activity) => activity switch
    { ProductivityActivity.Music => MusicSeconds, ProductivityActivity.Game => GameSeconds,
        ProductivityActivity.Focus => FocusSeconds, ProductivityActivity.Agent => AgentSeconds, _ => 0 };
}

/// <summary>Calendar aggregates only. Categories overlap; gaps, clock changes and suspend never invent activity.</summary>
internal sealed class ProductivityLedger(TimeZoneInfo zone)
{
    private readonly Dictionary<DateOnly, ProductivityDay> _days = new();
    private DateTimeOffset? _previous;
    private long _previousTimestamp;
    private ProductivityActivity _activity;
    private bool _enabled, _blocked;
    internal bool Advance(DateTimeOffset now, long monotonicMilliseconds)
    {
        bool changed = false;
        if (_previous is { } previous)
        {
            long elapsed = monotonicMilliseconds - _previousTimestamp;
            double clockElapsed = (now - previous).TotalMilliseconds;
            if (_enabled && !_blocked && _activity != ProductivityActivity.None && elapsed is > 0 and <= 90_000
                && Math.Abs(clockElapsed - elapsed) < 2000)
            {
                var cursor = previous;
                while (cursor < now)
                {
                    var local = TimeZoneInfo.ConvertTime(cursor, zone);
                    var date = DateOnly.FromDateTime(local.DateTime);
                    DateTime midnight = DateTime.SpecifyKind(date.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
                    // Some time zones advance their clock at midnight.
                    while (zone.IsInvalidTime(midnight)) midnight = midnight.AddHours(1);
                    var boundary = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight, zone), TimeSpan.Zero);
                    var end = boundary < now && boundary > cursor ? boundary : now;
                    double seconds = Math.Max(0, (end - cursor).TotalSeconds);
                    var day = _days.GetValueOrDefault(date) ?? new(date.ToString("yyyy-MM-dd"));
                    _days[date] = day with
                    {
                        MusicSeconds = Add(day.MusicSeconds, ProductivityActivity.Music),
                        GameSeconds = Add(day.GameSeconds, ProductivityActivity.Game),
                        FocusSeconds = Add(day.FocusSeconds, ProductivityActivity.Focus),
                        AgentSeconds = Add(day.AgentSeconds, ProductivityActivity.Agent)
                    };
                    double Add(double value, ProductivityActivity kind) => Math.Min(86400, value + (_activity.HasFlag(kind) ? seconds : 0));
                    cursor = end; changed = true;
                }
            }
        }
        _previous = now; _previousTimestamp = monotonicMilliseconds;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        foreach (var stale in _days.Keys.Where(day => day < today.AddDays(-89) || day > today.AddDays(1)).ToArray())
        { _days.Remove(stale); changed = true; }
        return changed;
    }
    internal bool SetState(ProductivityActivity activity, bool enabled, bool blocked, DateTimeOffset now, long timestamp)
    { bool changed = Advance(now, timestamp); _activity = activity; _enabled = enabled; _blocked = blocked; return changed; }
    internal ProductivityDay[] Snapshot() => _days.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
    internal void Restore(IEnumerable<ProductivityDay> days)
    {
        foreach (var day in days.Take(91))
            if (day != null && DateOnly.TryParseExact(day.Date, "yyyy-MM-dd", out var date)
                && new[] { day.MusicSeconds, day.GameSeconds, day.FocusSeconds, day.AgentSeconds }.All(value => double.IsFinite(value) && value is >= 0 and <= 86400))
                _days[date] = day;
    }
    internal void Clear(DateTimeOffset now, long timestamp) { _days.Clear(); _previous = now; _previousTimestamp = timestamp; }
}
