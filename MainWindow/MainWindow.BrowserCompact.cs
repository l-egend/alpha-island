using System.Threading.Tasks;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Services;

namespace alpha_island;

public partial class MainWindow
{
    private BrowserWindowMonitor? _browserWindowMonitor;
    private bool _browserCompactActive;
    private bool _browserManualReveal;
    private bool _browserRestoreExpanded;
    private bool BrowserCompactBlocking => _browserCompactActive && !_browserManualReveal
        && !GameDoNotDisturbActive && _approvalCurrent == null && !VoiceIslandVisible && !_fileDragActive;
    private bool BrowserVolumeFeedbackActive => BrowserCompactBlocking && VolumeNotifyActive && _activeMainIslandKey == "volume"
        && Environment.TickCount64 - _lastVolumeFeedbackAt < Math.Max(0, _options.VolumeNotifySeconds) * 1000;

    private void StartBrowserWindowMonitor()
    {
        if (_browserWindowMonitor != null) return;
        var monitor = new BrowserWindowMonitor(Dispatcher);
        _browserWindowMonitor = monitor;
        monitor.Changed += active =>
        {
            if (!_exiting && ReferenceEquals(_browserWindowMonitor, monitor)) ApplyBrowserCompact(active);
        };
        _ = Task.Run(() =>
        {
            monitor.Start();
            if (!monitor.EventsRunning && !Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (!_exiting && ReferenceEquals(_browserWindowMonitor, monitor))
                        _ticker.Register("browser-window-fallback", TimeSpan.FromSeconds(2), monitor.RequestRefresh);
                }));
        });
    }

    private void StopBrowserWindowMonitor()
    {
        var monitor = _browserWindowMonitor; _browserWindowMonitor = null;
        _ticker?.Unregister("browser-window-fallback"); monitor?.Dispose();
        if (!_exiting) ApplyBrowserCompact(false);
    }

    private void ApplyBrowserCompact(bool active)
    {
        active &= SettingsRegistry.GetBool(_options, "BrowserAutoCompactEnabled", true);
        if (_exiting || _browserCompactActive == active) return;
        if (active) _browserRestoreExpanded = _state != IslandState.Bar;
        _browserCompactActive = active; _browserManualReveal = false; _hoverOutTicks = 0;
        if (BrowserCompactBlocking && !BrowserVolumeFeedbackActive)
        {
            CloseComponentSwitcher(); HidePinBubble(immediate: true);
            SetState(IslandState.Bar);
        }
        else if (!active && !GameDoNotDisturbActive && !_sessionLocked && _state == IslandState.Bar)
        {
            _layers.ExpireAllStale();
            if (_pinnedCardPage is { } page && _pages.IndexOf(page) >= 0)
                SetState(IslandState.Card, preferredPage: page);
            else if (HasVisibleMusicPin || _layers.AnyPinned || HasActiveNotification
                || _browserRestoreExpanded && MusicIslandCanShow)
                SetState(IslandState.Pill);
        }
        SyncNotificationLayers();
    }

    private void RevealBrowserCompactIsland()
    {
        if (!_browserCompactActive || GameDoNotDisturbActive) return;
        _browserManualReveal = true; _hoverOutTicks = 0;
        string page = LastOpenedCardPage();
        if (page == IslandPageIds.Music && !MusicIslandCanShow) page = IslandPageIds.System;
        SetState(IslandState.Card, preferredPage: page);
    }

    private bool HandleBrowserCompactHover(bool inside)
    {
        if (!_browserCompactActive || GameDoNotDisturbActive || _approvalCurrent != null || VoiceIslandVisible) return false;
        if (BrowserVolumeFeedbackActive) { _hoverOutTicks = 0; return true; }
        if (_browserManualReveal && inside) { _hoverOutTicks = 0; return true; }
        if (_browserManualReveal && ++_hoverOutTicks < HoverCollapseTicks) return true;
        _browserManualReveal = false;
        if (BrowserCompactBlocking && _state != IslandState.Bar) SetState(IslandState.Bar);
        return true;
    }
}
