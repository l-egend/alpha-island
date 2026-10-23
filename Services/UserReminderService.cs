using System.Text.Json;
using System.Windows.Threading;
using AlphaIsland.PluginSdk;
using alpha_island.Core;

namespace alpha_island.Services;

internal sealed class UserReminderService : IDisposable
{
    private readonly UserReminderQueue _queue = new();
    private readonly LocalStateFile<UserReminderState>? _file;
    private readonly IslandTicker.Subscription? _tick;
    private readonly Func<ReminderDelivery, bool> _deliver;
    private readonly TimeZoneInfo _zone;
    private bool _enabled = true, _disposed, _ticking;
    internal event Action? Changed;
    internal event Action<string>? Cancelled;
    internal UserReminder[] Jobs => _queue.Jobs.OrderByDescending(job => job.Active).ThenBy(job => job.Due).ToArray();
    internal bool Enabled => _enabled;
    internal UserReminderService(IslandTicker ticker, string? path, Func<ReminderDelivery, bool> deliver, TimeZoneInfo? zone = null)
    {
        _deliver = deliver; _zone = zone ?? TimeZoneInfo.Local;
        if (path == null) return;
        _file = new(path, maximumBytes: 512 * 1024, encrypted: true);
        _queue.Restore(_file.Read(), DateTimeOffset.UtcNow);
        _tick = ticker.Subscribe("user-reminders", TimeSpan.FromSeconds(1), TickNow);
        Pace();
    }
    internal void Configure(bool enabled)
    {
        _enabled = enabled; Pace(); Changed?.Invoke();
    }
    internal ToolResult Create(string text)
    {
        if (_disposed || !_enabled) return ToolResult.Fail("自然语言提醒已关闭，可在智能助手设置中开启。");
        var now = DateTimeOffset.Now;
        var parsed = ReminderTextParser.Parse(text, now, _zone);
        if (parsed.Draft == null) return ToolResult.Fail(parsed.Error.Length > 0 ? parsed.Error : "请写明时间和事项，例如：30分钟后提醒我喝水。");
        try
        {
            var job = _queue.Add(parsed.Draft, now); Save(); Pace(); Changed?.Invoke();
            string repeat = job.IntervalMinutes > 0 ? $" · 每隔{job.IntervalMinutes}分钟" : job.Weekdays.Length == 7 ? " · 每天" : job.Weekdays.SequenceEqual(new[] {1,2,3,4,5}) ? " · 工作日" : job.Weekdays.Length > 0 ? " · 每周指定日期" : "";
            string due = TimeZoneInfo.ConvertTime(job.Due, _zone).ToString("yyyy-MM-dd HH:mm:ss");
            return ToolResult.Ok($"已设置：{due}{repeat}，提醒你「{SensitiveTextDetector.Inspect(job.Title).Redacted}」。提醒保存在本机，程序需保持运行；休眠或退出期间错过的提醒会在恢复运行后补入通知中心。", new { id = job.Id, due = job.Due, repeat });
        }
        catch (ArgumentException ex) { return ToolResult.Fail(ex.Message); }
    }
    internal ToolResult List()
    {
        var jobs = Jobs.Where(job => job.Active).Take(100).ToArray();
        string Title(UserReminder job) { var text=SensitiveTextDetector.Inspect(job.Title).Redacted;return text.Length>80?text[..80]+"…":text; }
        return ToolResult.Ok(jobs.Length == 0 ? "目前没有待办提醒。" : string.Join("\n", jobs.Take(10).Select(job =>
            $"{job.Id} · {TimeZoneInfo.ConvertTime(job.Due, _zone):MM-dd HH:mm} · {Title(job)}"))+(jobs.Length>10?"\n更多提醒请打开管理提醒查看。":""),
            jobs.Take(10).Select(job => new { id = job.Id, title = Title(job), due = job.Due }).ToArray());
    }
    internal ToolResult Cancel(string id)
    {
        if (_disposed || !_queue.Cancel(id)) return ToolResult.Fail("未找到这条提醒，请先查看提醒列表。");
        Save(); Pace(); Cancelled?.Invoke(id); Changed?.Invoke(); return ToolResult.Ok("已取消这条提醒。");
    }
    internal ToolResult CancelByTitle(string title)
    {
        var matches = Jobs.Where(job => job.Active && job.Title == title).ToArray();
        return matches.Length == 1 ? Cancel(matches[0].Id) : ToolResult.Fail(matches.Length == 0 ? "未找到完全同名的待办提醒。可打开管理提醒查看。" : "有多条同名提醒，请在管理提醒中选择具体一条，或提供提醒ID。");
    }
    internal ToolResult Snooze(string id, int minutes)
    {
        if (_disposed || !_enabled) return ToolResult.Fail("提醒已关闭。");
        try { _queue.Snooze(id, minutes, DateTimeOffset.Now); Save(); Pace(); Changed?.Invoke(); return ToolResult.Ok($"已设为{Math.Clamp(minutes,1,1440)}分钟后再提醒。"); }
        catch (ArgumentException ex) { return ToolResult.Fail(ex.Message); }
    }
    internal async void TickNow()
    {
        if (_disposed || !_enabled || _ticking) return;
        _ticking = true;
        try
        {
            bool changed = _queue.Advance(DateTimeOffset.Now);
            if (changed) Save();
            if (_queue.Pending.Length > 0 && _file != null)
            {
                bool persisted = await Task.Run(() => _file.Flush(TimeSpan.FromSeconds(3)));
                if (!persisted || _disposed || !_enabled) return;
            }
            foreach (var pending in _queue.Pending)
            {
                if (!_deliver(pending)) continue;
                _queue.Acknowledge(pending.Key); changed = true;
            }
            if (changed) { Save(); Changed?.Invoke(); }
            Pace();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { IslandLog.Warn("Reminders", "本地提醒暂不可用：" + ex.GetType().Name); }
        finally { _ticking = false; }
    }
    private void Pace() => _tick?.SetPaused(!_enabled || !_queue.Jobs.Any(job => job.Active) && _queue.Pending.Length == 0);
    private void Save() => _file?.Write(_queue.Snapshot());
    public void Dispose() { if (_disposed) return; _disposed = true; _tick?.Dispose(); Save(); _file?.Dispose(); Changed = null; Cancelled = null; }
}
