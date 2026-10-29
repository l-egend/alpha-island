using L = AlphaIsland.PluginSdk.Localization.UiText;
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
        TryMorphPillWidth();
    }
    private void OpenDesktopNotificationStack()
    {
        if (_sessionLocked || NotificationBurst.Count == 0) return;
        _workspace.Notifications.ExpandedKeys = NotificationBurst.Items.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        _workspace.Notifications.ExpandedApplications.Clear();
        _workspace.Notifications.ExpandedApplications.UnionWith(NotificationBurst.Items.Select(NotificationStackGroup.KeyFor));
        _notificationSearch.Text = ""; _notificationLevels.SelectedIndex = 0;
        _workspace.Notifications.Search = ""; _workspace.Notifications.MinimumImportance = NotificationImportance.Normal;
        _workspace.Notifications.UnreadOnly = false;
        SelectComponent(IslandPageIds.Notifications);
        FinishDesktopPopup(); RefreshDesktopInbox();
    }
    private void ShowAllDesktopNotifications()
    { _workspace.Notifications.ExpandedKeys = null; _workspace.Notifications.ExpandedApplications.Clear(); _workspace.Notifications.UnreadOnly = false; RefreshDesktopInbox(); }
    private void DismissDesktopNotification(DesktopNotification item)
    {
        RemoveNotificationViews(DesktopNotificationCards.Where(card => card.Notification.Key == item.Key), () =>
        {
            _desktopInbox.Dismiss(item.Key); NotificationBurst.Remove(item.Key); _workspace.Notifications.ExpandedKeys?.Remove(item.Key);
            if (NotificationBurst.Count > 0) RefreshNotificationStackPresentation();
            else if (_desktopCurrent?.Key == item.Key) FinishDesktopPopup();
        });
    }
    private int _notificationRemovalBatches;
    private readonly HashSet<FrameworkElement> _notificationRemoving = new();
    private void RemoveNotificationViews(IEnumerable<FrameworkElement> candidates, Action remove)
    {
        var views = _state == IslandState.Card && CardCarouselHost.Opacity > .01
            ? candidates.Where(view => view.IsVisible && !_notificationRemoving.Contains(view)).ToArray() : Array.Empty<FrameworkElement>();
        if (views.Length == 0) { remove(); RefreshDesktopInbox(); return; }
        _notificationRemovalBatches++;
        foreach (var view in views) _notificationRemoving.Add(view);
        try { remove(); }
        catch
        {
            foreach (var view in views) _notificationRemoving.Remove(view);
            _notificationRemovalBatches--; throw;
        }
        int remaining = views.Length;
        foreach (var view in views)
            NotificationDismissMotion.Play(view, () =>
            {
                _notificationRemoving.Remove(view);
                if (view.Parent is System.Windows.Controls.Panel parent) parent.Children.Remove(view);
                if (--remaining == 0) { _notificationRemovalBatches--; RefreshDesktopInbox(); }
            });
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
        int pendingCount = _desktopInbox.History.Count(n => n.Reminder is { Pending: true });
        _notificationHistoryTitle.Text = _workspace.Notifications.ExpandedKeys != null ? L.F($"本批通知 · {history.Length} 条") : pendingCount > 0 ? L.F($"通知中心 · 待处理 {pendingCount}") : L.T("通知中心");
        _notificationStackBack.Visibility = _workspace.Notifications.ExpandedKeys != null ? Visibility.Visible : Visibility.Collapsed;
        DesktopNotificationCard Create(DesktopNotification item)
        {
            var card = new DesktopNotificationCard(item);
            card.ToolTip = _desktopPolicy.Level(item) switch { NotificationImportance.Critical => L.T("紧急通知"), NotificationImportance.Important => L.T("重要通知"), _ => L.T("普通通知") };
            if (expandedText.Contains(item.Key)) card.SetFullContentExpanded(true);
            card.OpenRequested += OpenDesktopNotificationApp;
            card.DismissRequested += DismissDesktopNotification;
            card.BlockAppRequested += BlockDesktopNotificationApp;
            card.PinRequested += value => { _desktopInbox.SetPinned(value.Key, !value.IsPinned); RefreshDesktopInbox(); };
            card.ReadRequested += value => { _desktopInbox.MarkRead(value.Key); RefreshDesktopInbox(); };
            card.ReminderActionRequested += (value, action) => _ = ExecuteAssistantNotificationActionAsync(value, action);
            card.ReminderResponseRequested += RespondToAssistantNotification;
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
        foreach (var pinned in history.Where(n => n.IsPinned)) _notificationList.Children.Add(Create(pinned));
        foreach (var group in NotificationStackGroup.Create(history.Where(n => !n.IsPinned)))
        {
            if (group.Items.Length == 1) { _notificationList.Children.Add(Create(group.Latest)); continue; }
            var stack = new DesktopNotificationStackCard(group, Create);
            stack.SetExpanded(openApps.Contains(group.Key), animate: false);
            stack.ExpandedChanged += expanded => { if (expanded) openApps.Add(group.Key); else openApps.Remove(group.Key); };
            _notificationList.Children.Add(stack);
        }
        if (history.Length == 0) _notificationList.Children.Add(UtilityText(L.T("暂无通知。授权后接收新消息；静默消息保留在这里。"), 11));
    }
}
