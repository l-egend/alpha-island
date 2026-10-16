using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using alpha_island.Core;
using alpha_island.Controls;

internal static partial class Program
{
    private static void MessengerDirectOpenChecks(alpha_island.MainWindow window, Grid root)
    {
        var original = AgentField<Func<DesktopNotification, bool>>(window, "_notificationAppOpener");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        var inbox = AgentField<DesktopNotificationInbox>(window, "_desktopInbox");
        var dedup = AgentField<MessengerNotificationDeduplicator>(window, "_messengerNoticeDedup");
        var pages = AgentField<IslandPageHost>(window, "_pages");
        var opened = new ConcurrentQueue<DesktopNotification>();
        bool success = false;
        using var release = new ManualResetEventSlim(true);
        int uiThread = Environment.CurrentManagedThreadId;
        bool worker = false;
        void Wait()
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (AgentField<bool>(window, "_openingDesktopApp") && DateTime.UtcNow < deadline) PumpAnimations(20);
            Check(!AgentField<bool>(window, "_openingDesktopApp"), "消息快捷打开会在有限时间完成");
        }
        void Reset()
        {
            PrivateCall(window, "ClearDesktopInbox"); dedup.Clear(); layers.HideAll();
            SetAgentField(window, "_sessionLocked", false);
            PrivateCall(window, "SetState", IslandState.Bar, true);
            while (opened.TryDequeue(out _)) { }
        }
        void Notice(DesktopNotification item)
        {
            PrivateCall(window, "OnMessengerNotificationsReceived", new[] { item }, MessengerNoticeSource.Popup);
            PumpAnimations(50); root.UpdateLayout();
        }
        MouseButtonEventArgs ClickArgs() => new(System.Windows.Input.Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
        void MainClick()
        {
            var args = ClickArgs();
            PrivateCall(window, "IslandBody_MouseLeftButtonDown", window.IslandBody, args);
            Check(args.Handled && AgentField<IslandState>(window, "_state") == IslandState.Pill,
                "微信和 QQ 通知点击直接打开应用，不先展开通知卡片");
        }
        try
        {
            SetAgentField(window, "_notificationAppOpener", new Func<DesktopNotification, bool>(item =>
            {
                worker = Environment.CurrentManagedThreadId != uiThread;
                opened.Enqueue(item);
                release.Wait(3000);
                return success;
            }));
            foreach (var client in new[] { MessengerClient.WeChat, MessengerClient.QQ })
            {
                Reset(); var item = MessengerTestNotice(client, "direct-main-" + client, "联系人", "消息正文");
                Notice(item); MainClick(); Wait();
                Check(worker && opened.Count == 1 && opened.Single().Key == item.Key,
                    "主通知岛在工作线程调用对应客户端入口一次");
                Check(AgentField<DesktopNotification?>(window, "_desktopCurrent")?.Key == item.Key,
                    "打开失败时保留原消息，可重新点击");
                Check(AgentField<DesktopNotificationPill>(window, "_notificationSummary").ToolTip?.ToString()?.Contains("点击打开" + MessengerIdentity.Name(client)) == true,
                    "通知悬停说明提示直接打开对应客户端");

                PrivateCall(window, "SetState", IslandState.Card, true, "focus"); PumpAnimations(50); root.UpdateLayout();
                Check(AgentField<string?>(window, "_sideBubbleComponentKey") == "desktoptoast", "展开其他卡片时消息通过旁侧提示显示");
                var sideClick = ClickArgs(); PrivateCall(window, "SideBubble_MouseLeftButtonUp", window.SideBubble, sideClick); Wait();
                Check(sideClick.Handled && pages.ActiveId == "focus" && opened.Count == 2 && opened.Last().Key == item.Key,
                    "旁侧消息直接打开客户端，保留正在查看的卡片");

                layers.ShowSeconds("system", 20); PrivateCall(window, "SwitchMainIsland", "system");
                PrivateCall(window, "SetState", IslandState.Pill, true); PumpAnimations(50); root.UpdateLayout();
                var orbs = AgentField<Dictionary<string, Border>>(window, "_satelliteOrbViews");
                Check(orbs.TryGetValue("desktoptoast", out var orb) && orb.Visibility == Visibility.Visible,
                    "另一提示作为主岛时消息作为缩略小岛显示");
                orb!.RaiseEvent(new MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
                Wait();
                Check(opened.Count == 3 && opened.Last().Key == item.Key && AgentField<string>(window, "_activeMainIslandKey") == "system",
                    "缩略通知点击直接打开客户端，不先切换主岛");

                Reset(); Notice(item with { Key = "direct-success-" + client }); success = true;
                MainClick(); Wait();
                Check(AgentField<DesktopNotification?>(window, "_desktopCurrent") == null && inbox.History.Count == 1,
                    "成功打开后结束当前提示，历史消息仍保留");
                success = false;

                Reset(); Notice(item with { Key = "direct-locked-" + client }); SetAgentField(window, "_sessionLocked", true);
                MainClick(); PumpAnimations(30);
                Check(opened.IsEmpty, "锁屏时快捷打开不会调用外部客户端");
            }
            Reset(); var pending = MessengerTestNotice(MessengerClient.WeChat, "direct-pending"); Notice(pending);
            release.Reset(); MainClick(); MainClick();
            var until = DateTime.UtcNow.AddSeconds(1);
            while (opened.IsEmpty && DateTime.UtcNow < until) PumpAnimations(20);
            Check(opened.Count == 1 && AgentField<bool>(window, "_openingDesktopApp"),
                "快捷通知连点只提交一次恢复，不阻塞 UI");
            release.Set(); Wait();

            Reset(); var ordinary = new DesktopNotification("ordinary", "fixture.app", "普通软件", "标题", "正文", DateTimeOffset.Now);
            inbox.Add(ordinary, true); PrivateCall(window, "TickDesktopNotifications");
            PrivateCall(window, "IslandBody_MouseLeftButtonDown", window.IslandBody, ClickArgs());
            Check(opened.IsEmpty && pages.ActiveId == IslandPageIds.Notifications && AgentField<IslandState>(window, "_state") == IslandState.Card,
                "其他软件通知仍打开通知详情，不误用 QQ 或微信入口");
        }
        finally
        {
            release.Set(); Wait();
            SetAgentField(window, "_notificationAppOpener", original);
            SetAgentField(window, "_sessionLocked", false);
            PrivateCall(window, "ClearDesktopInbox"); layers.HideAll();
        }
    }
}