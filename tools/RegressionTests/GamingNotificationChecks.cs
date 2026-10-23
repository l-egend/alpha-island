using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;
using alpha_island.ViewModels;

internal static partial class Program
{
    private static void GamingNotificationChecks(alpha_island.MainWindow window, Grid root)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        var workspace = AgentField<IslandWorkspaceViewModel>(window, "_workspace");
        var saved = new Dictionary<string, System.Text.Json.JsonElement>(options.Extras);
        bool volumeEnabled = options.VolumeNotifyEnabled, reduce = MotionPolicy.ReduceMotion, power = MotionPolicy.PowerSave;
        var originalVolume = AgentField<VolumeService?>(window, "_volumeService");
        using var volume = new VolumeService(window.Dispatcher); // Never started: no real audio is changed.
        var failures = new List<Exception>();
        void Case(Action action) { try { action(); } catch (Exception error) { failures.Add(error); Console.WriteLine("FAIL: " + error.Message); } }
        var now = DateTimeOffset.Now;
        DesktopNotification Notice(string id) => new("assistant:game:" + id, "", "Alpha Island · 助手", "游戏收尾 · 鸣潮",
            "本次是游戏 1 小时 2 分钟，今日累计 1 小时 2 分钟。\n当前保留未读 4 条，期间 0 个助手任务结束，0 个下载完成。", now,
            DesktopEventKind.Assistant) { IsPinned = true, Reminder = new("game", true, now.AddHours(1), [new("stats", "查看时长"), new("notifications", "查看未读"), new("break-five", "休息5分钟")]) };
        try
        {
            MotionPolicy.Apply(false, false);
            SettingsRegistry.Write(options, "NotificationGameQuietEnabled", true);
            SettingsRegistry.Write(options, "NotificationQuietMode", false);
            SetAgentField(window, "_volumeService", volume); options.VolumeNotifyEnabled = true;
            PrivateCall(window, "ApplyGameActivity", new GameActivityState(1, true, "后台游戏", "fixture", 1));
            Case(() => Check(!(bool)ReadWindowMember(window, "VolumeFlyoutTakeoverAllowed")!, "游戏进入后台时仍放行 Windows 原生音量浮层"));
            Case(() =>
            {
                PrivateCall(window, "SyncVolumeFlyoutTakeover");
                var key = new KeyboardHookEventArgs(Key.VolumeUp, 0xAF, ModifierKeys.None, true);
                PrivateCall(window, "OnVolumeKeyPreview", null, key);
                Check(!key.Handled && !AgentField<bool>(window, "_volumeKeysTakeoverEnabled"), "游戏模式的音量键不被岛吞掉，系统仍能显示音量大小");
            });
            PrivateCall(window, "ApplyGameActivity", GameActivityState.Inactive);
            var pill = new DesktopNotificationPill(); pill.SetNotification(Notice("preview"));
            pill.Measure(new System.Windows.Size(328, 32)); pill.Arrange(new Rect(0, 0, 328, 32)); pill.UpdateLayout();
            Case(() => Check(!pill.Detail.Text.Contains('\n') && !pill.Detail.Text.Contains('\r') && pill.Detail.DesiredSize.Height <= 16,
                "多行游戏总结在紧凑通知中收为一行，不裁掉半行文字"));
            SaveReminderPreview(pill, "bin/GamingFix128/game-summary-pill.png", 328, 32);
            var mergedInbox = new DesktopNotificationInbox();
            mergedInbox.Add(Notice("queue-old"), true);
            mergedInbox.Add(Notice("queue-new") with { Created = now.AddSeconds(1) }, true);
            Case(() => Check(mergedInbox.PendingCount == 1 && mergedInbox.History.Single(n => n.Key.EndsWith("queue-old")) is { IsPinned: false, Reminder.Pending: false },
                "同类合并同时撤下旧弹窗队列并保留已处理历史"));
            mergedInbox.Add(Notice("late-old") with { Created = now.AddSeconds(-1) }, true);
            Case(() => Check(mergedInbox.History.Single(n => n.IsPinned).Key.EndsWith("queue-new"), "延迟到达的旧提醒不会覆盖最新待处理事项"));
            foreach (string id in new[] { "one", "two" })
                mergedInbox.Add(Notice(id) with { Key = "user:" + id, Reminder = new("user-reminder", true, now.AddHours(1), [new("reminder-snooze", "稍后提醒", id)]) }, false);
            Case(() => Check(mergedInbox.History.Count(n => n.Reminder is { Rule: "user-reminder", Pending: true }) == 2,
                "不同的用户定时提醒保留独立身份，不被同类合并误删"));
            workspace.Notifications.Inbox.Clear();
            PrivateCall(window, "PostAssistantNotification", Notice("old"), false);
            PrivateCall(window, "PostAssistantNotification", Notice("new") with { Created = now.AddSeconds(1) }, false);
            Case(() => Check(workspace.Notifications.Inbox.History.Count(n => n.IsPinned && n.Reminder is { Rule: "game", Pending: true }) == 1,
                "同类游戏收尾只保留最新一项待处理置顶，旧项合并处理"));
            PrivateCall(window, "ClearDesktopInbox");
            Case(() => Check(workspace.Notifications.Inbox.History.Count == 0, "清空记录也能一键移除置顶待处理通知"));
            workspace.Notifications.Inbox.Clear();
            var deleted = Notice("delete"); PrivateCall(window, "PostAssistantNotification", deleted, false);
            PrivateCall(window, "SelectComponent", IslandPageIds.Notifications);
            PrivateCall(window, "RefreshDesktopInbox"); PumpAnimations(300); root.UpdateLayout();
            var panel = AgentField<StackPanel>(window, "_notificationList");
            var card = Descendants<DesktopNotificationCard>(panel).Single(c => c.Notification.Key == deleted.Key);
            var originalTransform = card.RenderTransform;
            PrivateCall(window, "DismissDesktopNotification", deleted);
            Case(() => Check(Descendants<DesktopNotificationCard>(panel).Contains(card) && !workspace.Notifications.Inbox.History.Any(n => n.Key == deleted.Key),
                "删除先更新数据并保留原卡片播放退出动画，避免瞬间消失"));
            PumpAnimations(450);
            Case(() => Check(!Descendants<DesktopNotificationCard>(panel).Any(c => c.Notification.Key == deleted.Key), "删除动画结束后清理卡片与动画资源"));
            Case(() => Check(!card.HasAnimatedProperties && ReferenceEquals(card.RenderTransform, originalTransform), "删除退出后清除动画时钟并恢复原始变换"));
            MotionPolicy.Apply(true, true);
            var reduced = Notice("reduced"); PrivateCall(window, "PostAssistantNotification", reduced, false);
            PrivateCall(window, "RefreshDesktopInbox"); root.UpdateLayout(); PrivateCall(window, "DismissDesktopNotification", reduced);
            Case(() => Check(!Descendants<DesktopNotificationCard>(panel).Any(c => c.Notification.Key == reduced.Key), "削减动效模式即时删除，不残留等待动画的卡片"));
        }
        finally
        {
            workspace.Notifications.Inbox.Clear(); options.Extras = saved; options.VolumeNotifyEnabled = volumeEnabled;
            SetAgentField(window, "_volumeService", originalVolume);
            PrivateCall(window, "ApplyGameActivity", GameActivityState.Inactive);
            PrivateCall(window, "SyncVolumeFlyoutTakeover");
            MotionPolicy.Apply(reduce, power);
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }
}
