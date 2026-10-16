using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using alpha_island.Core;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

internal static partial class Program
{
    private static void NotificationFullContentUiChecks(alpha_island.MainWindow window, Grid root)
    {
        var message = new DesktopNotification("notification-full-content", "Contoso.Chat!App", "聊天",
            string.Join("\n", Enumerable.Range(1, 6).Select(i => "完整标题第 " + i + " 行")),
            string.Join("\n", Enumerable.Range(1, 35).Select(i => "正文第 " + i + " 行：这部分内容必须能够阅读、选中和复制。")),
            DateTimeOffset.Now);
        PrivateCall(window, "ClearDesktopInbox");
        PrivateCall(window, "SetState", IslandState.Bar, true);
        PrivateCall(window, "OnSystemToastsReceived", new[] { message }, false);
        PrivateCall(window, "IslandBody_MouseLeftButtonDown", window.IslandBody,
            new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
        PumpAnimations(100); root.UpdateLayout();
        var card = AgentField<StackPanel>(window, "_notificationList").Children
            .OfType<alpha_island.Controls.DesktopNotificationCard>().Single();
        var content = FullContentDescendants(card).OfType<TextBox>().SingleOrDefault(t => t.IsReadOnly);
        Check(content != null, "点击通知小岛后，长通知有可选择的全文阅读入口，不只保留截断摘要");
        var fullTextButton = FullContentDescendants(card).OfType<Button>().SingleOrDefault(b => Equals(b.Content, "收起全文"));
        Check(fullTextButton != null && content!.Visibility == Visibility.Visible,
            "点击当前通知直接展开全文，并提供明确的收起按钮");

        root.UpdateLayout();
        Check(content!.Visibility == Visibility.Visible && content.Text.Contains("完整标题第 6 行")
            && content.Text.EndsWith("正文第 35 行：这部分内容必须能够阅读、选中和复制。", StringComparison.Ordinal),
            "展开全文可阅读完整多行标题与最后一行正文，末尾内容不丢失");
        Check(content.TextWrapping == TextWrapping.Wrap && content.VerticalScrollBarVisibility == ScrollBarVisibility.Auto
            && content.ExtentHeight > content.ViewportHeight,
            "长通知全文在岛内滚动，不继续放大通知小岛或裁掉末尾");
        content.ScrollToEnd(); content.Select(content.Text.Length - 10, 10);
        root.UpdateLayout();
        Check(content.VerticalOffset > 0 && content.SelectedText == content.Text[^10..],
            "通知末尾可滚动到达并选中文字复制");
        SaveNotificationContentPreview(card, "notification-full-content.png");
        fullTextButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(content.Visibility == Visibility.Collapsed && Equals(fullTextButton.Content, "查看全文"),
            "通知全文可以再次收起为摘要");
        PrivateCall(window, "ClearDesktopInbox");
    }

    private static IEnumerable<DependencyObject> FullContentDescendants(DependencyObject parent)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in FullContentDescendants(child)) yield return descendant;
        }
    }
}
