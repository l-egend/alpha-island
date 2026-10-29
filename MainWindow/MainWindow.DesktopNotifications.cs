using L = AlphaIsland.PluginSdk.Localization.UiText;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Controls;
using alpha_island.Services;
using alpha_island.Interop;
using Microsoft.Win32;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Panel = System.Windows.Controls.Panel;

namespace alpha_island;

public partial class MainWindow
{
    private const string DesktopToastKey = "desktoptoast";
    private DesktopNotificationInbox _desktopInbox => _workspace.Notifications.Inbox;
    private DesktopNotificationPolicy _desktopPolicy => _workspace.Notifications.Policy ?? throw new InvalidOperationException(L.T("通知策略尚未初始化"));
    private SystemToastService? _systemToasts;
    private Action<DesktopNotification>? _suppressSystemPopup;
    private DesktopNotification? _desktopCurrent { get => _workspace.Notifications.Current; set => _workspace.Notifications.Current = value; }
    private Grid _notificationsView = null!;
    private UIElement? _notificationRoot;
    private StackPanel _notificationList = null!;
    private TextBlock _notificationAccessText = null!;
    private DesktopNotificationPill _notificationSummary = null!;
    private CheckBox _notificationQuiet = null!;
    private Button _notificationUnreadOnly = null!;
    private IslandTimer? _notificationUiTimer;
    private DateTimeOffset _desktopExpires { get => _workspace.Notifications.Expires; set => _workspace.Notifications.Expires = value; }
    private DateTime _notificationCalendarDate = DateTime.MinValue;
    private bool _sessionEventsStarted, _sessionLocked, _notificationAutoPill, _refreshingQuiet;
    private IslandState _notificationReturnState;
    private string _notificationReturnKey = "music";

    private void RegisterDesktopNotificationPage()
    {
        _workspace.Notifications.Policy = new DesktopNotificationPolicy(_options, () => GameDoNotDisturbActive);
        if (_runtimeEnabled) _workspace.Notifications.AttachArchive(System.IO.Path.Combine(AppPaths.Root, "notification-history.dat"),
            SettingsRegistry.GetBool(_options, "NotificationRememberHistory", true), IslandTicker.Shared(Dispatcher));
        RegisterGameQuietLayer();
        _notificationRoot = Content as UIElement;
        _notificationSummary = new DesktopNotificationPill();
        _notificationSummary.StackRequested += OpenDesktopNotificationStack;
        ((Panel)NetSpeedNotifyLayer.Parent).Children.Add(_notificationSummary);
        _layers.Register(new IslandLayerSpec(DesktopToastKey, _notificationSummary, Priority: 60,
            PriorityOverride: () => _desktopCurrent == null ? 60 : _desktopPolicy.Priority(_desktopCurrent)));

        _notificationsView = new Grid { Height = 194, VerticalAlignment = VerticalAlignment.Center, RenderTransform = new TranslateTransform() };
        _notificationsView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _notificationsView.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var heading = new StackPanel { Margin = new Thickness(4, 4, 4, 6) };
        var bar = new Grid(); bar.ColumnDefinitions.Add(new ColumnDefinition()); bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _notificationHistoryTitle = UtilityText(L.T("通知中心"), 14); bar.Children.Add(_notificationHistoryTitle);
        var quietLabel = UtilityText(L.T("静默"), 10); quietLabel.VerticalAlignment = VerticalAlignment.Center; quietLabel.Margin = new Thickness(0, 0, 7, 0);
        Grid.SetColumn(quietLabel, 1); bar.Children.Add(quietLabel);
        _notificationQuiet = new CheckBox { Style = (Style)FindResource("W11Switch"), VerticalAlignment = VerticalAlignment.Center };
        _notificationQuiet.Checked += (_, _) => SetNotificationQuiet(true); _notificationQuiet.Unchecked += (_, _) => SetNotificationQuiet(false);
        Grid.SetColumn(_notificationQuiet, 2); bar.Children.Add(_notificationQuiet); heading.Children.Add(bar);
        _notificationAccessText = UtilityText(L.T("等待通知授权"), 10); heading.Children.Add(_notificationAccessText);
        _notificationStackBack = DesktopNotificationCard.ActionButton(L.T("全部通知"), ShowAllDesktopNotifications);
        _notificationStackBack.HorizontalAlignment = HorizontalAlignment.Left; _notificationStackBack.Visibility = Visibility.Collapsed; heading.Children.Add(_notificationStackBack);
        heading.Children.Add(UtilityButtons(DesktopNotificationCard.ActionButton(L.T("授权读取"), async () => await RequestToastAccessAsync()),
            DesktopNotificationCard.ActionButton(L.T("权限设置"), OpenNotificationPrivacySettings), DesktopNotificationCard.ActionButton(L.T("清空全部"), ClearDesktopInbox)));
        _notificationUnreadOnly = DesktopNotificationCard.ActionButton(L.T("仅看未读"), () => { _workspace.Notifications.UnreadOnly = !_workspace.Notifications.UnreadOnly; RefreshDesktopInbox(); });
        heading.Children.Add(UtilityButtons(_notificationUnreadOnly, DesktopNotificationCard.ActionButton(L.T("全部已读"), () => { _desktopInbox.MarkAllRead(); RefreshDesktopInbox(); })));
        var filters = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        filters.ColumnDefinitions.Add(new ColumnDefinition()); filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var search = new System.Windows.Controls.TextBox { MinHeight = 25, Margin = new Thickness(0, 0, 6, 0), ToolTip = L.T("搜索应用、联系人或消息内容") };
        _notificationSearch = search; search.TextChanged += (_, _) => { _workspace.Notifications.Search = search.Text.Trim(); RefreshDesktopInbox(); };
        filters.Children.Add(search);
        var levels = new System.Windows.Controls.ComboBox { ItemsSource = new[] { L.T("全部通知"), L.T("重要及以上"), L.T("紧急通知") }, SelectedIndex = 0, MinWidth = 98 };
        _notificationLevels = levels; levels.SelectionChanged += (_, _) => { _workspace.Notifications.MinimumImportance = (NotificationImportance)Math.Max(0, levels.SelectedIndex); RefreshDesktopInbox(); };
        Grid.SetColumn(levels, 1); filters.Children.Add(levels); heading.Children.Add(filters);
        _notificationsView.Children.Add(heading);
        _notificationList = new StackPanel();
        var scroll = new ScrollViewer { Content = _notificationList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        // 消息列表的滚轮用于阅读，不向岛内翻页冒泡。
        scroll.PreviewMouseWheel += (_, e) =>
        {
            for (DependencyObject? node = e.OriginalSource as DependencyObject; node != null;
                node = node is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            {
                if (node is System.Windows.Controls.TextBox { IsReadOnly: true } reader)
                {
                    reader.ScrollToVerticalOffset(reader.VerticalOffset - e.Delta / 3.0);
                    e.Handled = true; return;
                }
                if (ReferenceEquals(node, scroll)) break;
            }
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta / 3.0); e.Handled = true;
        };
        Grid.SetRow(scroll, 1); _notificationsView.Children.Add(scroll); CardViewport.Children.Add(_notificationsView);
        _pages.Register(new DelegateIslandPage(IslandPageIds.Notifications, "通知中心", _notificationsView, 8,
            366, activated: RefreshDesktopInbox, deactivated: () => { _workspace.Notifications.ExpandedKeys = null; _workspace.Notifications.ExpandedApplications.Clear(); _workspace.Notifications.UnreadOnly = false; },
            viewportChanged: height => _notificationsView.Height = height));
        RefreshDesktopInbox();
    }
    private void StartSystemToastService()
    {
        if (_systemToasts != null) return;
        _systemToasts = new SystemToastService(Dispatcher);
        _systemToasts.SetBannerReplacementEnabled(SettingsRegistry.GetBool(_options, "NotificationReplaceSystemPopup", true));
        _suppressSystemPopup = item => _systemToasts?.SuppressPopup(item);
        _systemToasts.Received += OnSystemToastsReceived;
        _systemToasts.StatusChanged += RefreshDesktopAccess;
        _systemToasts.Start(); StartNotificationUiTimer();
    }
    private void StopSystemToastService()
    {
        _systemToasts?.Dispose(); _systemToasts = null; _suppressSystemPopup = null;
        if (_messengerNotifications == null) { _desktopInbox.DropPending(); FinishDesktopPopup(); }
        RefreshDesktopAccess(); StopNotificationTimerIfUnused();
    }
    private async System.Threading.Tasks.Task RequestToastAccessAsync()
    {
        SettingsRegistry.Write(_options, "SystemToastEnabled", true);
        if (_runtimeEnabled) { _options.Save(allowRecovery: true); _services.Sync(_options); }
        else StartSystemToastService();
        if (_systemToasts != null) await _systemToasts.RequestAccessAsync();
        RefreshDesktopAccess();
    }
    private static void OpenNotificationPrivacySettings()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-notifications") { UseShellExecute = true }); }
        catch (Exception ex)
        {
            IslandLog.Warn("Toast", "打开通知读取权限页失败，尝试通知设置：" + ex.Message);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true }); }
            catch (Exception fallback) { IslandLog.Warn("Toast", "打开通知设置失败：" + fallback.Message); }
        }
    }
    private void RefreshDesktopAccess()
    {
        if (_notificationAccessText != null) _notificationAccessText.Text = (_systemToasts == null ? L.T("Windows 通知监听已关闭")
            : _systemToasts.Status + " · " + _systemToasts.BannerTakeoverStatus)
            + (_messengerNotifications == null ? "" : "\n" + _messengerNotifications.Status)
            + (GameDoNotDisturbActive ? L.T("\n游戏免打扰已开启 · ") + _gameQuietName : "");
    }
    private void OnSystemToastsReceived(System.Collections.Generic.IReadOnlyList<DesktopNotification> messages, bool initial)
        => ReceiveDesktopNotifications(messages, initial, MessengerNoticeSource.Windows);
    private void RefreshDesktopInbox()
    {
        if (_notificationList == null) return;
        _notificationUnreadOnly.Content = _workspace.Notifications.UnreadOnly ? L.T("查看全部") : L.T("仅看未读");
        _refreshingQuiet = true; _notificationQuiet.IsChecked = NotificationQuietEffective; _refreshingQuiet = false;
        _notificationQuiet.IsEnabled = !GameDoNotDisturbActive;
        _notificationQuiet.ToolTip = GameDoNotDisturbActive ? L.T("游戏退出后恢复你原来的静默设置") : L.T("普通消息仅保留在通知中心");
        RefreshDesktopAccess();
        if (_pages.ActiveId != IslandPageIds.Notifications) return;
        if (_notificationRemovalBatches == 0) RenderDesktopNotificationStacks();
        RefreshDesktopAccess();
    }
    private bool _openingDesktopApp;
    private Func<DesktopNotification, bool> _notificationAppOpener = item => MessengerIdentity.FromNotification(item) is { } client
        ? MessengerWindowReader.TryOpen(client) : NotificationAppLauncher.TryOpen(item.AppId);
    private async void OpenDesktopNotificationApp(DesktopNotification item)
    {
        if (!_sessionLocked && item.Reminder != null)
        { _desktopInbox.MarkRead(item.Key); _workspace.Notifications.ExpandedKeys = null; _workspace.Notifications.UnreadOnly = false; FinishDesktopPopup(); SelectComponent(IslandPageIds.Notifications); return; }
        if (!_sessionLocked && item.Kind == DesktopEventKind.Weather)
        { FinishDesktopPopup(); SelectComponent("weather"); return; }
        if (_sessionLocked || !item.CanOpenApp || _openingDesktopApp) return;
        _openingDesktopApp = true;
        string? popupKey = _desktopCurrent?.Key;
        try
        {
            bool opened = await Task.Run(() => _notificationAppOpener(item));
            if (_sessionLocked) return;
            if (!opened) _notificationAccessText.Text = MessengerIdentity.FromNotification(item) is { } messenger
                ? L.T("无法调用") + MessengerIdentity.Name(messenger) + L.T("入口，请从右下角托盘打开。") : L.T("无法打开此应用，请从开始菜单打开。");
            else { _desktopInbox.MarkRead(item.Key); if (_desktopCurrent?.Key == popupKey) FinishDesktopPopup(); }
        }
        catch (Exception ex) { IslandLog.Debug("Notifications", "应用跳转失败：" + ex.GetType().Name); }
        finally { _openingDesktopApp = false; }
    }
    private bool TryOpenMessengerNotification(string? key)
    {
        if (key != DesktopToastKey || _desktopCurrent is not { CanOpenApp: true } item
            || MessengerIdentity.FromNotification(item) == null) return false;
        // Reuse the asynchronous, guarded tray activation used by the app button.
        OpenDesktopNotificationApp(item);
        return true;
    }
    private void ExpandCurrentNotification()
    {
        var current = DesktopNotificationCards
            .FirstOrDefault(card => card.Notification.Key == _desktopCurrent?.Key);
        current?.SetFullContentExpanded(true);
    }
    private void BlockDesktopNotificationApp(DesktopNotification item)
    {
        if (!item.CanOpenApp) return;
        string current = SettingsRegistry.GetText(_options, "NotificationBlockedApps", "");
        SettingsRegistry.Write(_options, "NotificationBlockedApps", current.Length == 0 ? item.AppId : current + ";" + item.AppId);
        if (_runtimeEnabled) _options.Save(allowRecovery: true);
        NotificationBurst.Retain(value => _desktopPolicy.CanPopup(value, _sessionLocked));
        if (NotificationBurst.Count > 0) RefreshNotificationStackPresentation();
        else if (_desktopCurrent?.AppId == item.AppId) FinishDesktopPopup(); RefreshDesktopInbox();
    }
    private void SetNotificationQuiet(bool quiet)
    {
        if (_refreshingQuiet) return;
        SettingsRegistry.Write(_options, "NotificationQuietMode", quiet);
        if (_runtimeEnabled) _options.Save(allowRecovery: true);
        OnDesktopNotificationOptionsChanged();
    }
    private void OnDesktopNotificationOptionsChanged()
    {
        _workspace.Notifications.SetRemember(SettingsRegistry.GetBool(_options, "NotificationRememberHistory", true));
        SyncVolumeFlyoutTakeover();
        _systemToasts?.SetBannerReplacementEnabled(SettingsRegistry.GetBool(_options, "NotificationReplaceSystemPopup", true));
        if (NotificationBurst.Count > 0)
        {
            NotificationBurst.Retain(item => _desktopPolicy.CanPopup(item, _sessionLocked));
            if (NotificationBurst.Count == 0) FinishDesktopPopup(); else RefreshNotificationStackPresentation();
        }
        else if (_desktopCurrent != null && !_desktopPolicy.CanPopup(_desktopCurrent, _sessionLocked)) FinishDesktopPopup();
        RefreshDesktopInbox();
        _assistantReminders?.TickNow();
    }
    private void ClearDesktopInbox() => RemoveNotificationViews(_notificationList.Children.OfType<FrameworkElement>(), () =>
    {
        _workspace.Notifications.ExpandedApplications.Clear(); _workspace.Notifications.ExpandedKeys = null;
        _desktopInbox.Clear(); FinishDesktopPopup();
    });
    private void StartNotificationUiTimer()
    {
        if (_notificationUiTimer != null) return;
        _notificationUiTimer = new IslandTimer(Dispatcher, "notifications-ui") { Interval = TimeSpan.FromMilliseconds(300) };
        _notificationUiTimer.Tick += OnNotificationUiTick; _notificationUiTimer.Start();
    }
    private void OnNotificationUiTick(object? sender, EventArgs e) => TickDesktopNotifications();
    private void StopNotificationTimerIfUnused()
    {
        if (_systemToasts != null || _messengerNotifications != null || _sessionEventsStarted || _radioMonitoring || _desktopCurrent != null || _desktopInbox.PendingCount > 0) return;
        if (_notificationUiTimer != null) { _notificationUiTimer.Dispose(); _notificationUiTimer.Tick -= OnNotificationUiTick; _notificationUiTimer = null; }
    }
    private void TickDesktopNotifications()
    {
        if (_exiting || _sessionLocked) return;
        if (ComponentSwitchGestureActive) return;
        var now = DateTimeOffset.Now;
        if (_pages.ActiveId == IslandPageIds.Notifications && _notificationCalendarDate != now.Date)
        {
            foreach (var card in DesktopNotificationCards) card.RefreshTimestamp(now);
            foreach (var stack in _notificationList.Children.OfType<DesktopNotificationStackCard>()) stack.RefreshTimestamp(now);
            _notificationCalendarDate = now.Date;
        }
        if (_desktopCurrent != null)
        {
            MergePendingDesktopMessages(now);
            if (now < _desktopExpires) return;
            FinishDesktopPopup();
        }
        if (_approvalCurrent != null || _layers.IsActive("volume") || _layers.IsActive("battery")
            || _state == IslandState.Card) return;
        var next = _desktopInbox.TakeNext(_desktopPolicy, _sessionLocked, now);
        if (next == null) { StopNotificationTimerIfUnused(); return; }
        _notificationReturnState = _state == IslandState.Bar ? IslandState.Bar : IslandState.Pill;
        _notificationReturnKey = _activeMainIslandKey;
        _desktopCurrent = next; _desktopExpires = now.AddSeconds(next.Kind == DesktopEventKind.BootTiming ? 15 : SettingsRegistry.GetInt(_options, "NotificationDisplaySeconds", 8));
        if (next.Kind == DesktopEventKind.Message)
        {
            NotificationBurst.Begin(next, now);
            foreach (var message in _desktopInbox.TakePendingMessages(_desktopPolicy, _sessionLocked, now)) NotificationBurst.Add(message, now);
            next = NotificationBurst.Top(_desktopPolicy) ?? next; _desktopCurrent = next;
        }
        else NotificationBurst.Clear();
        _notificationSummary.SetNotification(next); _notificationSummary.SetStackCount(NotificationBurst.Count);
        ShowPillNotification(IslandNotification.Seconds(DesktopToastKey, 120) with
        { Title = _notificationSummary.Title.Text, Detail = _notificationSummary.Detail.Text });
        // Messages stay compact; QQ/WeChat clicks open the client, other messages open notification details.
        _notificationAutoPill = true;
        RefreshDesktopInbox();
    }
    private void FinishDesktopPopup()
    {
        bool closePopup = _notificationAutoPill && _state == IslandState.Pill && _activeMainIslandKey == DesktopToastKey;
        _desktopCurrent = null; NotificationBurst.Clear(); _notificationSummary.SetStackCount(0);
        _notificationAutoPill = false;
        if (_layers.IsRegistered(DesktopToastKey)) _layers.Hide(DesktopToastKey);
        if (closePopup && !_exiting && _approvalCurrent == null)
        {
            if (CanRetainIsland(_notificationReturnKey)) SwitchMainIsland(_notificationReturnKey);
            else if (_activeMainIslandKey == DesktopToastKey)
            {
                // 细条没有活跃岛时也要移除已结束的消息键，保留用户原来的音乐收起状态。
                _activeMainIslandKey = "music"; _layers.PreferredWinnerKey = "music";
            }
            // Activity may have started while the popup owned the pill. Its saved
            // return shape must not hide a still-active background assistant or player.
            SetState(_layers.AnyPinned || HasVisibleMusicPin || IsIslandActive(_activeMainIslandKey)
                ? IslandState.Pill : _notificationReturnState);
        }
    }
    private void StartSessionEvents()
    {
        if (_sessionEventsStarted) return;
        SystemEvents.SessionSwitch += OnSessionSwitch; _sessionEventsStarted = true; StartNotificationUiTimer();
    }
    private void StopSessionEvents()
    {
        if (_sessionEventsStarted) SystemEvents.SessionSwitch -= OnSessionSwitch;
        _sessionEventsStarted = false; StopNotificationTimerIfUnused();
    }
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs args)
    {
        if (args.Reason is not (SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock)) return;
        Dispatcher.BeginInvoke(new Action(() => { if (!_exiting) ApplySessionNotification(args.Reason == SessionSwitchReason.SessionLock); }));
    }
    internal void ApplySessionNotification(bool locked)
    {
        _sessionLocked = locked;
        if (locked) { _notificationAudio?.Stop(); _screenTools?.StopSubtitles(); _screenTools?.Hide(); }
        if (locked && _voiceAssistant?.Visible == true) _ = _voiceAssistant.CancelAsync();
        ConfigureVoiceAssistant();
        SyncVolumeFlyoutTakeover();
        if (locked) { CloseComponentSwitcher(); _desktopInbox.DropPending(); FinishDesktopPopup(); }
        if (_notificationRoot != null) _notificationRoot.Visibility = locked ? Visibility.Hidden : Visibility.Visible;
        var item = new DesktopNotification("session-" + DateTimeOffset.UtcNow.UtcTicks, "", "Windows", locked ? L.T("电脑已锁定") : L.T("欢迎回来"), locked ? L.T("锁屏期间暂停弹出消息，保护通知内容。") : L.T("电脑已解锁"), DateTimeOffset.Now, locked ? DesktopEventKind.Locked : DesktopEventKind.Unlocked);
        _desktopInbox.Add(item, !locked && _desktopPolicy.CanPopup(item, false)); RefreshDesktopInbox();
    }
    private void RecordBatteryDesktopEvent(BatteryStatusInfo info)
    {
        if (info.IsCharging || info.IsOnAcPower == true || info.Percent > 20)
            foreach (var prior in _desktopInbox.History.Where(n => n.Reminder is { Rule: "battery", Pending: true }).ToArray()) _desktopInbox.ResolveReminder(prior.Key, L.T("电源或电量状态已恢复"));
        var kind = info.EventType switch
        {
            BatteryPowerEvent.PluggedIn => DesktopEventKind.PowerConnected,
            BatteryPowerEvent.Unplugged => DesktopEventKind.PowerDisconnected,
            BatteryPowerEvent.LowBattery => DesktopEventKind.LowBattery,
            BatteryPowerEvent.CriticalBattery => DesktopEventKind.CriticalBattery,
            _ => DesktopEventKind.System
        };
        if (kind == DesktopEventKind.System) return;
        var title = kind switch { DesktopEventKind.PowerConnected => L.T("已接通电源"), DesktopEventKind.PowerDisconnected => L.T("已拔出电源"), DesktopEventKind.CriticalBattery => L.T("电池电量严重不足"), _ => L.T("电池电量不足") };
        string context = kind is DesktopEventKind.LowBattery or DesktopEventKind.CriticalBattery ? BatteryContextDescription() : "";
        var item = new DesktopNotification(context.Length > 0 ? "assistant:battery:current" : "battery-" + DateTimeOffset.UtcNow.UtcTicks, "", "Windows", title,
            (info.Percent >= 0 ? L.F($"当前电量 {info.Percent}%") : L.T("电量未知")) + (context.Length > 0 ? "\n" + context : ""), DateTimeOffset.Now, kind);
        if (context.Length > 0) item = item with { IsPinned = true, Reminder = new("battery", true, DateTimeOffset.Now.AddHours(4), [new("power", L.T("电源设置"))]) };
        _desktopInbox.Add(item, false); RefreshDesktopInbox();
    }
    private void RecordVolumeDesktopEvent(VolumeInfo info)
    {
        _desktopInbox.Add(new("event-volume", "", "Windows", info.IsMuted ? L.T("系统音量已静音") : L.T("系统音量已调整"), L.F($"当前音量 {info.Percent}%"), DateTimeOffset.Now, DesktopEventKind.Volume), false);
        if (_pages.ActiveId == IslandPageIds.Notifications) RefreshDesktopInbox();
    }
}
