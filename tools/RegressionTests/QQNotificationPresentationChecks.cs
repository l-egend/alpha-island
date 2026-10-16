using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Controls;

internal static partial class Program
{
    private static void QQNotificationPresentationChecks(alpha_island.MainWindow window, Grid root)
    {
        var qq = MessengerTestNotice(MessengerClient.QQ, "qq-layout", "elliot", "test") with
        { Logo = TestNotificationImage(System.Windows.Media.Colors.Cyan), ContentImage = TestNotificationImage(System.Windows.Media.Colors.OrangeRed) };
        PrivateCall(window, "ClearDesktopInbox");
        PrivateCall(window, "SetState", IslandState.Bar, true);
        PrivateCall(window, "OnMessengerNotificationsReceived", new[] { qq }, MessengerNoticeSource.Popup);
        PumpAnimations(100); root.UpdateLayout();
        var pill = AgentField<DesktopNotificationPill>(window, "_notificationSummary");
        Check(pill.Title.Text == "elliot" && pill.Detail.Text == "test", "QQ 小岛上方显示联系人，下方直接显示消息正文");
        Check(ReferenceEquals(pill.Icon.Source, DesktopNotificationIcons.GetLogo(qq)), "QQ 左侧使用应用图标，不被联系人头像或通知配图替代");
        var preview = new DesktopNotificationPill(); preview.SetNotification(qq);
        preview.Measure(new System.Windows.Size(328, 34)); preview.Arrange(new System.Windows.Rect(0, 0, 328, 34)); preview.UpdateLayout();
        SaveNotificationContentPreview(preview, "qq-contact-message-pill.png");
        pill.SetNotification(qq with { Body = "第一行\n第二行\t第三行" });
        Check(pill.Title.Text == "elliot" && pill.Detail.Text == "第一行 第二行 第三行", "QQ 多行消息在小岛合并为一行预览，联系人独立保留");
        pill.SetNotification(qq with { Body = new string('消', 500) });
        Check(pill.Detail.TextTrimming == TextTrimming.CharacterEllipsis && qq.Body == "test", "长消息小岛使用省略显示，保留原通知完整正文");
        var card = new DesktopNotificationCard(qq);
        Check(card.TitleText.Text == "elliot" && card.BodyText.Text == "test", "QQ 卡片保留联系人和消息内容");
        PrivateCall(window, "ClearDesktopInbox");
    }
}
