using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;

namespace alpha_island;

public partial class MainWindow
{
    private const string GameQuietNoticeKey = "gamequiet";
    private GameActivityMonitor? _gameActivity;
    private DesktopNotificationPill _gameQuietNotice = null!;
    private DispatcherTimer? _gameQuietNoticeTimer;
    private bool _gameQuietActive;
    private long _gameActivityRevision;
    private string _gameQuietName = "";
    private string _gameQuietReturnKey = "music";
    private bool GameDoNotDisturbActive => _gameQuietActive && SettingsRegistry.GetBool(_options, "NotificationGameQuietEnabled", true);
    private bool NotificationQuietEffective => GameDoNotDisturbActive || SettingsRegistry.GetBool(_options, "NotificationQuietMode", false);

    private bool GameQuietAllowsNotification(string key) => !GameDoNotDisturbActive
        || key == GameQuietNoticeKey || key == "volume" && UserVolumeFeedbackActive;

    private void RegisterGameQuietLayer()
    {
        _gameQuietNotice = new DesktopNotificationPill();
        ((System.Windows.Controls.Panel)NetSpeedNotifyLayer.Parent).Children.Add(_gameQuietNotice);
        _layers.Register(new IslandLayerSpec(GameQuietNoticeKey, _gameQuietNotice, Priority: 86));
        // A separate rendering gate preserves task claims, approval decisions and every manual pin.
        _layers.Hub.PresentationAllowed = key => GameQuietAllowsNotification(key) || _layers.IsPinned(key);
    }
    private void StartGameActivity()
    {
        if (_gameActivity != null) return;
        var monitor = new GameActivityMonitor(SettingsRegistry.GetText(_options, "NotificationGameProcesses", ""), dispatcher: Dispatcher);
        _gameActivity = monitor; _gameActivityRevision = 0;
        monitor.Changed += state =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_exiting || !ReferenceEquals(_gameActivity, monitor) || state.Revision <= _gameActivityRevision) return;
                _gameActivityRevision = state.Revision; ApplyGameActivity(state);
            }));
        };
        _ = Task.Run(monitor.Start);
    }
    private void StopGameActivity()
    {
        var monitor = _gameActivity; _gameActivity = null;
        if (monitor != null) _ = Task.Run(monitor.Dispose);
        _gameQuietNoticeTimer?.Stop();
        if (!_exiting) ApplyGameActivity(GameActivityState.Inactive);
    }
    private void OnGameActivityOptionsChanged() => _gameActivity?.SetCustomProcesses(
        SettingsRegistry.GetText(_options, "NotificationGameProcesses", ""));

    private void ApplyGameActivity(GameActivityState state)
    {
        if (_exiting) return;
        _productivity?.SetActivity(ProductivityActivity.Game, state.Active);
        bool active = state.Active && SettingsRegistry.GetBool(_options, "NotificationGameQuietEnabled", true);
        _gameQuietName = active ? state.Name : "";
        if (_gameQuietActive == active) { RefreshDesktopAccess(); return; }
        _gameQuietActive = active;
        _desktopInbox.DropPending(); // Never burst out with messages collected while gaming.
        FinishDesktopPopup();
        _gameQuietNoticeTimer?.Stop();
        if (active)
        {
            _gameQuietReturnKey = _activeMainIslandKey;
            CancelMediaHandoff(); CancelAgentHandoff();
            _upNextTimer?.Stop(); _upNextShowing = false; _upNextUntil = DateTime.MinValue;
            SyncUpNextContent();
            var notice = new DesktopNotification("game-quiet-" + DateTimeOffset.UtcNow.UtcTicks, "", "Alpha Island",
                "检测到游戏开启", "免打扰模式已开启 · " + state.Name, DateTimeOffset.Now, DesktopEventKind.System);
            _desktopInbox.Add(notice, false);
            _gameQuietNotice.SetNotification(notice);
            _gameQuietNotice.Icon.Shape = IconShape.Auto; _gameQuietNotice.Icon.Text = "\uE7FC";
            if (!_sessionLocked)
            {
                if (_greetingShowing || GreetingActive) DismissGreeting();
                ShowPillNotification(IslandNotification.Seconds(GameQuietNoticeKey, 3, deferredInCard: false)
                    with { Glyph = "\uE7FC", Title = notice.Title, Detail = notice.Body });
                _gameQuietNoticeTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                    { Interval = TimeSpan.FromSeconds(3) };
                _gameQuietNoticeTimer.Tick -= OnGameQuietNoticeExpired;
                _gameQuietNoticeTimer.Tick += OnGameQuietNoticeExpired;
                _gameQuietNoticeTimer.Start();
            }
            IslandLog.Info("GameQuiet", "自动免打扰开启；游戏=" + state.Name + "；来源=" + state.Source);
        }
        else
        {
            _layers.Hide(GameQuietNoticeKey);
            _layers.ExpireAllStale();
            UpdateAgentStatusUi();
            RestoreAfterGameQuietNotice();
            if (_approvalCurrent != null) PresentCurrentApproval();
            else if (_state == IslandState.Bar && (KeepPlayingMusicIslandVisible || ActiveAgentIslandKey != null))
            {
                string key = ActiveAgentIslandKey ?? "music";
                _activeMainIslandKey = key; _layers.PreferredWinnerKey = key; SetState(IslandState.Pill);
            }
            SyncPausedMediaVisibility();
            _desktopInbox.Add(new("game-quiet-ended-" + DateTimeOffset.UtcNow.UtcTicks, "", "Alpha Island",
                "游戏免打扰已结束", "已恢复原来的通知设置", DateTimeOffset.Now, DesktopEventKind.System), false);
            IslandLog.Info("GameQuiet", "自动免打扰结束；手动静默=" + SettingsRegistry.GetBool(_options, "NotificationQuietMode", false));
        }
        OnDesktopNotificationOptionsChanged();
        SyncNotificationLayers();
    }
    private void OnGameQuietNoticeExpired(object? sender, EventArgs e)
    {
        _gameQuietNoticeTimer?.Stop(); _layers.Hide(GameQuietNoticeKey);
        RestoreAfterGameQuietNotice();
    }
    private void RestoreAfterGameQuietNotice()
    {
        if (_activeMainIslandKey != GameQuietNoticeKey) return; // A user-selected card/pin keeps its place.
        string key = CanRetainIsland(_gameQuietReturnKey) ? _gameQuietReturnKey
            : _layers.ActiveKeys.FirstOrDefault(k => k != GameQuietNoticeKey) ?? "music";
        _activeMainIslandKey = key; _layers.PreferredWinnerKey = key;
        bool keep = _layers.AnyPinned || HasVisibleMusicPin || (!GameDoNotDisturbActive
            && (HasActiveNotification || KeepPlayingMusicIslandVisible));
        SetState(keep ? IslandState.Pill : IslandState.Bar, force: true);
    }
}
