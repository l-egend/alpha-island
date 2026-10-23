using alpha_island.Services;

namespace alpha_island.Core;

internal sealed record UserReminder(string Id, string Title, DateTimeOffset Due, string ZoneId,
    int Hour, int Minute, int[] Weekdays, int IntervalMinutes, DateTimeOffset Created, bool Active = true);
internal sealed record ReminderDelivery(string Key, string JobId, string Title, DateTimeOffset Due, bool Late);
internal sealed record UserReminderState(UserReminder[] Jobs, ReminderDelivery[] Pending);

/// <summary>Bounded reminder jobs and a persistent outbox. Repeats coalesce after sleep instead of flooding.</summary>
internal sealed class UserReminderQueue
{
    private readonly List<UserReminder> _jobs = [];
    private readonly List<ReminderDelivery> _outbox = [];
    internal IReadOnlyList<UserReminder> Jobs => _jobs;
    internal ReminderDelivery[] Pending => _outbox.ToArray();
    internal UserReminderState Snapshot() => new(_jobs.ToArray(), _outbox.ToArray());
    internal void Restore(UserReminderState? state, DateTimeOffset now)
    {
        _jobs.Clear(); _outbox.Clear();
        foreach (var job in (state?.Jobs ?? []).Take(200))
        {
            if (job == null || !Valid(job) || _jobs.Any(existing => existing.Id == job.Id) || job.Active && _jobs.Count(existing => existing.Active) >= 100
                || job.Created > now.AddDays(1) || job.Due > now.AddDays(367) || !job.Active && job.Due < now.AddDays(-7)) continue;
            try { _ = TimeZoneInfo.FindSystemTimeZoneById(job.ZoneId); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { continue; }
            _jobs.Add(job with { Weekdays = job.Weekdays.Distinct().Order().ToArray() });
        }
        foreach (var item in (state?.Pending ?? []).Take(100))
            if (item != null && _jobs.FirstOrDefault(job => job.Id == item.JobId) is { } owner
                && item.Key == DeliveryKey(owner.Id, item.Due) && item.Title == owner.Title
                && item.Due <= now.AddMinutes(1) && !_outbox.Any(existing => existing.JobId == item.JobId)) _outbox.Add(item);
    }
    internal UserReminder Add(ReminderDraft draft, DateTimeOffset now)
    {
        if (draft.Due <= now || draft.Due > now.AddDays(366)) throw new ArgumentException("提醒时间应在未来一年内。");
        var local = TimeZoneInfo.ConvertTime(draft.Due, TimeZoneInfo.FindSystemTimeZoneById(draft.ZoneId));
        var job = new UserReminder(Guid.NewGuid().ToString("N"), draft.Title, draft.Due, draft.ZoneId,
            local.Hour, local.Minute, draft.Weekdays.Distinct().Order().ToArray(), draft.IntervalMinutes, now);
        if (!Valid(job)) throw new ArgumentException("提醒内容或时间规则无效。");
        var existing = _jobs.FirstOrDefault(value => value.Active && value.Title == job.Title && Math.Abs((value.Due - job.Due).TotalSeconds) < 2
            && value.ZoneId == job.ZoneId && value.IntervalMinutes == job.IntervalMinutes && value.Weekdays.SequenceEqual(job.Weekdays));
        if (existing != null) return existing;
        if (_jobs.Count(value => value.Active) >= 100) throw new ArgumentException("最多同时保存100条待办提醒，请先完成或取消一些。");
        _jobs.Add(job); Trim(now); return job;
    }
    internal bool Cancel(string id)
    {
        bool removed = _jobs.RemoveAll(job => job.Id == id) > 0;
        _outbox.RemoveAll(item => item.JobId == id); return removed;
    }
    internal UserReminder Snooze(string id, int minutes, DateTimeOffset now)
    {
        var job = _jobs.FirstOrDefault(value => value.Id == id) ?? throw new ArgumentException("提醒已删除或不存在。");
        return Add(new(job.Title, now.AddMinutes(Math.Clamp(minutes, 1, 1440)), job.ZoneId, []), now);
    }
    internal bool Advance(DateTimeOffset now)
    {
        bool changed = false;
        for (int i = 0; i < _jobs.Count; i++)
        {
            var job = _jobs[i];
            if (!job.Active || job.Due > now) continue;
            if (!_outbox.Any(item => item.JobId == job.Id))
                _outbox.Add(new(DeliveryKey(job.Id, job.Due), job.Id, job.Title, job.Due, now - job.Due > TimeSpan.FromMinutes(2)));
            DateTimeOffset? next = null;
            if (job.IntervalMinutes > 0)
            {
                long interval = TimeSpan.FromMinutes(job.IntervalMinutes).Ticks;
                next = job.Due.AddTicks(((now.UtcTicks - job.Due.UtcTicks) / interval + 1) * interval);
            }
            else if (job.Weekdays.Length > 0)
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(job.ZoneId);
                next = ReminderTextParser.NextLocal(zone, job.Hour, job.Minute, job.Weekdays, now, TimeZoneInfo.ConvertTime(job.Due, zone).Date);
            }
            _jobs[i] = job with { Due = next ?? job.Due, Active = next.HasValue }; changed = true;
        }
        Trim(now); return changed;
    }
    internal void Acknowledge(string key) => _outbox.RemoveAll(item => item.Key == key);
    private void Trim(DateTimeOffset now)
    {
        _jobs.RemoveAll(job => !job.Active && job.Due < now.AddDays(-7) && !_outbox.Any(item => item.JobId == job.Id));
        while (_jobs.Count > 200)
        {
            int index = _jobs.FindIndex(job => !job.Active && !_outbox.Any(item => item.JobId == job.Id));
            if (index < 0) break;
            _jobs.RemoveAt(index);
        }
    }
    internal static string DeliveryKey(string id, DateTimeOffset due) => "user-reminder:" + id + ":" + due.UtcTicks;
    private static bool Valid(UserReminder job) => job.Id is { Length: 32 } && Guid.TryParseExact(job.Id, "N", out _)
        && job.Title is { Length: > 0 and <= 160 } && !job.Title.Any(char.IsControl)
        && job.ZoneId is { Length: > 0 and <= 128 } && job.Hour is >= 0 and < 24 && job.Minute is >= 0 and < 60
        && job.Weekdays is { Length: <= 7 } && job.Weekdays.All(day => day is >= 0 and <= 6)
        && job.IntervalMinutes is >= 0 and <= 10080 && !(job.IntervalMinutes > 0 && job.Weekdays.Length > 0);
}
