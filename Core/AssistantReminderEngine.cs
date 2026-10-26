using alpha_island.Services;

namespace alpha_island.Core;

/// <summary>All local reminder decisions share one bounded, clock-driven module.</summary>
internal sealed partial class AssistantReminderEngine(TimeZoneInfo zone)
{
    private readonly Dictionary<string, DateTimeOffset> _suppressed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _next = new(StringComparer.Ordinal);
    private readonly List<AssistantReminderChange> _changes = [];
    private readonly List<DeferredAssistantReminder> _deferred = [];
    private AssistantRuleOptions _options = new();
    private AssistantFrame? _previous;
    private DateTimeOffset? _firstObservation;
    private string _reviewDate = "";
    private DateTimeOffset? _breakUntil;
    private double _usageSeconds, _gameSeconds;
    private string? _gameName;
    private DateTimeOffset _gameStarted, _quietStarted;
    private int _gameFinished, _quietFinished, _gameDownloads, _quietDownloads;
    private bool _quiet;
    private readonly HashSet<string> _quietCoveredMessages = new(StringComparer.Ordinal);
    private DateTimeOffset _nextPopup;
    private readonly MeetingGuard _meeting = new();
    private string _meetingKey = "";
    private DateTimeOffset _meetingSince, _meetingAbsent;
    private int? _meetingVolume;
    private bool? _meetingMuted;
    private string _meetingMedia = "";
    private string _meetingOutputDevice = "";
    private bool _meetingPlayed, _meetingStable;
    internal bool MeetingActive => _meetingKey.Length > 0 && _meetingAbsent == default;
    internal void Configure(AssistantRuleOptions options) => _options = options;
    internal AssistantRuleState Snapshot() => new(new(_suppressed), _reviewDate, _breakUntil, _deferred.ToArray());
    internal void Restore(AssistantRuleState? state, DateTimeOffset now)
    {
        if (state == null) return;
        foreach (var item in (state.Suppressed ?? []).Take(32))
            if (AssistantReminderRules.Names.Contains(item.Key) && item.Value > now && item.Value <= now.AddDays(2)) _suppressed[item.Key] = item.Value;
        _reviewDate = state.ReviewDate?.Length == 10 ? state.ReviewDate : "";
        if (state.BreakUntil > now.AddHours(-1) && state.BreakUntil < now.AddHours(1)) _breakUntil = state.BreakUntil;
        foreach (var entry in (state.Deferred ?? []).Take(16))
            if (entry?.Notice?.Reminder is { } reminder && AssistantReminderRules.Valid(reminder) && entry.Notice.Body is { Length: <= 2048 }
                && entry.Notice.Title is { Length: <= 256 } && entry.Notice.Key is { Length: <= 512 } && entry.Notice.Key.StartsWith("assistant:", StringComparison.Ordinal)
                && (entry.Notice.Kind == DesktopEventKind.Assistant || entry.Notice.IsCritical)
                && reminder.Expires > now && entry.Due <= now.AddHours(1)) _deferred.Add(entry);
    }
    internal void Respond(string rule, string action, DateTimeOffset now)
    {
        if (!AssistantReminderRules.Names.Contains(rule)) return;
        if (action is "later" or "today")
        {
            if (action == "today") _deferred.RemoveAll(d => d.Notice.Reminder?.Rule == rule);
            var local = TimeZoneInfo.ConvertTime(now, zone);
            DateTime midnight = DateTime.SpecifyKind(local.Date.AddDays(1), DateTimeKind.Unspecified);
            while (zone.IsInvalidTime(midnight)) midnight = midnight.AddHours(1);
            _suppressed[rule] = action == "today" ? new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight, zone)) : now.AddMinutes(10);
            foreach (var key in _next.Keys.Where(k => k.StartsWith("assistant:" + rule + ":", StringComparison.Ordinal)).ToArray()) _next.Remove(key);
        }
        if (action == "break-five") { _breakUntil = now.AddMinutes(5); _usageSeconds = 0; }
        if (rule == "usage" && action != "later") _usageSeconds = 0;
    }
    internal void Defer(DesktopNotification notice, DateTimeOffset now)
    {
        if (notice.Reminder is not { } reminder || reminder.Expires <= now.AddMinutes(10)) return;
        _deferred.RemoveAll(d => d.Notice.Key == notice.Key);
        _deferred.Add(new(notice, now.AddMinutes(10)));
        while (_deferred.Count > 16) _deferred.RemoveAt(0);
    }
    internal bool AllowPopup(DateTimeOffset now)
    {
        if (now < _nextPopup) return false;
        _nextPopup = now.AddMinutes(Math.Clamp(_options.CooldownMinutes, 1, 60)); return true;
    }
    internal AssistantReminderChange[] Evaluate(AssistantFrame frame)
    {
        _changes.Clear();
        _firstObservation ??= frame.Now;
        foreach (string key in _suppressed.Where(p => p.Value <= frame.Now).Select(p => p.Key).ToArray()) _suppressed.Remove(key);
        if (frame.Locked)
        {
            _usageSeconds = 0; _previous = frame; return [];
        }
        foreach (var notice in frame.Notifications.Where(n => n.Reminder is { Pending: true } reminder && AssistantReminderRules.EngineNames.Contains(reminder.Rule) && (reminder.Expires <= frame.Now || !RuleEnabled(reminder.Rule))))
            Resolve(notice.Key, notice.Reminder!.Expires <= frame.Now ? "提醒已过期，请以当前状态为准" : "此类提醒已关闭");
        foreach (var entry in _deferred.Where(d => d.Due <= frame.Now).ToArray())
        {
            _deferred.Remove(entry);
            var reminder = entry.Notice.Reminder!;
            bool relevant = reminder.Rule switch
            {
                "microphone" => frame.PrivacyFresh && frame.MicrophoneMuted == true && frame.DeviceApps.Any(MeetingGuard.IsMeetingApp),
                "meeting" => frame.PrivacyFresh && frame.DeviceApps.Any(MeetingGuard.IsMeetingApp),
                "agent-wait" => reminder.Actions.Any(a => frame.Agents.Any(agent => agent.Owner == a.Target && agent.State == AgentRunState.ToolWait)),
                "agent-error" => reminder.Actions.Any(a => frame.Agents.Any(agent => agent.Owner == a.Target && agent.State == AgentRunState.Error)),
                "download" => frame.Downloads.Any(d => d.Measuring && entry.Notice.Key.EndsWith(":" + d.Id, StringComparison.Ordinal)),
                "usage" => frame.IdleSeconds is < 300,
                _ => true
            };
            if (relevant && RuleEnabled(reminder.Rule) && !Suppressed(reminder.Rule, frame.Now) && reminder.Expires > frame.Now)
            {
                _next[entry.Notice.Key] = frame.Now.AddMinutes(10);
                _changes.Add(new(entry.Notice with { Created = frame.Now, IsRead = false, IsPinned = reminder.Pending }));
            }
        }
        double elapsed = _previous == null ? 0 : (frame.Stamp - _previous.Stamp) / 1000d;
        bool gap = elapsed is < 0 or > 90 || _previous?.Locked == true
            || _previous != null && Math.Abs((frame.Now - _previous.Now).TotalSeconds - elapsed) > 2;
        if (gap) { elapsed = 0; _usageSeconds = 0; _downloadProgress.Clear(); }
        if (_breakUntil is { } end && frame.Now >= end)
        {
            _breakUntil = null;
            if (_options.Enabled) Put(frame, "break", end.UtcTicks.ToString(), "休息计时结束", "五分钟休息已结束，可以按自己的节奏继续。", false,
                [new("stats", "查看今日活动")], once: true);
        }
        var unread = frame.Notifications.Where(n => n.Kind == DesktopEventKind.Message && !n.IsRead).ToArray();
        bool quiet = frame.Quiet || frame.Focus;
        if (!_quiet && quiet) { _quietStarted = frame.Now; _quietFinished = _quietDownloads = 0; _quietCoveredMessages.Clear(); }
        bool gameEnded = _gameName != null && frame.Game == null;
        if (_gameName == null && frame.Game != null)
        { _gameName = Clean(frame.Game); _gameStarted = frame.Now; _gameSeconds = 0; _gameFinished = _gameDownloads = 0; }
        if (_gameName != null && frame.Game != null) { _gameName = Clean(frame.Game); _gameSeconds += elapsed; }
        ObserveDownloads(frame);
        ObserveAgents(frame);
        if (gameEnded)
        {
            if (_options.Enabled && _options.GameWrap)
            {
                int count = unread.Count(n => n.Created >= _gameStarted);
                string total = frame.DayGameSeconds is { } seconds ? $"，今日累计 {Duration(seconds)}" : "";
                Put(frame, "game", _gameStarted.UtcTicks.ToString(), "游戏总结 · " + _gameName,
                    $"本次已观察 {Duration(_gameSeconds)}{total}。\n当前保留未读 {count} 条；期间 {_gameFinished} 个助手任务结束、{_gameDownloads} 个下载完成。",
                    true, [new("stats", "查看时长"), new("notifications", "查看未读"), new("break-five", "休息5分钟")], once: true);
                if (quiet)
                {
                    _quietCoveredMessages.UnionWith(unread.Where(n => n.Created >= _gameStarted).Select(n => n.Key));
                    _quietFinished = Math.Max(0, _quietFinished - _gameFinished); _quietDownloads = Math.Max(0, _quietDownloads - _gameDownloads);
                }
            }
            _gameName = null;
        }
        if (_quiet && !quiet && !gameEnded && _options.Enabled && _options.QuietDigest)
        {
            int count = unread.Count(n => n.Created >= _quietStarted && !_quietCoveredMessages.Contains(n.Key));
            if (count + _quietFinished + _quietDownloads > 0)
                Put(frame, "digest", _quietStarted.UtcTicks.ToString(), "免打扰／专注结束摘要",
                    $"当前保留未读 {count} 条，期间 {_quietFinished} 个助手任务结束、{_quietDownloads} 个下载完成。",
                    true, [new("notifications", "查看未读"), new("stats", "查看活动")], once: true);
        }
        ObserveMeeting(frame);
        if (_options.Enabled && _options.UsageBreak && frame.IdleSeconds is { } idle)
        {
            if (idle >= 300) { _usageSeconds = 0; Resolve("assistant:usage:current", "已检测到休息间隔"); }
            else if (idle < 120 && _previous?.IdleSeconds < 120) _usageSeconds += elapsed;
            if (_usageSeconds >= Math.Clamp(_options.UsageBreakMinutes, 15, 240) * 60)
                Put(frame, "usage", "current", "连续使用提醒", $"已持续操作电脑约 {Duration(_usageSeconds)}，可以休息一下。闲置五分钟后重新计时。",
                    true, [new("break-five", "休息5分钟")]);
        }
        var localNow = TimeZoneInfo.ConvertTime(frame.Now, zone);
        string today = localNow.ToString("yyyy-MM-dd");
        if (_options.Enabled && _options.DailyReview && !quiet && frame.IdleSeconds is >= 0 and < 300
            && localNow.TimeOfDay >= new TimeSpan(Math.Clamp(_options.ReviewHour, 0, 23), Math.Clamp(_options.ReviewMinute, 0, 59), 0)
            && _reviewDate != today && frame.DaySummary.Length > 0 && !Suppressed("daily", frame.Now))
        {
            _reviewDate = today;
            Put(frame, "daily", today, "今日活动回顾", frame.DaySummary, false,
                [new("stats", "查看时间轴"), new("daily-ai", "让 AI 整理")], once: true);
        }
        _quiet = quiet; _previous = frame;
        while (_next.Count > 256) _next.Remove(_next.OrderBy(p => p.Value).First().Key);
        return _changes.ToArray();
    }
    private void ObserveMeeting(AssistantFrame frame)
    {
        if (!frame.PrivacyFresh) { _meetingAbsent = default; _meeting.Observe([], false, null, frame.Now); return; }
        bool active = frame.DeviceApps.Any(MeetingGuard.IsMeetingApp);
        if (active)
        {
            _meetingAbsent = default;
            if (_meetingKey.Length == 0)
            {
                _meetingKey = frame.Now.UtcTicks.ToString(); _meetingSince = frame.Now;
                var before = _previous is { PrivacyFresh: true } previous && !previous.DeviceApps.Any(MeetingGuard.IsMeetingApp) ? previous : null;
                _meetingVolume = before?.Volume; _meetingMuted = before?.Muted;
                _meetingOutputDevice = before?.OutputDevice ?? "";
                _meetingMedia = before?.MediaSession ?? ""; _meetingPlayed = before?.MediaPlaying == true;
            }
            _meetingStable |= frame.Now - _meetingSince >= TimeSpan.FromSeconds(6);
        }
        else if (_meetingKey.Length > 0 || frame.Notifications.Any(n => n.Reminder is { Pending: true } r && r.Rule is "meeting" or "microphone"))
        {
            if (_meetingAbsent == default) _meetingAbsent = frame.Now;
            if (frame.Now - _meetingAbsent >= TimeSpan.FromSeconds(20))
            {
                Resolve("assistant:microphone:" + _meetingKey, "会议设备占用已结束");
                Resolve("assistant:meeting:" + _meetingKey, "会议设备占用已结束");
                if (_meetingStable && _options.Enabled && _options.MeetingWrap)
                {
                    var actions = new List<AssistantNoticeAction> { new("meeting-notes", "整理会议笔记"), new("stats", "查看时间轴") };
                    if (_meetingVolume != null) actions.Add(new("volume-restore", "恢复会前音量"));
                    if (_meetingPlayed && !_meetingMedia.Equals("") && frame.MediaSession == _meetingMedia && !frame.MediaPlaying) actions.Add(new("media-resume", "继续音乐"));
                    Put(frame, "meeting-wrap", _meetingKey, "会议结束收尾", "已观察到会议应用停止占用设备。可提供笔记整理待办，或选择恢复会前状态。",
                        true, actions.ToArray(), once: true, volume: _meetingVolume, muted: _meetingMuted, media: _meetingMedia, outputDevice: _meetingOutputDevice);
                }
                _meetingKey = ""; _meetingStable = false;
            }
        }
        foreach (var old in frame.Notifications.Where(n => n.Reminder is { Pending: true } r && r.Rule is "meeting" or "microphone"))
            if (old.Reminder!.Rule == "microphone" && frame.MicrophoneMuted == false || !active && _meetingAbsent != default && frame.Now - _meetingAbsent >= TimeSpan.FromSeconds(20))
                Resolve(old.Key, "设备占用或麦克风静音状态已变化");
        var suggestion = _meeting.Observe(frame.DeviceApps, true, frame.MicrophoneMuted, frame.Now);
        if (suggestion != null && (suggestion.Kind == "microphone" ? _options.Microphone : _options.Enabled))
            Put(frame, suggestion.Kind, _meetingKey, suggestion.Title, suggestion.Detail, true,
                [suggestion.Kind == "microphone" ? new("sound", "声音设置") : new("meeting-notes", "整理会议笔记")], once: true);
    }
    private bool Suppressed(string rule, DateTimeOffset now) => _suppressed.TryGetValue(rule, out var until) && until > now;
    internal bool ShouldOffer(string rule, DateTimeOffset now) => RuleEnabled(rule) && !Suppressed(rule, now);
    private bool RuleEnabled(string rule) => rule == "microphone" ? _options.Microphone : _options.Enabled && (rule switch
    {
        "game" => _options.GameWrap, "digest" => _options.QuietDigest, "agent-wait" or "agent-error" or "agent-ended" => _options.AgentFollowup,
        "usage" => _options.UsageBreak, "meeting-wrap" => _options.MeetingWrap, "download" => _options.DownloadStall, "battery" => _options.BatteryContext, "daily" => _options.DailyReview, _ => true
    });
    private void Put(AssistantFrame frame, string rule, string id, string title, string body, bool pending,
        AssistantNoticeAction[] actions, bool once = false, int? volume = null, bool? muted = null, string media = "", string outputDevice = "")
    {
        string key = "assistant:" + rule + ":" + id;
        if (Suppressed(rule, frame.Now) || _next.TryGetValue(key, out var next) && next > frame.Now
            || frame.Notifications.Any(n => (n.Key == key || rule is "meeting" or "microphone" or "usage" && n.Reminder?.Rule == rule)
                && n.Reminder is { Pending: true } reminder && reminder.Expires > frame.Now)) return;
        _next[key] = once ? frame.Now.AddDays(2) : frame.Now.AddMinutes(10);
        var reminder = new AssistantReminder(rule, pending, frame.Now.AddHours(rule is "agent-wait" or "agent-error" or "download" ? 4 : 24), actions, volume, muted, media, outputDevice);
        _changes.Add(new(new(key, "", "Alpha Island · 助手", Clean(title), Clean(body, 2048), frame.Now, DesktopEventKind.Assistant,
            Importance: pending ? NotificationImportance.Important : NotificationImportance.Normal) { Reminder = reminder, IsPinned = pending }));
    }
    private void Resolve(string key, string result) => _changes.Add(new(ResolveKey: key, Result: result));
    internal static string Duration(double seconds) => seconds < 60 ? $"{Math.Max(0, seconds):0} 秒" : seconds < 3600 ? $"{seconds / 60:0} 分钟" : $"{(int)(seconds / 3600)} 小时 {seconds % 3600 / 60:0} 分钟";
    internal static string Clean(string value, int limit = 160)
    { string text = SensitiveTextDetector.Inspect(value).Redacted; return text[..Math.Min(limit, text.Length)]; }
}
