namespace alpha_island.Core;

internal sealed record TimelineEntry(string Id, string Kind, string Name, DateTimeOffset Started,
    DateTimeOffset? Ended = null, bool Interrupted = false);
internal sealed record TimelineState(TimelineEntry[] Entries, DateTimeOffset LastObserved);

/// <summary>Records observed intervals only. Sleep, process gaps and clock changes never invent activity.</summary>
internal sealed class TimelineLedger(TimeZoneInfo zone)
{
    private readonly List<TimelineEntry> _entries = new();
    private readonly Dictionary<string, TimelineEntry> _active = new();
    private DateTimeOffset _last;
    private long _stamp;
    internal int RetentionDays { get; set; } = 7;
    internal TimelineState Snapshot() => new(_entries.ToArray(), _last);
    internal void Restore(TimelineState? state)
    {
        if (state?.Entries == null) return;
        foreach (var entry in state.Entries.TakeLast(1500))
        {
            if (entry == null || entry.Id == null || entry.Kind == null || entry.Name == null || entry.Id.Length > 128 || entry.Kind.Length > 40 || entry.Name.Length > 160
                || entry.Started > DateTimeOffset.UtcNow.AddDays(1) || entry.Ended < entry.Started) continue;
            _entries.Add(entry.Ended == null ? entry with { Ended = state.LastObserved < entry.Started ? entry.Started : state.LastObserved, Interrupted = true } : entry);
        }
        _last = state.LastObserved;
    }
    internal void Observe(Dictionary<string, (string Kind, string Name)> activities, DateTimeOffset now, long stamp)
    {
        bool gap = _stamp != 0 && (stamp - _stamp is < 0 or > 90000 || Math.Abs((now - _last).TotalMilliseconds - (stamp - _stamp)) > 2000);
        if (gap) CloseAll(_last, interrupted: true);
        foreach (var pair in _active.ToArray())
        {
            bool sameDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(pair.Value.Started, zone).DateTime)
                == DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
            if (!sameDay)
            {
                var midnight = TimeZoneInfo.ConvertTime(now, zone).Date;
                while (zone.IsInvalidTime(midnight)) midnight = midnight.AddHours(1);
                var boundary = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(midnight, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
                Close(pair.Key, boundary < now ? boundary : now, false);
            }
            else if (!activities.TryGetValue(pair.Key, out var current) || (current.Kind, current.Name) != (pair.Value.Kind, pair.Value.Name)) Close(pair.Key, now, false);
        }
        foreach (var pair in activities)
        {
            if (_active.ContainsKey(pair.Key)) continue;
            var entry = new TimelineEntry(Guid.NewGuid().ToString("N"), pair.Value.Kind, Clip(pair.Value.Name), now);
            _entries.Add(entry); _active.Add(pair.Key, entry);
        }
        _last = now; _stamp = stamp; Prune(now);
    }
    internal void AddBoot(DateTimeOffset started)
    {
        string id = "boot-" + started.UtcTicks;
        if (_entries.Any(e => e.Id == id)) return;
        _entries.Add(new(id, "boot", "系统启动", started, started));
        Prune(DateTimeOffset.UtcNow);
    }
    internal void CloseAll(DateTimeOffset now, bool interrupted = false)
    { foreach (string key in _active.Keys.ToArray()) Close(key, now, interrupted); _last = now; _stamp = 0; }
    private void Close(string key, DateTimeOffset end, bool interrupted)
    {
        var item = _active[key]; int index = _entries.IndexOf(item);
        if (index >= 0) _entries[index] = item with { Ended = end < item.Started ? item.Started : end, Interrupted = interrupted };
        _active.Remove(key);
    }
    private void Prune(DateTimeOffset now)
    {
        var cutoff = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime).AddDays(1 - Math.Clamp(RetentionDays, 1, 30));
        _entries.RemoveAll(e => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(e.Ended ?? now, zone).DateTime) < cutoff);
        while (_entries.Count > 1500) { var old = _entries.FindIndex(e => e.Ended != null); if (old < 0) break; _entries.RemoveAt(old); }
    }
    internal void Clear(DateTimeOffset now) { _entries.Clear(); _active.Clear(); _last = now; _stamp = 0; }
    internal void Trim(DateTimeOffset now) => Prune(now);
    private static string Clip(string name) => name.Length <= 160 ? name : name[..160];
}
