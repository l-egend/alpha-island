using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.ViewModels;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Point = System.Windows.Point;

internal static partial class Program
{
    private static void NotificationVisualStackChecks(alpha_island.MainWindow window, Grid root)
    {
        var workspace = AgentField<IslandWorkspaceViewModel>(window, "_workspace");
        var options = AgentField<IslandOptions>(window, "_options");
        var saved = new Dictionary<string, System.Text.Json.JsonElement>(options.Extras);
        var opener = AgentField<Func<DesktopNotification, bool>>(window, "_notificationAppOpener");
        var now = DateTimeOffset.Now;
        var messages = Enumerable.Range(1, 26).Select(index => new DesktopNotification("visual-qq-" + index, "QQ.exe", "QQ", "联系人 " + index,
            index == 26 ? "今晚的测试已完成，收到请回复。" : "消息内容 " + index, index < 15 ? now.AddDays(-1).AddMinutes(index) : now.AddMinutes(index - 26))).ToArray();
        var wechat = Enumerable.Range(1, 3).Select(index => new DesktopNotification("visual-wx-" + index, "Weixin.exe", "微信", "项目小组",
            "明天的会议时间已确认。", now.AddMinutes(index - 5))).ToArray();
        var groups = NotificationStackGroup.Create(messages.Concat(wechat));
        Check(groups.Length == 2 && groups.Single(group => group.Latest.AppName == "QQ").Items.Length == 26,
            "同一应用跨今天昨天的通知形成一个堆叠，不按日期拆成分类");
        Check(groups[0].Latest.Key == "visual-qq-26", "卡片堆按最新通知时间排序");
        Check(NotificationStackGroup.KeyFor(wechat[0]) == NotificationStackGroup.KeyFor(wechat[0] with { AppId = "WeChat.exe" }),
            "微信不同采集标识仍形成同一叠");
        Check(NotificationStackGroup.Create(messages.Take(2).Select(item => item with { Kind = DesktopEventKind.PowerConnected })).Length == 2,
            "独立电源事件不被归入消息卡片堆");
        try
        {
            SettingsRegistry.Write(options, "NotificationQuietMode", false); SettingsRegistry.Write(options, "NotificationPriorityOnly", false);
            SettingsRegistry.Write(options, "NotificationBlockedApps", ""); SettingsRegistry.Write(options, "NotificationAllowedApps", ""); SettingsRegistry.Write(options, "NotificationKeywords", "");
            PrivateCall(window, "ClearDesktopInbox");
            PrivateCall(window, "OnSystemToastsReceived", messages.Concat(wechat).ToArray(), true);
            PrivateCall(window, "SelectComponent", IslandPageIds.Notifications); PumpAnimations(80); root.UpdateLayout();
            var list = AgentField<StackPanel>(window, "_notificationList");
            var stacks = list.Children.OfType<DesktopNotificationStackCard>().ToArray();
            Check(stacks.Length == 2 && stacks.All(stack => !stack.IsExpanded && !stack.Cards.Any()),
                "通知中心默认显示两叠收起卡片，未展开时不创建二十九张正文控件");
            Check(!list.Children.OfType<TextBlock>().Any(), "消息区域没有日期应用分类标题冒充视觉堆叠");
            var qq = stacks.Single(stack => stack.Group.Latest.AppName == "QQ");
            Check(qq.CountText.Text == "26 个通知" && qq.ToggleButton.IsVisible && qq.TimestampText.Text.StartsWith("今天"),
                "堆叠正面显示应用通知总数、日期与展开入口");
            var face = (Border)qq.ToggleButton.Parent;
            var front = face.TranslatePoint(new Point(), qq); var middle = qq.MiddleCard.TranslatePoint(new Point(), qq); var rear = qq.RearCard.TranslatePoint(new Point(), qq);
            Check(qq.MiddleCard.IsVisible && qq.RearCard.IsVisible && front.Y < middle.Y && middle.Y < rear.Y
                && face.ActualWidth > qq.MiddleCard.ActualWidth && qq.MiddleCard.ActualWidth > qq.RearCard.ActualWidth,
                "真实布局中有两层向下错位且逐层缩窄的圆角卡片");
            Check(!qq.MiddleCard.IsHitTestVisible && !qq.RearCard.IsHitTestVisible, "后层卡片不遮挡正面点击");
            NotificationStackInteractionChecks(qq, wechat[0], root);
            SaveThemePreview(window.IslandBody, "docs/screens/v109-notification-stack-collapsed.png");
            int opened = 0; SetAgentField(window, "_notificationAppOpener", (Func<DesktopNotification, bool>)(_ => { opened++; return true; }));
            qq.ToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); root.UpdateLayout();
            Check(qq.IsExpanded && qq.Cards.Count() == 26 && !qq.MiddleCard.IsVisible && opened == 0,
                "点击堆叠原位展开二十六条消息，先展开而非误打开软件");
            Check(stacks.Single(stack => stack.Group.Latest.AppName == "微信").IsExpanded == false, "展开 QQ 不会同时展开微信");
            SaveThemePreview(window.IslandBody, "docs/screens/v109-notification-stack-expanded.png");
            PrivateCall(window, "OnSystemToastsReceived", new[] { messages[25] with { Body = "更新后的消息正文" } }, false); root.UpdateLayout();
            qq = list.Children.OfType<DesktopNotificationStackCard>().Single(stack => stack.Group.Latest.AppName == "QQ");
            Check(qq.IsExpanded && qq.Cards.Count() == 26 && qq.Cards.First().BodyText.Text == "更新后的消息正文",
                "新内容刷新保留展开状态，重复消息更新不重复计数");
            var card = qq.Cards.First();
            card.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            PumpUntilState(() => opened == 1);
            Check(opened == 1, "展开后的 QQ 消息正文一键直达应用");
            card.DismissButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); root.UpdateLayout();
            qq = list.Children.OfType<DesktopNotificationStackCard>().Single(stack => stack.Group.Latest.AppName == "QQ");
            Check(qq.IsExpanded && qq.Group.Items.Length == 25 && qq.Cards.Count() == 25, "删除其中一条同步更新堆叠数量并保留展开");
            qq.ToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); root.UpdateLayout();
            Check(!qq.IsExpanded && qq.MiddleCard.IsVisible && qq.RearCard.IsVisible, "再次点击收回真实卡片堆");
            var search = AgentField<TextBox>(window, "_notificationSearch"); search.Text = "会议"; root.UpdateLayout();
            Check(list.Children.OfType<DesktopNotificationStackCard>().Single().Group.Latest.AppName == "微信", "搜索继续筛选对应堆叠中的实际消息");
            search.Text = ""; root.UpdateLayout();
            qq = list.Children.OfType<DesktopNotificationStackCard>().Single(stack => stack.Group.Latest.AppName == "QQ");
            Check(qq.MiddleCard.Clip is RectangleGeometry middleClip && middleClip.Bounds.Height <= 7
                && qq.RearCard.Clip is RectangleGeometry rearClip && rearClip.Bounds.Height <= 14,
                "透明前卡内不会透出后卡轮廓，后层只露出底部边缘");
            var theme = new SolidColorBrush(System.Windows.Media.Color.FromRgb(31, 47, 63));
            list.Resources["TextPrimary"] = theme; root.UpdateLayout();
            Check(ReferenceEquals(qq.CountText.Foreground, theme), "数量和文字随主题资源即时更新");
            list.Resources.Remove("TextPrimary");
            bool reduced = alpha_island.Animation.MotionPolicy.ReduceMotion, power = alpha_island.Animation.MotionPolicy.PowerSave;
            try
            {
                alpha_island.Animation.MotionPolicy.Apply(false, false);
                qq.SetExpanded(true); PumpAnimations(50); qq.SetExpanded(false); PumpAnimations(50); qq.SetExpanded(true); PumpAnimations(260);
                var panel = AgentField<Border>(qq, "_expansion");
                Check(qq.IsExpanded && qq.Cards.Count() == 25 && double.IsNaN(panel.Height) && !panel.HasAnimatedProperties,
                    "快速展开收起再展开后正确停在最终状态并清理动画时钟");
            }
            finally { alpha_island.Animation.MotionPolicy.Apply(reduced, power); }
        }
        finally
        {
            SetAgentField(window, "_notificationAppOpener", opener); options.Extras = saved;
            workspace.Notifications.ExpandedKeys = null; workspace.Notifications.ExpandedApplications.Clear(); PrivateCall(window, "ClearDesktopInbox");
        }
    }
}
