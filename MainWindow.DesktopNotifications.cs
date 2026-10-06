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
    private readonly DesktopNotificationInbox _desktopInbox = new();
    private DesktopNotificationPolicy _desktopPolicy = null!;
    private SystemToastService? _systemToasts;
    private DesktopNotification? _desktopCurrent;
    private Grid _notificationsView = null!;
    private UIElement? _notificationRoot;
    private StackPanel _notificationList = null!;
    private TextBlock _notificationAccessText = null!;
    private DesktopNotificationPill _notificationSummary = null!;
    private CheckBox _notificationQuiet = null!;
    private DispatcherTimer? _notificationUiTimer;
    private DateTimeOffset _desktopExpires;
    private bool _sessionEventsStarted, _sessionLocked, _notificationAutoCard, _notificationAutoPill, _refreshingQuiet;
    private IslandState _notificationReturnState;
    private string _notificationReturnKey = "music";

    private void RegisterDesktopNotificationPage()
    {
        _desktopPolicy = new DesktopNotificationPolicy(_options);
        _notificationRoot = Content as UIElement;
        _notificationSummary = new DesktopNotificationPill();
        ((Panel)NetSpeedNotifyLayer.Parent).Children.Add(_notificationSummary);
        _layers.Register(new IslandLayerSpec(DesktopToastKey, _notificationSummary, Priority: 60,
            PriorityOverride: () => _desktopCurrent == null ? 60 : _desktopPolicy.Priority(_desktopCurrent)));

        _notificationsView = new Grid { Height = 194, VerticalAlignment = VerticalAlignment.Center, RenderTransform = new TranslateTransform() };
        _notificationsView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _notificationsView.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var heading = new StackPanel { Margin = new Thickness(4, 4, 4, 6) };
        var bar = new Grid(); bar.ColumnDefinitions.Add(new ColumnDefinition()); bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(UtilityText("通知中心", 14));
        var quietLabel = UtilityText("静默", 10); quietLabel.VerticalAlignment = VerticalAlignment.Center; quietLabel.Margin = new Thickness(0, 0, 7, 0);
        Grid.SetColumn(quietLabel, 1); bar.Children.Add(quietLabel);
        _notificationQuiet = new CheckBox { Style = (Style)FindResource("W11Switch"), VerticalAlignment = VerticalAlignment.Center };
        _notificationQuiet.Checked += (_, _) => SetNotificationQuiet(true); _notificationQuiet.Unchecked += (_, _) => SetNotificationQuiet(false);
        Grid.SetColumn(_notificationQuiet, 2); bar.Children.Add(_notificationQuiet); heading.Children.Add(bar);
        _notificationAccessText = UtilityText("等待通知授权", 10); heading.Children.Add(_notificationAccessText);
        heading.Children.Add(UtilityButtons(DesktopNotificationCard.ActionButton("授权读取", async () => await RequestToastAccessAsync()),
            DesktopNotificationCard.ActionButton("权限设置", OpenNotificationPrivacySettings), DesktopNotificationCard.ActionButton("清空记录", ClearDesktopInbox)));
        _notificationsView.Children.Add(heading);
        _notificationList = new StackPanel();
        var scroll = new ScrollViewer { Content = _notificationList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        // 消息列表的滚轮用于阅读，不向岛内翻页冒泡。
        scroll.PreviewMouseWheel += (_, e) => { scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta / 3.0); e.Handled = true; };
        Grid.SetRow(scroll, 1); _notificationsView.Children.Add(scroll); CardViewport.Children.Add(_notificationsView);
        _pages.Register(new DelegateIslandPage(IslandPageIds.Notifications, "通知中心", _notificationsView, 8,
            300, activated: () => { _notificationAutoCard = false; RefreshDesktopInbox(); },
            viewportChanged: height => _notificationsView.Height = height));
        RefreshDesktopInbox();
    }
    private void StartSystemToastService()
    {
        if (_systemToasts != null) return;
        _systemToasts = new SystemToastService(Dispatcher);
        _systemToasts.Received += OnSystemToastsReceived;
        _systemToasts.StatusChanged += RefreshDesktopAccess;
        _systemToasts.Start(); StartNotificationUiTimer();
    }
    private void StopSystemToastService()
    {
        _systemToasts?.Dispose(); _systemToasts = null;
        _desktopInbox.DropPending(); FinishDesktopPopup(); RefreshDesktopAccess(); StopNotificationTimerIfUnused();
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
        catch (Exception ex) { IslandLog.Warn("Toast", "打开系统通知权限页失败：" + ex.Message); }
    }
    private void RefreshDesktopAccess()
    {
        if (_notificationAccessText != null) _notificationAccessText.Text = _systemToasts?.Status ?? "通知监听已关闭";
    }
    private void OnSystemToastsReceived(System.Collections.Generic.IReadOnlyList<DesktopNotification> messages, bool initial)
    {
        if (_exiting) return;
        foreach (var item in messages)
        {
            if (!_desktopPolicy.Accepts(item)) continue;
            _desktopInbox.Add(item, !initial && _desktopPolicy.CanPopup(item, _sessionLocked), _desktopPolicy);
        }
        RefreshDesktopInbox(); TickDesktopNotifications();
    }
    private void RefreshDesktopInbox()
    {
        if (_notificationList == null) return;
        _refreshingQuiet = true; _notificationQuiet.IsChecked = SettingsRegistry.GetBool(_options, "NotificationQuietMode", false); _refreshingQuiet = false;
        RefreshDesktopAccess();
        if (_pages.ActiveId != IslandPageIds.Notifications) return;
        _notificationList.Children.Clear();
        var history = _desktopInbox.History.Where(_desktopPolicy.Accepts).ToArray();
        if (_desktopCurrent != null) history = new[] { _desktopCurrent }.Concat(history.Where(x => x.Key != _desktopCurrent.Key)).ToArray();
        history = history.Take(50).ToArray();
        foreach (var item in history)
        {
            var card = new DesktopNotificationCard(item);
            card.OpenRequested += OpenDesktopNotificationApp;
            card.DismissRequested += value => { _desktopInbox.Dismiss(value.Key); if (_desktopCurrent?.Key == value.Key) FinishDesktopPopup(); RefreshDesktopInbox(); };
            card.BlockAppRequested += BlockDesktopNotificationApp;
            _notificationList.Children.Add(card);
        }
        if (history.Length == 0) _notificationList.Children.Add(UtilityText("暂无通知。授权后接收新消息；静默消息保留在这里。", 11));
        RefreshDesktopAccess();
    }
    private void OpenDesktopNotificationApp(DesktopNotification item)
    {
        if (_sessionLocked || !item.CanOpenApp) return;
        if (!NotificationAppLauncher.TryOpen(item.AppId)) _notificationAccessText.Text = "无法打开此应用，请从开始菜单打开。";
        else FinishDesktopPopup();
    }
    private void BlockDesktopNotificationApp(DesktopNotification item)
    {
        if (!item.CanOpenApp) return;
        string current = SettingsRegistry.GetText(_options, "NotificationBlockedApps", "");
        SettingsRegistry.Write(_options, "NotificationBlockedApps", current.Length == 0 ? item.AppId : current + ";" + item.AppId);
        if (_runtimeEnabled) _options.Save(allowRecovery: true);
        if (_desktopCurrent?.AppId == item.AppId) FinishDesktopPopup(); RefreshDesktopInbox();
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
        if (_desktopCurrent != null && !_desktopPolicy.CanPopup(_desktopCurrent, _sessionLocked)) FinishDesktopPopup();
        RefreshDesktopInbox();
    }
    private void ClearDesktopInbox() { _desktopInbox.Clear(); FinishDesktopPopup(); RefreshDesktopInbox(); }
    private void StartNotificationUiTimer()
    {
        if (_notificationUiTimer != null) return;
        _notificationUiTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        _notificationUiTimer.Tick += OnNotificationUiTick; _notificationUiTimer.Start();
    }
    private void OnNotificationUiTick(object? sender, EventArgs e) => TickDesktopNotifications();
    private void StopNotificationTimerIfUnused()
    {
        if (_systemToasts != null || _sessionEventsStarted || _radioMonitoring) return;
        if (_notificationUiTimer != null) { _notificationUiTimer.Stop(); _notificationUiTimer.Tick -= OnNotificationUiTick; _notificationUiTimer = null; }
    }
    private void TickDesktopNotifications()
    {
        if (_exiting || _sessionLocked) return;
        if (ComponentSwitchGestureActive) return;
        var now = DateTimeOffset.Now;
        if (_desktopCurrent != null)
        {
            if (_notificationAutoCard && _pages.ActiveId != IslandPageIds.Notifications) _notificationAutoCard = false;
            if (_notificationAutoCard && IslandBody.IsMouseOver) _desktopExpires = now.AddSeconds(2);
            if (now < _desktopExpires) return;
            FinishDesktopPopup();
        }
        if (_approvalCurrent != null || _layers.IsActive("volume") || _layers.IsActive("battery")
            || (_state == IslandState.Card && (_pages.ActiveId != IslandPageIds.Notifications || !_notificationAutoCard))) return;
        var next = _desktopInbox.TakeNext(_desktopPolicy, _sessionLocked, now);
        if (next == null) return;
        _notificationReturnState = _state == IslandState.Bar ? IslandState.Bar : IslandState.Pill;
        _notificationReturnKey = _activeMainIslandKey;
        _desktopCurrent = next; _desktopExpires = now.AddSeconds(SettingsRegistry.GetInt(_options, "NotificationDisplaySeconds", 8));
        _notificationSummary.SetNotification(next);
        ShowPillNotification(IslandNotification.Seconds(DesktopToastKey, 120) with
        { Title = _notificationSummary.Title.Text, Detail = _notificationSummary.Detail.Text });
        if (next.Kind == DesktopEventKind.Message)
        {
            SetState(IslandState.Card); SwitchCardPage(_pages.IndexOf(IslandPageIds.Notifications), animated: false);
            _notificationAutoCard = true;
        }
        else _notificationAutoPill = true;
        RefreshDesktopInbox();
    }
    private void FinishDesktopPopup()
    {
        bool closePopup = (_notificationAutoCard && _state == IslandState.Card && _pages.ActiveId == IslandPageIds.Notifications)
            || (_notificationAutoPill && _state == IslandState.Pill && _activeMainIslandKey == DesktopToastKey);
        _desktopCurrent = null;
        _notificationAutoCard = false; _notificationAutoPill = false;
        if (_layers.IsRegistered(DesktopToastKey)) _layers.Hide(DesktopToastKey);
        if (closePopup && !_exiting && _approvalCurrent == null)
        {
            if (CanRetainIsland(_notificationReturnKey)) SwitchMainIsland(_notificationReturnKey);
            else if (_activeMainIslandKey == DesktopToastKey)
            {
                // 细条没有活跃岛时也要移除已结束的消息键，保留用户原来的音乐收起状态。
                _activeMainIslandKey = "music"; _layers.PreferredWinnerKey = "music";
            }
            SetState(_layers.AnyPinned || HasVisibleMusicPin ? IslandState.Pill : _notificationReturnState);
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
        if (locked) { CloseComponentSwitcher(); _desktopInbox.DropPending(); FinishDesktopPopup(); }
        if (_notificationRoot != null) _notificationRoot.Visibility = locked ? Visibility.Hidden : Visibility.Visible;
        var item = new DesktopNotification("session-" + DateTimeOffset.UtcNow.UtcTicks, "", "Windows", locked ? "电脑已锁定" : "欢迎回来", locked ? "锁屏期间暂停弹出消息，保护通知内容。" : "电脑已解锁，系统事件监听继续运行。", DateTimeOffset.Now, locked ? DesktopEventKind.Locked : DesktopEventKind.Unlocked);
        _desktopInbox.Add(item, !locked && _desktopPolicy.CanPopup(item, false)); RefreshDesktopInbox();
    }
    private void RecordBatteryDesktopEvent(BatteryStatusInfo info)
    {
        var kind = info.EventType switch
        {
            BatteryPowerEvent.PluggedIn => DesktopEventKind.PowerConnected,
            BatteryPowerEvent.Unplugged => DesktopEventKind.PowerDisconnected,
            BatteryPowerEvent.LowBattery => DesktopEventKind.LowBattery,
            BatteryPowerEvent.CriticalBattery => DesktopEventKind.CriticalBattery,
            _ => DesktopEventKind.System
        };
        if (kind == DesktopEventKind.System) return;
        var title = kind switch { DesktopEventKind.PowerConnected => "已接通电源", DesktopEventKind.PowerDisconnected => "已拔出电源", DesktopEventKind.CriticalBattery => "电池电量严重不足", _ => "电池电量不足" };
        _desktopInbox.Add(new("battery-" + DateTimeOffset.UtcNow.UtcTicks, "", "Windows", title,
            info.Percent >= 0 ? $"当前电量 {info.Percent}%" : "电量未知", DateTimeOffset.Now, kind), false); RefreshDesktopInbox();
    }
    private void RecordVolumeDesktopEvent(VolumeInfo info)
    {
        _desktopInbox.Add(new("event-volume", "", "Windows", info.IsMuted ? "系统音量已静音" : "系统音量已调整", $"当前音量 {info.Percent}%", DateTimeOffset.Now, DesktopEventKind.Volume), false);
        if (_pages.ActiveId == IslandPageIds.Notifications) RefreshDesktopInbox();
    }
}
