using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;


namespace alpha_island;

public partial class MainWindow
{
    private DesktopNotificationBurst NotificationBurst => _workspace.Notifications.Burst;
    private TextBlock _notificationHistoryTitle = null!;
    private System.Windows.Controls.Button _notificationStackBack = null!;
    private System.Windows.Controls.TextBox _notificationSearch = null!;
    private System.Windows.Controls.ComboBox _notificationLevels = null!;
    private int NotificationStackCount => NotificationBurst.Count;
    private void RefreshNotificationStackPresentation()
    {
        if (NotificationBurst.Top(_desktopPolicy) is not { } top) return;
        _desktopCurrent = top; _notificationSummary.SetNotification(top); _notificationSummary.SetStackCount(NotificationBurst.Count);
        if (_layers.IsActive(DesktopToastKey))
            _layers.Request(IslandNotification.Seconds(DesktopToastKey, 120, opensIsland: false) with
            { Title = _notificationSummary.Title.Text, Detail = _notificationSummary.Detail.Text }, requestSync: false);
        SyncNotificationLayers();
    }
    private void OpenDesktopNotificationStack()
    {
        if (_sessionLocked || NotificationBurst.Count == 0) return;
        _workspace.Notifications.ExpandedKeys = NotificationBurst.Items.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        _workspace.Notifications.ExpandedApplications.Clear();
        _workspace.Notifications.ExpandedApplications.UnionWith(NotificationBurst.Items.Select(NotificationStackGroup.KeyFor));
        _notificationSearch.Text = ""; _notificationLevels.SelectedIndex = 0;
        _workspace.Notifications.Search = ""; _workspace.Notifications.MinimumImportance = NotificationImportance.Normal;
        SelectComponent(IslandPageIds.Notifications);
        FinishDesktopPopup(); RefreshDesktopInbox();
    }
    private void ShowAllDesktopNotifications()
    { _workspace.Notifications.ExpandedKeys = null; _workspace.Notifications.ExpandedApplications.Clear(); RefreshDesktopInbox(); }
    private void DismissDesktopNotification(DesktopNotification item)
    {
        _desktopInbox.Dismiss(item.Key); NotificationBurst.Remove(item.Key); _workspace.Notifications.ExpandedKeys?.Remove(item.Key);
        if (NotificationBurst.Count > 0) RefreshNotificationStackPresentation();
        else if (_desktopCurrent?.Key == item.Key) FinishDesktopPopup();
        RefreshDesktopInbox();
    }
    private void MergePendingDesktopMessages(DateTimeOffset now)
    {
        if (_desktopCurrent?.Kind != DesktopEventKind.Message || !NotificationBurst.CanMerge(now)) return;
        var additions = _desktopInbox.TakePendingMessages(_desktopPolicy, _sessionLocked, now);
        if (additions.Length == 0) return;
        foreach (var message in additions) NotificationBurst.Add(message, now);
        var deadline = now.AddSeconds(SettingsRegistry.GetInt(_options, "NotificationDisplaySeconds", 8));
        _desktopExpires = deadline < NotificationBurst.Started.AddSeconds(30) ? deadline : NotificationBurst.Started.AddSeconds(30);
        RefreshNotificationStackPresentation();
    }

    private IEnumerable<DesktopNotificationCard> DesktopNotificationCards => _notificationList.Children.OfType<DesktopNotificationCard>()
        .Concat(_notificationList.Children.OfType<DesktopNotificationStackCard>().SelectMany(stack => stack.Cards));

    private void RenderDesktopNotificationStacks()
    {
        var expandedText = DesktopNotificationCards.Where(card => card.FullContentExpanded).Select(card => card.Notification.Key).ToHashSet(StringComparer.Ordinal);
        var history = _workspace.Notifications.Filter(_desktopPolicy).Take(50).ToArray();
        DesktopNotificationIcons.PrimeLogos(history);
        var openApps = _workspace.Notifications.ExpandedApplications;
        openApps.IntersectWith(_desktopInbox.History.Select(NotificationStackGroup.KeyFor));
        _notificationList.Children.Clear();
        _notificationHistoryTitle.Text = _workspace.Notifications.ExpandedKeys != null ? $"本批通知 · {history.Length} 条" : "通知中心";
        _notificationStackBack.Visibility = _workspace.Notifications.ExpandedKeys != null ? Visibility.Visible : Visibility.Collapsed;
        DesktopNotificationCard Create(DesktopNotification item)
        {
            var card = new DesktopNotificationCard(item);
            card.ToolTip = _desktopPolicy.Level(item) switch { NotificationImportance.Critical => "紧急通知", NotificationImportance.Important => "重要通知", _ => "普通通知" };
            if (expandedText.Contains(item.Key)) card.SetFullContentExpanded(true);
            card.OpenRequested += OpenDesktopNotificationApp;
            card.DismissRequested += DismissDesktopNotification;
            card.BlockAppRequested += BlockDesktopNotificationApp;
            card.MouseLeftButtonUp += (_, args) =>
            {
                if (MessengerIdentity.FromNotification(item) == null || !item.CanOpenApp) return;
                for (DependencyObject? node = args.OriginalSource as DependencyObject; node != null && node != card;
                    node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
                    if (node is ButtonBase or System.Windows.Controls.TextBox) return;
                args.Handled = true; OpenDesktopNotificationApp(item);
            };
            return card;
        }
        foreach (var group in NotificationStackGroup.Create(history))
        {
            if (group.Items.Length == 1) { _notificationList.Children.Add(Create(group.Latest)); continue; }
            var stack = new DesktopNotificationStackCard(group, Create);
            stack.SetExpanded(openApps.Contains(group.Key), animate: false);
            stack.ExpandedChanged += expanded => { if (expanded) openApps.Add(group.Key); else openApps.Remove(group.Key); };
            _notificationList.Children.Add(stack);
        }
        if (history.Length == 0) _notificationList.Children.Add(UtilityText("暂无通知。授权后接收新消息；静默消息保留在这里。", 11));
    }
}
