namespace alpha_island.Core;

/// <summary>Local notification data, never executable commands or model-supplied actions.</summary>
public sealed record AssistantNoticeAction(string Id, string Label, string Target = "");
public sealed record AssistantReminder(string Rule, bool Pending, DateTimeOffset Expires,
    AssistantNoticeAction[] Actions, int? Volume = null, bool? Muted = null, string MediaSession = "", string OutputDevice = "");

internal sealed record AssistantAgent(string Owner, long ActivityId, AgentRunState State, string Label);
internal sealed record AssistantDownload(string Id, string Label, long Bytes, bool Measuring, bool Completed);
internal sealed record AssistantFrame(DateTimeOffset Now, long Stamp, bool Locked, bool Quiet, bool Focus,
    string? Game, double? IdleSeconds, bool PrivacyFresh, bool? MicrophoneMuted, string[] DeviceApps,
    string MediaSession, bool MediaPlaying, int? Volume, bool? Muted,
    AssistantAgent[] Agents, AssistantDownload[] Downloads, DesktopNotification[] Notifications,
    string DaySummary, double? DayGameSeconds, string OutputDevice = "");
internal sealed record AssistantRuleState(Dictionary<string, DateTimeOffset> Suppressed, string ReviewDate = "",
    DateTimeOffset? BreakUntil = null, DeferredAssistantReminder[]? Deferred = null);
internal sealed record DeferredAssistantReminder(DesktopNotification Notice, DateTimeOffset Due);
internal sealed record AssistantReminderChange(DesktopNotification? Notice = null, string ResolveKey = "", string Result = "");
internal sealed record AssistantReminderDelivery(AssistantReminderChange Change, bool Popup = false);

internal sealed record AssistantRuleOptions
{
    internal bool Enabled { get; init; } = true;
    internal bool Microphone { get; init; } = true;
    internal bool GameWrap { get; init; } = true;
    internal bool QuietDigest { get; init; } = true;
    internal bool AgentFollowup { get; init; } = true;
    internal bool UsageBreak { get; init; } = true;
    internal bool MeetingWrap { get; init; } = true;
    internal bool DownloadStall { get; init; } = true;
    internal bool BatteryContext { get; init; } = true;
    internal bool DailyReview { get; init; } = true;
    internal int CooldownMinutes { get; init; } = 3;
    internal int AgentWaitMinutes { get; init; } = 3;
    internal int UsageBreakMinutes { get; init; } = 75;
    internal int DownloadStallSeconds { get; init; } = 90;
    internal int ReviewHour { get; init; } = 21;
    internal int ReviewMinute { get; init; }
    internal static AssistantRuleOptions From(IslandOptions options)
    {
        bool B(string key) => SettingsRegistry.GetBool(options, key, true);
        int N(string key, int fallback) => SettingsRegistry.GetInt(options, key, fallback);
        return new()
        {
            Enabled = B("AgentProactiveEnabled"), Microphone = B("MeetingGuardEnabled"),
            GameWrap = B("AssistantGameWrapEnabled"), QuietDigest = B("AssistantQuietDigestEnabled"),
            AgentFollowup = B("AssistantAgentFollowupEnabled"), UsageBreak = B("AssistantUsageBreakEnabled"),
            MeetingWrap = B("AssistantMeetingWrapEnabled"), DownloadStall = B("AssistantDownloadStallEnabled"), BatteryContext = B("AssistantBatteryContextEnabled"),
            DailyReview = B("AssistantDailyReviewEnabled"), CooldownMinutes = N("AssistantReminderCooldownMinutes", 3),
            AgentWaitMinutes = N("AssistantAgentWaitMinutes", 3), UsageBreakMinutes = N("AssistantUsageBreakMinutes", 75),
            DownloadStallSeconds = N("AssistantDownloadStallSeconds", 90), ReviewHour = N("AssistantDailyReviewHour", 21),
            ReviewMinute = N("AssistantDailyReviewMinute", 0)
        };
    }
}

internal static class AssistantReminderRules
{
    internal static readonly string[] EngineNames = ["microphone", "meeting", "meeting-wrap", "game", "digest", "agent-wait", "agent-error", "agent-ended", "usage", "download", "battery", "daily", "break"];
    internal static readonly string[] Names = [.. EngineNames, "user-reminder", "habit", "health"];
    internal static readonly string[] Actions = ["sound", "meeting-notes", "stats", "notifications", "agent", "downloads", "power", "volume-restore", "media-resume", "break-five", "daily-ai", "assistant-center", "reminder-snooze", "habit-accept", "habit-dismiss", "health-taskmgr"];
    internal static string? PendingGroup(DesktopNotification notice)
    {
        if (!notice.IsPinned || notice.Reminder is not { Pending: true } reminder) return null;
        return reminder.Rule switch
        {
            "game" or "digest" or "usage" or "daily" or "break" or "microphone" or "meeting" or "meeting-wrap" => reminder.Rule,
            "agent-wait" or "agent-error" => reminder.Actions.FirstOrDefault(a => a.Id == "agent") is { Target.Length: > 0 } action
                ? reminder.Rule + ":" + action.Target : null,
            "habit" => reminder.Actions.FirstOrDefault(a => a.Id == "habit-accept") is { Target.Length: > 0 } action
                ? "habit:" + action.Target : null,
            // Independent user reminders and download tasks must retain their identities.
            _ => null
        };
    }
    internal static bool Valid(AssistantReminder reminder) => Names.Contains(reminder.Rule) && reminder.Actions != null
        && reminder.Actions.Length <= 8 && reminder.Actions.All(a => a != null && Actions.Contains(a.Id)
            && a.Label is { Length: > 0 and <= 40 } && a.Target is { Length: <= 256 })
        && reminder.Volume is null or >= 0 and <= 100 && reminder.MediaSession is { Length: <= 256 } && reminder.OutputDevice is { Length: <= 256 };
}
