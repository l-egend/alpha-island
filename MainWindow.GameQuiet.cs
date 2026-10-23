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
    private bool _gameQuietActive;
    private DispatcherTimer? _gameQuietIntroTimer;
    private DispatcherTimer? _gameQuietCollapseTimer;
    private double _gameQuietCollapseWidth, _gameQuietCollapseHeight;
    private long _gameQuietCollapseDeadline;
    private long _gameQuietIntroUntil;
    private string _gameQuietPreviousIslandKey = "music";
    private long _gameActivityRevision;
    private string _gameQuietName = "";
    private bool GameDoNotDisturbActive => _gameQuietActive && SettingsRegistry.GetBool(_options, "NotificationGameQuietEnabled", true);
    private bool NotificationQuietEffective => GameDoNotDisturbActive || SettingsRegistry.GetBool(_options, "NotificationQuietMode", false);

    private bool GameQuietIntroActive => GameDoNotDisturbActive && !_sessionLocked && _gameQuietIntroUntil != 0;
    private bool GameQuietAllowsNotification(string key) => !GameDoNotDisturbActive || GameQuietIntroActive && key == GameQuietNoticeKey;

    private void BeginGameQuietIntro()
    {
        _gameQuietPreviousIslandKey = _activeMainIslandKey;
        if (_sessionLocked) { SetState(IslandState.Bar, force: true); return; }
        _gameQuietIntroUntil = Environment.TickCount64 + 2000;
        _activeMainIslandKey = GameQuietNoticeKey;
        _layers.PreferredWinnerKey = GameQuietNoticeKey;
        _layers.Request(IslandNotification.While(GameQuietNoticeKey, () => GameQuietIntroActive, deferredInCard: false), requestSync: false);
        SetState(IslandState.Pill, force: true);
        if (_gameQuietIntroTimer == null)
        {
            // A game may suspend rendering; expiry must not depend on a render/animation callback.
            _gameQuietIntroTimer = new DispatcherTimer(DispatcherPriority.Send, Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
            _gameQuietIntroTimer.Tick += (_, _) => FinishGameQuietIntro();
        }
        _gameQuietIntroTimer.Start();
    }

    private void CancelGameQuietIntro()
    {
        _gameQuietIntroTimer?.Stop();
        _gameQuietCollapseTimer?.Stop();
        _gameQuietIntroUntil = 0;
        _layers.Hide(GameQuietNoticeKey);
    }

    private void FinishGameQuietIntro()
    {
        if (_gameQuietIntroUntil == 0) return;
        CancelGameQuietIntro();
        if (_exiting || !GameDoNotDisturbActive) return;
        CollapseToBar(force: true);
        var geometry = _animator?.CurrentGeometry();
        _gameQuietCollapseWidth = geometry?.Width ?? 0; _gameQuietCollapseHeight = geometry?.Height ?? 0;
        _gameQuietCollapseDeadline = Environment.TickCount64 + 4000;
        // Interaction is locked by _state/GameDoNotDisturbActive immediately;
        // the contour can still use the normal collapse spring. A fullscreen
        // game may stop render frames, so settle independently after its budget.
        _gameQuietCollapseTimer ??= new DispatcherTimer(DispatcherPriority.Send, Dispatcher)
        { Interval = IslandMetrics.CollapseDuration.TimeSpan + TimeSpan.FromMilliseconds(350) };
        _gameQuietCollapseTimer.Tick -= SettleGameQuietCollapse;
        _gameQuietCollapseTimer.Tick += SettleGameQuietCollapse;
        _gameQuietCollapseTimer.Start();
        SyncNotificationLayers();
    }

    private void SettleGameQuietCollapse(object? sender, EventArgs args)
    {
        _gameQuietCollapseTimer?.Stop();
        if (_exiting || !GameDoNotDisturbActive || _state != IslandState.Bar || _animator is not { IsRunning: true } animator) return;
        var geometry = animator.CurrentGeometry();
        bool progressed = Math.Abs(geometry.Width - _gameQuietCollapseWidth) > .01 || Math.Abs(geometry.Height - _gameQuietCollapseHeight) > .01;
        _gameQuietCollapseWidth = geometry.Width; _gameQuietCollapseHeight = geometry.Height;
        if (progressed && Environment.TickCount64 < _gameQuietCollapseDeadline) _gameQuietCollapseTimer?.Start();
        else SetState(IslandState.Bar, force: true);
    }

    private void RegisterGameQuietLayer()
    {
        _gameQuietNotice = new DesktopNotificationPill();
        ((System.Windows.Controls.Panel)NetSpeedNotifyLayer.Parent).Children.Add(_gameQuietNotice);
        _layers.Register(new IslandLayerSpec(GameQuietNoticeKey, _gameQuietNotice, Priority: 86));
        // A separate rendering gate preserves task claims, approval decisions and every manual pin.
        _layers.Hub.PresentationAllowed = GameQuietAllowsNotification;
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
        if (!_exiting) ApplyGameActivity(GameActivityState.Inactive);
    }
    private void OnGameActivityOptionsChanged()
    {
        _gameActivity?.SetCustomProcesses(SettingsRegistry.GetText(_options, "NotificationGameProcesses", ""));
        if (_gameQuietActive && !SettingsRegistry.GetBool(_options, "NotificationGameQuietEnabled", true))
            ApplyGameActivity(GameActivityState.Inactive);
    }

    private void ApplyGameActivity(GameActivityState state)
    {
        if (_exiting) return;
        _productivity?.SetActivity(ProductivityActivity.Game, state.Active);
        _desktopAssistant?.NotifyActivityChanged();
        bool active = state.Active && SettingsRegistry.GetBool(_options, "NotificationGameQuietEnabled", true);
        _gameQuietName = active ? state.Name : "";
        if (_gameQuietActive == active) { RefreshDesktopAccess(); _assistantReminders?.TickNow(); return; }
        _gameQuietActive = active;
        SyncVolumeFlyoutTakeover();
        alpha_island.Animation.AgentAvatarMotion.SetSuspended(active);
        _desktopInbox.DropPending(); // Never burst out with messages collected while gaming.
        FinishDesktopPopup();
        if (active)
        {
            if (_voiceAssistant?.Visible == true) _ = _voiceAssistant.CancelAsync();
            CancelMediaHandoff(); CancelAgentHandoff();
            CloseComponentSwitcher(); EndVolumeDrag(); EndIncomingFileDrag(restore: false);
            _isSwiping = false; _dragStartedInsideIsland = false;
            _mediaOpenPointer = null; _clipboardOpenPointer = null;
            if (IslandBody.IsMouseCaptured) IslandBody.ReleaseMouseCapture();
            _clipboard?.DismissCopiedPending();
            OnClipboardCopiedDismissed(null, EventArgs.Empty);
            HidePinBubble(immediate: true);
            // A game can suspend rendering before Completed callbacks run. Settle
            // page clocks and the contour now instead of retaining an in-flight UI.
            SwitchCardPage(_pages.ActiveIndex, animated: false, updateGeometry: false);
            _layers.CancelPrimaryAnimation();
            _upNextTimer?.Stop(); _upNextShowing = false; _upNextUntil = DateTime.MinValue;
            SyncUpNextContent();
            var notice = new DesktopNotification("game-quiet-" + DateTimeOffset.UtcNow.UtcTicks, "", "Alpha Island",
                "检测到游戏开启", "免打扰模式已开启 · 即将收起 · " + state.Name, DateTimeOffset.Now, DesktopEventKind.System);
            _desktopInbox.Add(notice, false);
            _gameQuietNotice.SetNotification(notice);
            _gameQuietNotice.Icon.Shape = IconShape.Auto; _gameQuietNotice.Icon.Text = "\uE7FC";
            if (_greetingShowing || GreetingActive) DismissGreeting();
            BeginGameQuietIntro();
            HideAllSatellites(immediate: true);
            IslandLog.Info("GameQuiet", "自动免打扰开启；游戏=" + state.Name + "；来源=" + state.Source);
        }
        else
        {
            CancelGameQuietIntro();
            if (_activeMainIslandKey == GameQuietNoticeKey)
                _activeMainIslandKey = _gameQuietPreviousIslandKey;
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
        _assistantReminders?.TickNow();
        SyncNotificationLayers();
    }
    private void RestoreAfterGameQuietNotice()
    {
        if (GameDoNotDisturbActive) { SetState(IslandState.Bar); return; }
        string key = CanRetainIsland(_activeMainIslandKey) ? _activeMainIslandKey
            : _layers.ActiveKeys.FirstOrDefault(k => k != GameQuietNoticeKey) ?? "music";
        _activeMainIslandKey = key; _layers.PreferredWinnerKey = key;
        bool keep = _layers.AnyPinned || HasVisibleMusicPin || HasActiveNotification || KeepPlayingMusicIslandVisible;
        SetState(keep ? IslandState.Pill : IslandState.Bar, force: true);
    }
}
