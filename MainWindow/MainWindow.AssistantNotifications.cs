using System.Diagnostics;
using System.Text.Json;
using alpha_island.Core;
using alpha_island.Services;

namespace alpha_island;

public partial class MainWindow
{
    private readonly HashSet<string> _assistantActionsInFlight = new(StringComparer.Ordinal);
    private bool _assistantNotificationBatch;
    private void ApplyAssistantNotificationChanges(AssistantReminderDelivery[] deliveries)
    {
        bool changed = false, popup = false;
        _assistantNotificationBatch = true;
        try
        {
            foreach (var delivery in deliveries)
            {
                var change = delivery.Change;
                if (change.Notice is { } notice) { PostAssistantNotification(notice, delivery.Popup); changed = true; popup |= delivery.Popup; }
                else if (_desktopInbox.History.Any(n => n.Key == change.ResolveKey && n.Reminder is { Pending: true }))
                { ResolveAssistantNotification(change.ResolveKey, change.Result); changed = true; }
            }
        }
        finally { _assistantNotificationBatch = false; }
        if (popup) { StartNotificationUiTimer(); TickDesktopNotifications(); }
        if (changed) RefreshDesktopInbox();
    }
    private AssistantFrame ReadAssistantFrame()
    {
        var now = DateTimeOffset.Now;
        var media = _workspace.Media.Current;
        var day = _productivity?.Enabled == true ? _productivity.Days.FirstOrDefault(d => d.Date == now.ToString("yyyy-MM-dd")) : null;
        string summary = day == null ? "" : $"今天已记录：专注 {AssistantReminderEngine.Duration(day.FocusSeconds)}，游戏 {AssistantReminderEngine.Duration(day.GameSeconds)}，音乐 {AssistantReminderEngine.Duration(day.MusicSeconds)}，助手运行 {AssistantReminderEngine.Duration(day.AgentSeconds)}。各类别可重叠。";
        var agents = AgentStatus.Snapshot.Select(a => new AssistantAgent(a.Owner, a.ActivityId, a.State,
            _agentSessions.Values.FirstOrDefault(s => s.Owner == a.Owner) is { } view ? AgentLabel(view.Key) : AgentSourceName(AgentSource(a.Owner)))).ToArray();
        var downloads = _workspace.Downloads.Current.Select(d => new AssistantDownload(
            AssistantReminderEngine.Digest(d.Key + ":" + d.StartLocal.Ticks), d.Source + " · " + d.Name, d.ReceivedBytes,
            d.Status == DownloadState.Downloading && d.HasReliableProgress && d.SourceRunning
                && DateTime.UtcNow - _workspace.Downloads.SampledUtc < TimeSpan.FromSeconds(10), d.IsCompleted)).ToArray();
        return new(now, Environment.TickCount64, _sessionLocked || _exiting, NotificationQuietEffective, _pomodoro.Running && !_pomodoro.IsBreak,
            _gameActivity?.Current is { Active: true } game ? game.Name : null,
            _runtimeEnabled ? Interop.UserIdleTime.ReadSeconds() : null,
            _desktopAssistant?.PrivacyFresh == true, _desktopAssistant?.InputMuted,
            _desktopAssistant?.PrivacySnapshot.Select(p => p.AppName).Distinct().ToArray() ?? [],
            media?.SessionId ?? "", media?.IsPlaying == true && media.ContentKind == MediaContentKind.Music,
            _volumeService?.IsAvailable == true ? _volumeService.CurrentPercent : null,
            _volumeService?.IsAvailable == true ? _volumeService.IsMuted : null,
            agents, downloads, _desktopInbox.History.Where(n => _desktopPolicy.Accepts(n)).ToArray(), summary, day?.GameSeconds, _volumeService?.CurrentDeviceIdentifier ?? "");
    }
    private void PostAssistantNotification(DesktopNotification item, bool popup)
    {
        if (_exiting || item.Reminder == null || !AssistantReminderRules.Valid(item.Reminder)) return;
        _desktopInbox.ExpireReminders(DateTimeOffset.Now);
        _desktopInbox.Add(item, popup && _desktopPolicy.CanPopup(item, _sessionLocked), _desktopPolicy);
        if (_desktopCurrent?.Reminder is { Pending: true } &&
            !_desktopInbox.History.Any(n => n.Key == _desktopCurrent.Key && n.Reminder is { Pending: true })) FinishDesktopPopup();
        if (!_assistantNotificationBatch)
        {
            if (popup) { StartNotificationUiTimer(); TickDesktopNotifications(); }
            RefreshDesktopInbox();
        }
    }
    private void RespondToAssistantNotification(DesktopNotification item, string response)
    {
        var current = _desktopInbox.History.FirstOrDefault(n => n.Key == item.Key);
        if (current?.Reminder is not { } reminder || _sessionLocked) return;
        if (reminder.Rule is "user-reminder" or "habit" or "health")
        {
            if (reminder.Rule == "habit")
            {
                string id = reminder.Actions.FirstOrDefault(action => action.Id == "habit-dismiss")?.Target ?? "";
                _insights?.Respond(id, false);
            }
            ResolveAssistantNotification(item.Key, "已处理"); RefreshDesktopInbox(); return;
        }
        _assistantReminders?.Respond(reminder.Rule, response);
        if (response == "later") _assistantReminders?.Defer(current);
        ResolveAssistantNotification(item.Key, response == "today" ? "今天不再提醒此类事项" : response == "later" ? "十分钟内暂缓此类提醒" : "已处理");
        if (response == "today")
            foreach (var other in _desktopInbox.History.Where(n => n.Key != item.Key && n.Reminder is { Pending: true } r && r.Rule == reminder.Rule).ToArray())
                ResolveAssistantNotification(other.Key, "今天不再提醒此类事项");
        RefreshDesktopInbox();
    }
    private async Task ExecuteAssistantNotificationActionAsync(DesktopNotification item, AssistantNoticeAction action)
    {
        var current = _desktopInbox.History.FirstOrDefault(n => n.Key == item.Key);
        if (_exiting || _sessionLocked || current?.Reminder is not { } reminder || reminder.Expires <= DateTimeOffset.Now
            || !reminder.Actions.Contains(action) || !AssistantReminderRules.Valid(reminder) || !_assistantActionsInFlight.Add(item.Key)) return;
        string? error = null;
        try
        {
            _desktopInbox.MarkRead(item.Key);
            switch (action.Id)
            {
                case "stats": SelectComponent("productivity"); break;
                case "notifications":
                    _workspace.Notifications.ExpandedKeys = null; _notificationSearch.Text = ""; _notificationLevels.SelectedIndex = 0;
                    _workspace.Notifications.UnreadOnly = true;
                    SelectComponent(IslandPageIds.Notifications); break;
                case "downloads": SelectComponent("downloads"); break;
                case "sound": Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); break;
                case "power": Process.Start(new ProcessStartInfo("ms-settings:powersleep") { UseShellExecute = true }); break;
                case "agent":
                    string? page = _plugins?.PageForActivity(action.Target);
                    if (page != null && _pages.IndexOf(page) >= 0) SelectComponent(page);
                    else if (!_agentNavigation.TryOpen(action.Target, out var navigationError)) error = navigationError;
                    break;
                case "meeting-notes":
                case "daily-ai":
                    const string chatPage = "plugin:ai.api-chat/chat";
                    string draft = action.Id == "daily-ai" ? "请根据以下本机活动时长做简短回顾，只使用已有数据，不推测工作内容或游戏战绩：\n" + item.Body
                        : "请将以下会议笔记整理为待办，列出事项、负责人和截止时间；没有的信息标为待确认：\n";
                    if (_plugins == null || _pages.IndexOf(chatPage) < 0 || !await _plugins.PrefillAsync(chatPage, draft))
                        error = "请先在插件设置中配置并启用 API 对话。此提醒仍保留在通知中心。";
                    else SelectComponent(chatPage);
                    break;
                case "break-five":
                    _assistantReminders?.Respond(reminder.Rule, "break-five");
                    PostAssistantNotification(new("assistant:break-started", "", "Alpha Island · 助手", "五分钟休息已开始", "结束时会在通知中心提醒；锁屏或免打扰期间保留记录。", DateTimeOffset.Now,
                        DesktopEventKind.Assistant) { Reminder = new("break", false, DateTimeOffset.Now.AddMinutes(10), []) }, false);
                    break;
                case "volume-restore":
                    if (_islandToolHost == null || reminder.Volume == null) { error = "没有可恢复的会前音量记录。"; break; }
                    if (reminder.OutputDevice.Length > 0 && _volumeService?.CurrentDeviceIdentifier != reminder.OutputDevice)
                    { error = "会前音频输出设备已变化，未修改其他设备的音量。"; break; }
                    var volume = await _islandToolHost.ExecuteAsync("set_volume", JsonSerializer.SerializeToElement(new { percent = reminder.Volume.Value }), _utilitiesLifetime.Token);
                    if (!volume.Success) { error = volume.Message; break; }
                    if (reminder.Muted is { } muted)
                    {
                        var mute = await _islandToolHost.ExecuteAsync("set_mute", JsonSerializer.SerializeToElement(new { muted }), _utilitiesLifetime.Token);
                        if (!mute.Success) error = mute.Message;
                    }
                    break;
                case "media-resume":
                    if (_smtc?.Current?.SessionId != reminder.MediaSession || reminder.MediaSession.Length == 0) { error = "会前播放器已变化，未自动切换其他播放器。"; break; }
                    if (!await _smtc.TryControlAsync("play")) error = "播放器暂未接受播放指令。";
                    break;
                case "assistant-center": OpenAssistantCenter(action.Target); break;
                case "health-taskmgr":
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Taskmgr.exe")) { UseShellExecute = true });
                    break;
                case "reminder-snooze":
                    var snoozed = _userReminders?.Snooze(action.Target, 10);
                    if (snoozed?.Success == true) ResolveAssistantNotification(item.Key, "已延后十分钟"); else error = snoozed?.Message ?? "本地提醒暂不可用。";
                    break;
                case "habit-accept":
                    var adopted = ApplyHabitSuggestion(action.Target); if (!adopted.Success) error = adopted.Message;
                    break;
                case "habit-dismiss":
                    _insights?.Respond(action.Target, false); ResolveAssistantNotification(item.Key, "暂不采纳，14天内不再提出此建议");
                    break;
                default: return;
            }
            if (error == null && reminder.Pending)
            {
                var remainingRestore = reminder.Actions.Where(a => a.Id != action.Id && a.Id is "volume-restore" or "media-resume").ToArray();
                if (action.Id is "volume-restore" or "media-resume" && remainingRestore.Length > 0
                    && _desktopInbox.History.FirstOrDefault(n => n.Key == item.Key) is { Reminder.Pending: true } latest)
                    _desktopInbox.Add(latest with { Reminder = latest.Reminder! with { Actions = latest.Reminder.Actions.Where(a => a.Id != action.Id).ToArray() },
                        ActionFeedback = action.Label + "已完成，可继续处理其余恢复操作" }, false);
                else
                {
                    ResolveAssistantNotification(item.Key, "已处理 · " + action.Label);
                    _assistantReminders?.Respond(reminder.Rule, "done");
                }
            }
            else if (error != null) SetAssistantNotificationError(item.Key, error);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { SetAssistantNotificationError(item.Key, "暂时无法打开或执行，请稍后重试。"); }
        finally { _assistantActionsInFlight.Remove(item.Key); RefreshDesktopInbox(); }
    }
    private void SetAssistantNotificationError(string key, string message)
    {
        if (_desktopInbox.History.FirstOrDefault(n => n.Key == key) is not { } current) return;
        _desktopInbox.Add(current with { ActionFeedback = message }, false);
    }
    private void ResolveAssistantNotification(string key, string result)
    {
        _desktopInbox.ResolveReminder(key, result);
        if (_desktopCurrent?.Key == key) FinishDesktopPopup();
    }
    private string BatteryContextDescription()
    {
        if (!SettingsRegistry.GetBool(_options, "AgentProactiveEnabled", true) || !SettingsRegistry.GetBool(_options, "AssistantBatteryContextEnabled", true)) return "";
        if (_assistantReminders?.ShouldOffer("battery") == false) return "";
        if (_gameActivity?.Current.Active == true) return "游戏进行中，请留意连接电源";
        if (_assistantReminders?.MeetingActive == true && _desktopAssistant?.PrivacyFresh == true) return "会议设备使用中，请留意连接电源";
        return "";
    }
}
