using L = AlphaIsland.PluginSdk.Localization.UiText;
namespace alpha_island.Core;

internal sealed record HabitDay(string Date, int? WorkStart = null, int? MeetingStart = null, int? MediaStart = null);
internal sealed record HabitSession(string Date, int Minutes, bool Focus);
internal sealed record HabitLearningState(HabitDay[] Days, HabitSession[] Sessions, Dictionary<string, DateTimeOffset> Suppressed, string[] Accepted, Dictionary<string, DateTimeOffset> Offered);
internal sealed record HabitSuggestion(string Id, string Title, string Detail, string ReminderText, int FocusMinutes = 0, int BreakMinutes = 0);
internal sealed record HabitObservation(DateTimeOffset Now, long Stamp, bool Active, bool Work, bool Meeting, bool Media, bool Focus, double? IdleSeconds);

/// <summary>Only coarse local times and durations are retained; no app names, titles or content.</summary>
internal sealed class HabitLearner(TimeZoneInfo zone)
{
    private readonly List<HabitDay> _days = [];
    private readonly List<HabitSession> _sessions = [];
    private readonly Dictionary<string, DateTimeOffset> _suppressed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _offered = new(StringComparer.Ordinal);
    private readonly HashSet<string> _accepted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset Start, long Stamp)> _candidates = new();
    private long _lastStamp;
    private DateTimeOffset _lastAt, _lastWork;
    private double _workSeconds, _focusSeconds;
    internal int ObservedDays => _days.Count;
    internal int CompletedSessions => _sessions.Count;
    internal HabitLearningState Snapshot() => new(_days.ToArray(), _sessions.ToArray(), new(_suppressed), _accepted.ToArray(), new(_offered));
    internal void Restore(HabitLearningState? state, DateTimeOffset now)
    {
        Clear();
        foreach (var day in (state?.Days ?? []).TakeLast(28))
            if (day != null && DateOnly.TryParseExact(day.Date,"yyyy-MM-dd",out _) && !_days.Any(value => value.Date == day.Date)
                && ValidMinute(day.WorkStart) && ValidMinute(day.MeetingStart) && ValidMinute(day.MediaStart)) _days.Add(day);
        foreach (var session in (state?.Sessions ?? []).TakeLast(160))
            if (session != null && DateOnly.TryParseExact(session.Date,"yyyy-MM-dd",out _) && session.Minutes is >= 5 and <= 240) _sessions.Add(session);
        foreach (var pair in (state?.Suppressed ?? []).Take(16)) if (ValidId(pair.Key) && pair.Value > now && pair.Value < now.AddDays(31)) _suppressed[pair.Key] = pair.Value;
        foreach (string id in (state?.Accepted ?? []).Take(16)) if (ValidId(id)) _accepted.Add(id);
        foreach (var pair in (state?.Offered ?? []).Take(16)) if (ValidId(pair.Key) && pair.Value <= now) _offered[pair.Key] = pair.Value;
        Trim(now);
    }
    internal bool Observe(HabitObservation frame)
    {
        bool changed = false;
        double delta = _lastStamp != 0 ? (frame.Stamp - _lastStamp) / 1000d : 0;
        bool continuous = delta is > 0 and <= 90 && Math.Abs((frame.Now - _lastAt).TotalSeconds - delta) < 3;
        if (!continuous) { _candidates.Clear(); _workSeconds = _focusSeconds = 0; _lastWork = default; }
        if (frame.Active)
        {
            changed |= ObserveStart("work", frame.Work, 300, frame);
            changed |= ObserveStart("meeting", frame.Meeting, 120, frame);
            changed |= ObserveStart("media", frame.Media && !frame.Work && !frame.Meeting, 120, frame);
            if (frame.Work && frame.IdleSeconds is < 120)
            { if (continuous) _workSeconds += delta; _lastWork = frame.Now; }
            if (frame.Focus && frame.IdleSeconds is < 120 && continuous) _focusSeconds += delta;
        }
        else _candidates.Clear();
        if (_lastWork != default && (frame.IdleSeconds >= 300 || frame.Now - _lastWork >= TimeSpan.FromMinutes(5) || !frame.Active))
        {
            if (_workSeconds >= 20 * 60)
            { _sessions.Add(new(LocalDate(frame.Now), Math.Clamp((int)Math.Round(_workSeconds / 60), 20, 240), false)); changed = true; }
            _workSeconds = 0; _lastWork = default;
        }
        if (!frame.Focus) _focusSeconds = 0;
        _lastStamp = frame.Stamp; _lastAt = frame.Now; Trim(frame.Now); return changed;
    }
    internal void FocusCompleted(int minutes, DateTimeOffset now)
    {
        if (minutes is >= 5 and <= 240 && _focusSeconds >= minutes * 60 * .75)
            _sessions.Add(new(LocalDate(now), Math.Clamp((int)Math.Round(_focusSeconds / 60), 5, 240), true));
        _focusSeconds = 0; Trim(now);
    }
    private bool ObserveStart(string kind, bool active, int seconds, HabitObservation frame)
    {
        if (!active) { _candidates.Remove(kind); return false; }
        if (!_candidates.TryGetValue(kind, out var candidate))
        { _candidates[kind] = (frame.Now, frame.Stamp); return false; }
        if (frame.Stamp - candidate.Stamp < seconds * 1000L) return false;
        var local = TimeZoneInfo.ConvertTime(candidate.Start, zone);
        string date = local.ToString("yyyy-MM-dd");
        var day = _days.FirstOrDefault(value => value.Date == date) ?? new(date);
        if ((kind == "work" ? day.WorkStart : kind == "meeting" ? day.MeetingStart : day.MediaStart).HasValue) return false;
        // Quarter-hour buckets are enough to learn a useful schedule without retaining a detailed history.
        int minute = (local.Hour * 60 + local.Minute) / 15 * 15;
        var next = kind switch { "work" => day with { WorkStart = minute }, "meeting" => day with { MeetingStart = minute }, _ => day with { MediaStart = minute } };
        _days.Remove(day); _days.Add(next); return true;
    }
    internal HabitSuggestion[] Suggestions(DateTimeOffset now, int currentFocus, int currentBreak)
    {
        Trim(now);
        var output = new List<HabitSuggestion>();
        var today = TimeZoneInfo.ConvertTime(now, zone);
        bool weekday = today.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
        var days = _days.Where(day => DateOnly.TryParseExact(day.Date,"yyyy-MM-dd",out var date)
            && (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) == weekday).ToArray();
        foreach (string kind in new[] {"work","meeting","media"})
        {
            string id = kind + (weekday ? ":weekday" : ":weekend");
            if (!Available(id, now)) continue;
            var values = days.Select(day => kind == "work" ? day.WorkStart : kind == "meeting" ? day.MeetingStart : day.MediaStart).OfType<int>().ToArray();
            if (values.Length < 5) continue;
            var cluster = values.Select(center => values.Where(value => Distance(value, center) <= 30).ToArray()).OrderByDescending(group => group.Length).First();
            if (cluster.Length < 5 || cluster.Length < values.Length * .65) continue;
            int centerMinute = cluster.Order().ElementAt(cluster.Length / 2);
            int reminderMinute = (centerMinute + 1440 - (kind == "meeting" ? 10 : 5)) % 1440;
            string time = $"{reminderMinute / 60:00}:{reminderMinute % 60:00}";
            string frequency = weekday ? L.T("工作日") : L.T("周末");
            string verb = kind switch { "work"=>L.T("开始专注"), "meeting"=>L.T("准备会议"), _=>L.T("放松休息") };
            string repeat = weekday ? L.T("每个工作日") : L.T("每个周末");
            string note = kind == "meeting" ? L.T("仅根据会议设备使用时段推测，不代表已读取日历。") : L.T("仅根据本机已观察到的类别和时段推测，不会自动修改设置。");
            output.Add(new(id, L.F($"要在{frequency}{time}提醒你{verb}吗？"),
                L.F($"最近28天的{cluster.Length}个观察日集中在{centerMinute / 60:00}:{centerMinute % 60:00}附近。{note}"), L.F($"{repeat}{time}提醒我{verb}")));
        }
        foreach (bool focus in new[] { true, false })
        {
            string id = focus ? "focus-duration" : "break-duration";
            if (!Available(id,now)) continue;
            var sessions = _sessions.Where(value => value.Focus == focus).ToArray();
            if (sessions.Length < 5 || sessions.Select(value => value.Date).Distinct().Count() < 3) continue;
            int minutes = Math.Clamp((int)(sessions.Select(value => value.Minutes).Order().ElementAt(sessions.Length / 2) / 5d + .5) * 5, 10, focus ? 120 : 180);
            int current = focus ? currentFocus : currentBreak;
            if (Math.Abs(minutes - current) < (focus ? 5 : 15)) continue;
            output.Add(new(id, focus ? L.F($"将常用专注时长设为{minutes}分钟？") : L.F($"将连续使用休息提醒设为{minutes}分钟？"),
                L.F($"根据{sessions.Length}次、至少3天的有效时长记录提出；只有点击采纳后才改变对应设置。"), "", focus ? minutes : 0, focus ? 0 : minutes));
        }
        return output.ToArray();
    }
    internal bool CanOffer(string id, DateTimeOffset now) => Available(id, now)
        && (!_offered.TryGetValue(id, out var previous) || now - previous >= TimeSpan.FromDays(7))
        && !_offered.Values.Any(value => TimeZoneInfo.ConvertTime(value, zone).Date == TimeZoneInfo.ConvertTime(now,zone).Date);
    internal void Offered(string id, DateTimeOffset now) { if (ValidId(id)) _offered[id] = now; }
    internal void Respond(string id, bool accepted, DateTimeOffset now)
    { if (!ValidId(id)) return; if (accepted) _accepted.Add(id); else _suppressed[id] = now.AddDays(14); }
    internal void Clear()
    { _days.Clear(); _sessions.Clear(); _suppressed.Clear(); _offered.Clear(); _accepted.Clear(); _candidates.Clear(); _lastStamp=0; _lastAt=_lastWork=default; _workSeconds=_focusSeconds=0; }
    internal void Pause() { _candidates.Clear(); _lastStamp=0; _lastAt=_lastWork=default; _workSeconds=_focusSeconds=0; }
    internal void Prune(DateTimeOffset now) => Trim(now);
    private bool Available(string id, DateTimeOffset now) => !_accepted.Contains(id) && (!_suppressed.TryGetValue(id,out var until) || until <= now);
    private void Trim(DateTimeOffset now)
    {
        string cutoff = TimeZoneInfo.ConvertTime(now,zone).AddDays(-27).ToString("yyyy-MM-dd");
        string last = TimeZoneInfo.ConvertTime(now,zone).ToString("yyyy-MM-dd");
        _days.RemoveAll(day => string.CompareOrdinal(day.Date,cutoff)<0 || string.CompareOrdinal(day.Date,last)>0);
        _sessions.RemoveAll(session => string.CompareOrdinal(session.Date,cutoff)<0 || string.CompareOrdinal(session.Date,last)>0);
        while (_sessions.Count>160) _sessions.RemoveAt(0);
    }
    private string LocalDate(DateTimeOffset now) => TimeZoneInfo.ConvertTime(now,zone).ToString("yyyy-MM-dd");
    private static bool ValidMinute(int? minute) => minute == null || minute is >= 0 and < 1440;
    private static int Distance(int a,int b) => Math.Min(Math.Abs(a-b),1440-Math.Abs(a-b));
    private static bool ValidId(string id) => id is "work:weekday" or "work:weekend" or "meeting:weekday" or "meeting:weekend" or "media:weekday" or "media:weekend" or "focus-duration" or "break-duration";
}
