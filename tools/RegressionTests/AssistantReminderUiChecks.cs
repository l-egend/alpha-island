using System.IO;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;
using alpha_island.ViewModels;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;

internal static partial class Program
{
    private static void AssistantReminderUiChecks(alpha_island.MainWindow window, string root)
    {
        var workspace = AgentField<IslandWorkspaceViewModel>(window, "_workspace");
        var pages = AgentField<IslandPageHost>(window, "_pages");
        var options = AgentField<IslandOptions>(window, "_options");
        var saved = new Dictionary<string, System.Text.Json.JsonElement>(options.Extras);
        workspace.Notifications.Inbox.Clear();
        var now = DateTimeOffset.Now;
        var game = new DesktopNotification("assistant:game:ui", "", "Alpha Island · 助手", "游戏收尾 · 示例游戏", "本次 52 分钟，今天累计 1 小时 40 分钟。当前保留未读 8 条，2 个助手任务结束。", now.AddSeconds(-10), DesktopEventKind.Assistant)
        { IsPinned = true, Reminder = new("game", true, now.AddHours(1), [new("stats", "查看时长"), new("notifications", "查看未读"), new("break-five", "休息5分钟")]) };
        var meeting = game with { Key = "assistant:meeting:ui", Title = "会议待办", Body = "提供会议笔记后整理待办。", Created = now.AddSeconds(-8),
            Reminder = new("meeting", true, now.AddHours(1), [new("meeting-notes", "整理会议笔记")]) };
        try
        {
            Check(pages.IndexOf("assistant-guard") < 0, "独立助手提醒页已移除，不再保留无响应的打开助手按钮");
            PrivateCall(window, "PostAssistantNotification", game, false);
            PrivateCall(window, "PostAssistantNotification", meeting, false);
            workspace.Notifications.Inbox.Add(new("normal-ui", "wechat", "微信", "新消息", "普通通知继续保持原有堆叠与跳转。", now), false);
            PrivateCall(window, "SelectComponent", IslandPageIds.Notifications); PrivateCall(window, "RefreshDesktopInbox"); PumpAnimations(80);
            var panel = AgentField<StackPanel>(window, "_notificationList");
            var cards = Descendants<DesktopNotificationCard>(panel).ToArray();
            Check(cards.Length == 3 && cards.Take(2).All(c => c.Notification.IsPinned), "需要处理的助手事项在普通消息之前独立置顶显示");
            Check(cards.Any(c => Descendants<Button>(c).Any(b => Equals(b.Content, "休息5分钟"))) && !Descendants<Button>(panel).Any(b => Equals(b.Content, "打开助手")), "通知中直接提供当前事项动作，不使用通用空按钮");
            var unreadTask = (Task)PrivateCall(window, "ExecuteAssistantNotificationActionAsync", game, game.Reminder!.Actions.Single(a => a.Id == "notifications"))!;
            AwaitPlugin(unreadTask.ContinueWith(_ => true));
            Check(workspace.Notifications.UnreadOnly && Descendants<DesktopNotificationCard>(panel).Single().Notification.Key == "normal-ui", "查看未读动作真正筛选未读消息而非只打开全部通知");
            Check(workspace.Notifications.Inbox.History.Single(n => n.Key == game.Key) is { IsPinned: false, Reminder.Pending: false }, "成功处理游戏收尾动作后自动解除待处理置顶");
            AgentField<Button>(window, "_notificationUnreadOnly").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!workspace.Notifications.UnreadOnly && Descendants<DesktopNotificationCard>(panel).Count() == 3, "查看全部恢复待处理置顶项和普通通知");
            PrivateCall(window, "ClearDesktopInbox");
            Check(workspace.Notifications.Inbox.History.Count == 0, "清空全部入口同时删除置顶、待处理和普通通知");
            PumpAnimations(320);
            PrivateCall(window, "PostAssistantNotification", game, false);
            PrivateCall(window, "PostAssistantNotification", meeting, false);
            var task = (Task)PrivateCall(window, "ExecuteAssistantNotificationActionAsync", meeting, meeting.Reminder!.Actions[0])!;
            AwaitPlugin(task.ContinueWith(_ => true));
            var failed = workspace.Notifications.Inbox.History.Single(n => n.Key == meeting.Key);
            Check(failed.IsPinned && failed.Reminder?.Pending == true && failed.ActionFeedback.Contains("配置并启用 API"), "API 未配置时在原通知内明确提示，并保留待处理事项");
            PrivateCall(window, "RefreshDesktopInbox");
            var meetingCard = Descendants<DesktopNotificationCard>(panel).Single(c => c.Notification.Key == meeting.Key);
            Descendants<Button>(meetingCard).Single(b => Equals(b.Content, "已处理")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(workspace.Notifications.Inbox.History.Single(n => n.Key == meeting.Key) is { IsPinned: false, IsRead: true, Reminder.Pending: false }, "真实已处理按钮解除置顶并保留已处理历史");
            task = (Task)PrivateCall(window, "ExecuteAssistantNotificationActionAsync", game, game.Reminder!.Actions.Single(a => a.Id == "break-five"))!;
            AwaitPlugin(task.ContinueWith(_ => true));
            var reminderService = AgentField<AssistantReminderService>(window, "_assistantReminders");
            var engine = AgentField<AssistantReminderEngine>(reminderService, "_engine");
            Check(engine.Snapshot().BreakUntil > now.AddMinutes(4) && workspace.Notifications.Inbox.History.Any(n => n.Key == "assistant:break-started"), "休息按钮启动真实本地倒计时并给出已开始反馈");
            PrivateCall(window, "PostAssistantNotification", game with { Key = "assistant:game:later" }, false);
            PrivateCall(window, "RespondToAssistantNotification", game with { Key = "assistant:game:later" }, "later");
            Check(engine.Snapshot().Deferred?.Length == 1, "通知中心稍后提醒进入真实持久化调度队列");
            var originalHost = AgentField<AlphaIsland.AgentTools.IslandTools.IIslandToolHost>(window, "_islandToolHost");
            var manualHost = new ManualReminderHost();
            try
            {
                SetAgentField(window, "_islandToolHost", manualHost); SettingsRegistry.Write(options, "AgentDesktopToolsEnabled", false); PrivateCall(window, "ConfigureDesktopAssistant");
                var volume = game with { Key = "assistant:meeting-wrap:manual", Reminder = new("meeting-wrap", true, now.AddHours(1), [new("volume-restore", "恢复会前音量"), new("media-resume", "恢复播放")], 50, false) };
                PrivateCall(window, "PostAssistantNotification", volume, false);
                AwaitPlugin(((Task)PrivateCall(window, "ExecuteAssistantNotificationActionAsync", volume, volume.Reminder.Actions[0])!).ContinueWith(_ => true));
                Check(manualHost.Calls == 2, "用户手动恢复会前音量不受 AI 工具权限开关误禁用，测试不操作真实音量");
                Check(workspace.Notifications.Inbox.History.Single(n => n.Key == volume.Key) is { IsPinned: true, Reminder.Pending: true } partial
                    && partial.Reminder!.Actions is [{ Id: "media-resume" }], "完成音量恢复后保留尚未处理的恢复播放操作");
            }
            finally { SetAgentField(window, "_islandToolHost", originalHost); }
            var originalGame = AgentField<GameActivityMonitor?>(window, "_gameActivity");
            using (var fakeGame = new GameActivityMonitor(dispatcher: window.Dispatcher))
            {
                try
                {
                    SetAgentField(fakeGame, "_current", new GameActivityState(1, true, "示例游戏", "fixture", 1)); SetAgentField(window, "_gameActivity", fakeGame);
                    int count = workspace.Notifications.Inbox.History.Count;
                    PrivateCall(window, "RecordBatteryDesktopEvent", new BatteryStatusInfo(15, false, BatteryChargeType.Unknown, BatteryPowerEvent.LowBattery, null));
                    var battery = workspace.Notifications.Inbox.History.Single(n => n.Key == "assistant:battery:current");
                    Check(workspace.Notifications.Inbox.History.Count == count + 1 && battery.IsPinned && battery.Body.Contains("游戏进行中"), "情境电量补充原有低电量记录，只有一条待处理通知");
                    PrivateCall(window, "RecordBatteryDesktopEvent", new BatteryStatusInfo(15, true, BatteryChargeType.StandardAC, BatteryPowerEvent.PluggedIn, null));
                    Check(workspace.Notifications.Inbox.History.Single(n => n.Key == battery.Key).Reminder?.Pending == false, "接通电源自动解除旧低电量待处理项");
                }
                finally { SetAgentField(window, "_gameActivity", originalGame); }
            }
            PrivateCall(window, "PostAssistantNotification", game with { Key = "assistant:game:preview" }, false); PrivateCall(window, "RefreshDesktopInbox");
            var center = AgentField<Grid>(window, "_notificationsView"); double savedHeight = center.Height; center.Height = 650;
            SaveReminderPreview(center, "docs/screens/v116-assistant-notification-center.png", 420, 650); center.Height = savedHeight;
            var previewCard = Descendants<DesktopNotificationCard>(panel).First(c => c.Notification.Key == "assistant:game:preview");
            SaveReminderPreview(previewCard, "docs/screens/v116-assistant-actions.png", 372, 300);
            string archive = Path.Combine(root, "reminder-notification-history.dat");
            var model = new NotificationsViewModel(); model.AttachArchive(archive, true, IslandTicker.Shared(window.Dispatcher)); model.Inbox.Add(game, false); model.Dispose();
            var recovered = new NotificationsViewModel(); recovered.AttachArchive(archive, true, IslandTicker.Shared(window.Dispatcher));
            Check(recovered.Inbox.History.Single() is { IsPinned: true, Reminder.Pending: true } && recovered.Inbox.History[0].Reminder!.Actions.Length == 3, "通知中心重启后保留置顶状态和安全动作元数据"); recovered.Dispose();
            using var settings = new ClipboardSettingsLifetime(); settings.Window.LoadFrom(new IslandOptions().Normalized());
            Check(settings.Window.FindName("AssistantDownloadStallEnabledToggle") is CheckBox && settings.Window.FindName("AssistantDailyReviewMinuteBox") is TextBox, "新增提醒开关、阈值与每日时间实际进入设置页面");
            PrivateCall(window, "FinishDesktopPopup"); PrivateCall(window, "SetState", IslandState.Bar, true);
            var transient = game with { Key = "assistant:agent-error:resolve-probe", Reminder = new("agent-error", true, now.AddHours(1), [new("agent", "打开助手", "external:codex/resolve-probe")]) };
            PrivateCall(window, "PostAssistantNotification", transient, true);
            Check(workspace.Notifications.Current?.Key == transient.Key, "错误提醒通过真实通知管线进入小岛展示");
            PrivateCall(window, "ApplyAssistantNotificationChanges", (object)new AssistantReminderDelivery[] { new(new(ResolveKey: transient.Key, Result: "任务状态已恢复")) });
            Check(workspace.Notifications.Current == null && !AgentField<IslandLayerHost>(window, "_layers").IsActive("desktoptoast"), "错误恢复同时撤下通知中心待处理项与小岛弹窗，不残留旧状态");
        }
        finally { options.Extras = saved; PrivateCall(window, "ConfigureDesktopAssistant"); workspace.Notifications.Inbox.Clear(); }
    }
    private static void SaveReminderPreview(FrameworkElement view, string path, int width, int height)
    {
        double originalWidth = view.Width; view.Width = width - view.Margin.Left - view.Margin.Right;
        view.Measure(new System.Windows.Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var background = new System.Windows.Media.DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 24, 30)), null, new Rect(0, 0, width, height));
        bitmap.Render(background); bitmap.Render(view);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        string file = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(file)!); using var stream = File.Create(file); png.Save(stream);
        view.Width = originalWidth;
    }
    private sealed class ManualReminderHost : AlphaIsland.AgentTools.IslandTools.IIslandToolHost
    {
        internal int Calls;
        public ValueTask<AlphaIsland.PluginSdk.ToolResult> ExecuteAsync(string command, System.Text.Json.JsonElement args, CancellationToken token)
        { Calls++; return ValueTask.FromResult(AlphaIsland.PluginSdk.ToolResult.Ok("本地动作已接受")); }
    }
}
