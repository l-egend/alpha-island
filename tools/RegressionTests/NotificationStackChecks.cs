using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Controls;
using alpha_island.ViewModels;
using Button = System.Windows.Controls.Button;

internal static partial class Program
{
    private static void NotificationStackChecks(alpha_island.MainWindow window, Grid root)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        var saved = new Dictionary<string, System.Text.Json.JsonElement>(options.Extras);
        var originalOpener = AgentField<Func<DesktopNotification, bool>>(window, "_notificationAppOpener");
        var workspace = AgentField<IslandWorkspaceViewModel>(window, "_workspace");
        var policy = new DesktopNotificationPolicy(options);
        var now = DateTimeOffset.Now;
        DesktopNotification Message(int index) => new("stack-" + index, "QQ.exe", "QQ", "联系人 " + index, "消息正文 " + index, now.AddSeconds(-2).AddMilliseconds(index));
        var burst = new DesktopNotificationBurst(); burst.Begin(Message(1), now); burst.Add(Message(2), now.AddSeconds(1));
        Check(burst.Count == 2 && burst.CanMerge(now.AddSeconds(6)), "短时间消息进入同一堆叠窗口");
        burst.Add(Message(2) with { Body = "更新正文" }, now.AddSeconds(2));
        Check(burst.Count == 2 && burst.Top(policy)?.Body == "更新正文", "同一通知更新不重复计数");
        Check(!burst.CanMerge(now.AddSeconds(8)), "超过六秒的消息间隔开始新批次");
        for (int index = 3; index < 90; index++) burst.Add(Message(index), now.AddSeconds(3));
        Check(burst.Count == 50 && !burst.CanMerge(now.AddSeconds(31)), "堆叠有容量上限和三十秒总展示上限");
        var inbox = new DesktopNotificationInbox(); inbox.Add(Message(1), true); inbox.Add(Message(2), true);
        inbox.Add(Message(3) with { Kind = DesktopEventKind.System }, true);
        Check(inbox.TakePendingMessages(policy, false, now).Length == 2 && inbox.TakeNext(policy, false, now)?.Kind == DesktopEventKind.System,
            "批量取消息不吞掉电源等独立系统事件");
        try
        {
            SettingsRegistry.Write(options, "NotificationQuietMode", false); SettingsRegistry.Write(options, "NotificationPriorityOnly", false);
            SettingsRegistry.Write(options, "NotificationAllowedApps", ""); SettingsRegistry.Write(options, "NotificationBlockedApps", ""); SettingsRegistry.Write(options, "NotificationKeywords", "");
            PrivateCall(window, "ClearDesktopInbox"); AgentField<IslandLayerHost>(window, "_layers").HideAll();
            PrivateCall(window, "SetState", IslandState.Bar, true);
            PrivateCall(window, "OnSystemToastsReceived", new[] { Message(0) }, true);
            PrivateCall(window, "OnSystemToastsReceived", new[] { Message(1) }, false);
            var pill = AgentField<DesktopNotificationPill>(window, "_notificationSummary");
            Check(workspace.Notifications.Burst.Count == 1 && pill.StackCountButton.Visibility == Visibility.Collapsed, "单条通知保持原有正文与点击布局");
            PrivateCall(window, "OnSystemToastsReceived", new[] { Message(2), Message(3) }, false);
            PumpAnimations(180); root.UpdateLayout();
            Check(workspace.Notifications.Burst.Count == 3 && workspace.Notifications.Inbox.PendingCount == 0
                && workspace.Notifications.Current?.Key == Message(3).Key, "后续消息原位合并，最新内容置顶且没有逐条补弹队列");
            Check(pill.StackCountButton.IsVisible && Equals(pill.StackCountButton.Content, "共 3 条"), "真实小岛显示可点击的通知总数");
            SaveThemePreview(window.IslandBody, "docs/screens/v106-notification-stack-pill.png");
            var layers = AgentField<IslandLayerHost>(window, "_layers");
            PrivateCall(window, "ShowPillNotification", IslandNotification.Seconds("volume", 5));
            PumpAnimations(100); root.UpdateLayout();
            var orbs = AgentField<Dictionary<string, Border>>(window, "_satelliteOrbViews");
            Check(orbs.TryGetValue("desktoptoast", out var orb) && Descendants<Button>(orb).Any(button => button.Name == "NotificationStackCount" && button.Visibility == Visibility.Visible),
                "通知缩到旁侧小图标时仍显示数量展开入口");
            layers.Hide("volume"); PrivateCall(window, "SwitchMainIsland", "desktoptoast"); PrivateCall(window, "SyncNotificationLayers");
            int opened = 0; SetAgentField(window, "_notificationAppOpener", (Func<DesktopNotification, bool>)(_ => { opened++; return true; }));
            pill.StackCountButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpAnimations(120); root.UpdateLayout();
            var cards = Descendants<DesktopNotificationCard>(AgentField<StackPanel>(window, "_notificationList")).ToArray();
            Check(opened == 0 && AgentField<IslandPageHost>(window, "_pages").ActiveId == IslandPageIds.Notifications
                && cards.Length == 3 && cards.All(card => card.Notification.Key != Message(0).Key), "数量按钮只展开这批三条通知，不跳软件或混入旧历史");
            Check(workspace.Notifications.Current == null && workspace.Notifications.Inbox.PendingCount == 0, "展开后不在背后继续轮播这批通知");
            SaveThemePreview(window.IslandBody, "docs/screens/v106-notification-stack-expanded.png");
            AgentField<System.Windows.Controls.Button>(window, "_notificationStackBack").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(AgentField<StackPanel>(window, "_notificationList").Children.OfType<DesktopNotificationStackCard>().Single() is { IsExpanded: false } all
                && all.Group.Items.Length == 4, "全部通知回到包含四条历史消息的收起卡片堆");
            PrivateCall(window, "ClearDesktopInbox"); PrivateCall(window, "SetState", IslandState.Bar, true);
            PrivateCall(window, "OnSystemToastsReceived", new[] { Message(4), Message(5) }, false);
            Check((bool)PrivateCall(window, "TryOpenMessengerNotification", "desktoptoast")!, "多条消息时正文仍使用 QQ 直接跳转路径");
            PumpUntilState(() => opened == 1 && workspace.Notifications.Current == null);
            Check(opened == 1 && workspace.Notifications.Inbox.PendingCount == 0, "正文一次点击只打开一次应用，旧堆叠不会再次弹出");
            PrivateCall(window, "OnSystemToastsReceived", new[] { Message(6), Message(7) }, false);
            workspace.Notifications.Expires = DateTimeOffset.Now.AddSeconds(-1); PrivateCall(window, "TickDesktopNotifications");
            Check(workspace.Notifications.Current == null && workspace.Notifications.Burst.Count == 0 && workspace.Notifications.Inbox.PendingCount == 0,
                "超时整体收回，历史保留且不逐条重放");
            SettingsRegistry.Write(options, "NotificationQuietMode", true); SettingsRegistry.Write(options, "NotificationQuietAllowPriority", false);
            PrivateCall(window, "OnSystemToastsReceived", new[] { Message(8), Message(9) }, false);
            Check(workspace.Notifications.Current == null && workspace.Notifications.Burst.Count == 0 && workspace.Notifications.Inbox.History.Any(item => item.Key == Message(9).Key),
                "免打扰期间仅保存历史，不弹堆叠");
        }
        finally
        {
            SetAgentField(window, "_notificationAppOpener", originalOpener); options.Extras = saved;
            workspace.Notifications.ExpandedKeys = null; PrivateCall(window, "ClearDesktopInbox");
        }
    }
}
