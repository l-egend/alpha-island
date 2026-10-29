using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;
using Point = System.Windows.Point;

namespace alpha_island;

public partial class MainWindow
{
    private MediaWindowService? _agentWindows;
    private AgentAppWindow? _agentWindowTarget;
    private string _agentWindowOwner = "";
    private MediaHandoffOverlay? _agentHandoff;
    private bool _agentSourceForeground, _agentHandoffPreparing, _agentHandoffOpening;
    private bool _agentHandoffSourceCovered, _agentHandoffLanding;
    private int _agentHandoffEpoch;

    private string AgentWindowActivityOwner => AgentSession(_agentWindowKey) is { } view
        && (view.Activity != null || _layers.IsPinned(view.Key)) ? view.Owner : "";

    private bool ShouldHideForegroundAgent => MediaWindowHandoffEnabled
        && AgentAppNavigationService.Provider(AgentWindowActivityOwner) != null
        && (_agentSourceForeground || _agentHandoff != null
            && (!_agentHandoffOpening || _agentHandoffSourceCovered) && !_agentHandoffLanding);

    private void SyncAgentWindowTarget()
    {
        // Cache the window the user is actually using, including an assistant whose
        // island is currently a satellite. Keep an in-flight handoff's identity stable.
        if (_agentHandoff == null && !_agentHandoffPreparing)
        {
            var foreground = _agentSessions.Values.FirstOrDefault(view => (view.Activity != null || _layers.IsPinned(view.Key))
                && view.Target != null && _agentNavigation.IsForeground(view.Target) && !NativeMethods.IsIconic(view.Target.Handle));
            if (foreground != null) _agentWindowKey = foreground.Key;
        }
        if (AgentWindowActivityOwner.Length == 0)
            _agentWindowKey = _agentSessions.Values.FirstOrDefault(v => v.Activity != null || _layers.IsPinned(v.Key))?.Key ?? AgentTickerKey;
        string owner = AgentWindowActivityOwner;
        if (!MediaWindowHandoffEnabled || !_agentStatusEnabled || (!AgentWindowSessionBusy && !_layers.IsPinned(_agentWindowKey))
            || AgentAppNavigationService.Provider(owner) == null)
        { StopAgentWindowTracking(); return; }
        if (_agentWindowOwner != owner || _agentWindowTarget == null
            || _agentWindows?.IsTrackedWindowValid(_agentWindowTarget.Handle) != true)
        {
            if (!_agentNavigation.TryResolve(owner, out var target, out _) || target == null || !NativeMethods.IsWindow(target.Handle))
            { StopAgentWindowTracking(); return; }
            TrackAgentWindow(target, owner);
        }
        _agentSourceForeground = _agentWindowTarget != null && _agentNavigation.IsForeground(_agentWindowTarget)
            && !NativeMethods.IsIconic(_agentWindowTarget.Handle);
        if (_agentWindows != null) _agentWindows.AutomaticSnapshotCaptureEnabled = AgentWindowSessionBusy;
    }

    private void TrackAgentWindow(AgentAppWindow target, string owner)
    {
        if (_agentWindowTarget?.Handle != target.Handle || _agentWindowOwner != owner
            || _agentWindows?.IsTrackedWindowValid(target.Handle) == false) CancelAgentHandoff();
        _agentWindowTarget = target; _agentWindowOwner = owner;
        if (_agentWindows == null)
        {
            // Busy foreground assistants cache at most one frame every two seconds.
            // Background/minimized windows do not cause periodic captures.
            _agentWindows = new MediaWindowService(Dispatcher, captureIntervalMilliseconds: 2000, refreshIntervalMilliseconds: 500)
                { RequireSnapshotForTransitions = true };
            _agentWindows.Changed += OnAgentWindowChanged;
            _agentWindows.Minimizing += OnAgentWindowMinimizing;
            _agentWindows.WindowActivityChanged += QueueAgentStatusRefresh;
        }
        _agentWindows.SetTrackedWindow(target);
    }

    private void StopAgentWindowTracking()
    {
        CancelAgentHandoff();
        _agentWindows?.Dispose(); _agentWindows = null;
        _agentWindowTarget = null; _agentWindowOwner = ""; _agentSourceForeground = false;
        _layers.SetPresentationSuppressed(_agentWindowKey, !SettingsRegistry.GetBool(_options, "AgentStatusEnabled", true));
    }

    private void CancelAgentHandoff()
    {
        _agentHandoffEpoch++;
        var overlay = _agentHandoff; _agentHandoff = null;
        _agentHandoffPreparing = false; _agentHandoffOpening = false;
        _agentHandoffSourceCovered = false; _agentHandoffLanding = false;
        overlay?.Close();
        if (_agentWindows != null) _agentWindows.SnapshotCaptureSuspended = false;
        _layers.SetPresentationSuppressed(_agentWindowKey,
            !SettingsRegistry.GetBool(_options, "AgentStatusEnabled", true) || ShouldHideForegroundAgent);
    }

    private void OnAgentWindowChanged(MediaWindowState state)
    {
        if (_exiting || _agentWindowTarget == null || _agentWindowOwner != AgentWindowActivityOwner) return;
        if (_agentHandoff != null && !_agentHandoffOpening && !state.Minimized
            && _agentNavigation.IsForeground(_agentWindowTarget)) CancelAgentHandoff();
        bool foreground = !state.Minimized && _agentNavigation.IsForeground(_agentWindowTarget);
        if (foreground == _agentSourceForeground) return;
        _agentSourceForeground = foreground;
        UpdateAgentStatusUi();
    }

    private void CollapseSuppressedAgentIsland()
    {
        if (!ShouldHideForegroundAgent || _state != IslandState.Pill || _approvalCurrent != null || _clipboardLocked
            || HasActiveNotification || MusicIslandCanShow || _layers.AnyPinned) return;
        SetState(IslandState.Bar, force: true);
    }

    private void RestorePinnedIdleAgentIsland()
    {
        if (GameDoNotDisturbActive || AgentWindowSessionBusy || !_layers.IsPinned(_agentWindowKey) || ShouldHideForegroundAgent
            || _agentWindowTarget == null || _state != IslandState.Bar || _approvalCurrent != null || _clipboardLocked
            || _sessionLocked || _fileDragActive) return;
        SwitchMainIsland(_agentWindowKey); SetState(IslandState.Pill);
    }

    private FrameworkElement AgentOpenSurface()
    {
        if (SideBubble.Visibility == Visibility.Visible && _sideBubbleComponentKey == _agentWindowKey && SideBubble.IsMouseOver)
            return SideBubble;
        if (_satelliteOrbViews.TryGetValue(_agentWindowKey, out var satellite) && satellite.Visibility == Visibility.Visible
            && satellite.IsMouseOver) return satellite;
        return IslandBody;
    }

    private BitmapSource? AgentSurfacePreview(FrameworkElement surface)
    {
        try
        {
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
                context.DrawRectangle(new VisualBrush(surface), null, new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
            var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(surface.ActualWidth)),
                Math.Max(1, (int)Math.Ceiling(surface.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawing); bitmap.Freeze(); return bitmap;
        }
        catch { return null; }
    }

    private Rect AgentIslandScreenRect(bool landing = false)
    {
        FrameworkElement surface = AgentOpenSurface();
        if (!landing && surface.ActualWidth > 0 && surface.ActualHeight > 0)
            return new Rect(surface.PointToScreen(new Point()), surface.PointToScreen(new Point(surface.ActualWidth, surface.ActualHeight)));
        var root = PresentationSource.FromVisual(IslandBody)?.RootVisual as FrameworkElement ?? this;
        double center = root.ActualWidth > 0 ? root.ActualWidth / 2 : Width / 2;
        double width = landing ? IslandMetrics.PillWidth : CurrentPillWidth;
        return new Rect(root.PointToScreen(new Point(center - width / 2, IslandMetrics.PillTop)),
            root.PointToScreen(new Point(center + width / 2, IslandMetrics.PillTop + IslandMetrics.PillHeight)));
    }

    private void PrepareAgentIslandLanding(int epoch)
    {
        if (_exiting || epoch != _agentHandoffEpoch || _agentWindowOwner != AgentWindowActivityOwner) return;
        _agentHandoffLanding = true; _agentSourceForeground = false;
        SwitchMainIsland(_agentWindowKey);
        UpdateAgentStatusUi();
        if (_state != IslandState.Pill) SetState(IslandState.Pill, force: true);
        if (_state == IslandState.Pill && _activeMainIslandKey == _agentWindowKey)
        {
            _widthOnlyMorph = false; _animator?.SetImmediate(GeometryFor(_state));
            _reveal = 1; _layers.EndMorph(); ApplyReveal(); CancelSiriOrb(); SyncNotificationLayers();
        }
        IslandBody.UpdateLayout();
    }

    private async void OnAgentWindowMinimizing(MediaWindowState state)
    {
        if (_exiting || GameDoNotDisturbActive || _agentHandoff != null || _agentHandoffPreparing || !MediaWindowHandoffEnabled
            || _agentWindowOwner != AgentWindowActivityOwner || _agentWindowTarget?.Handle != state.Handle
            || _agentWindows?.IsTrackedWindowValid(state.Handle) != true
            || (!AgentWindowSessionBusy && !_layers.IsPinned(_agentWindowKey)) || _approvalCurrent != null || _sessionLocked) return;
        _agentSourceForeground = false;
        if (MotionPolicy.InstantMorph || MotionPolicy.PowerSave || state.Bounds.IsEmpty
            || _agentWindows?.TransitionsSuppressed != true || _agentWindows.Snapshot == null)
        { UpdateAgentStatusUi(); return; }
        int epoch = ++_agentHandoffEpoch;
        var service = _agentWindows;
        MediaHandoffOverlay? overlay = null;
        try
        {
            service.SnapshotCaptureSuspended = true;
            overlay = new MediaHandoffOverlay(state.Bounds, AgentIslandScreenRect(landing: true), service.Snapshot, null,
                _agentWindowTarget.Process, opening: false, reveal: () => PrepareAgentIslandLanding(epoch));
            _agentHandoff = overlay; _agentHandoffOpening = false; _agentHandoffLanding = false;
            UpdateAgentStatusUi();
            await overlay.PlayAsync();
        }
        catch (Exception ex) { IslandLog.Warn("AgentWindow", "助手吸入动画降级: " + ex.GetType().Name); }
        finally
        {
            overlay?.Close();
            if (epoch == _agentHandoffEpoch)
            {
                _agentHandoff = null; _agentHandoffLanding = false;
                service.SnapshotCaptureSuspended = false;
                UpdateAgentStatusUi();
            }
        }
    }

    private async Task OpenAgentWindowAsync(AgentAppWindow target)
    {
        if (_exiting || _agentHandoff != null || _agentHandoffPreparing) return;
        var navigation = _agentNavigation;
        if (navigation.IsForeground(target) && !NativeMethods.IsIconic(target.Handle)) { _agentSourceForeground = true; UpdateAgentStatusUi(); return; }
        if (MotionPolicy.InstantMorph || MotionPolicy.PowerSave || !MediaWindowHandoffEnabled || !NativeMethods.IsWindow(target.Handle))
        { navigation.Activate(target, out _); UpdateAgentStatusUi(); return; }
        TrackAgentWindow(target, AgentWindowActivityOwner);
        var service = _agentWindows;
        if (service == null || service.Current.Bounds.IsEmpty)
        { navigation.Activate(target, out _); UpdateAgentStatusUi(); return; }
        int epoch = ++_agentHandoffEpoch;
        _agentHandoffPreparing = true;
        bool activated = false;
        MediaHandoffOverlay? overlay = null;
        try
        {
            BitmapSource? snapshot = service.Snapshot ?? await service.CaptureWindowSnapshotAsync();
            if (_exiting || epoch != _agentHandoffEpoch || !service.IsTrackedWindowValid(target.Handle)) return;
            var source = AgentOpenSurface();
            var from = AgentIslandScreenRect();
            var destination = service.Current.Bounds;
            if (snapshot == null || !service.TransitionsSuppressed)
            {
                // Animate the actual island into the destination even before a real window
                // frame is cached. Keep its aspect ratio instead of inventing app contents.
                snapshot = null; service.AllowNativeTransitions();
                double width = Math.Min(Math.Max(from.Width * 1.75, 180), Math.Max(from.Width, destination.Width * .8));
                double height = from.Height * width / Math.Max(1, from.Width);
                destination = new Rect(destination.Left + (destination.Width - width) / 2,
                    destination.Top + (destination.Height - height) / 2, width, height);
            }
            overlay = new MediaHandoffOverlay(from, destination, snapshot, null, target.Process,
                opening: true, islandPreview: AgentSurfacePreview(source), fallbackSubtitle: L.T("AI 助手"),
                reveal: () => { if (!_exiting && epoch == _agentHandoffEpoch && service.IsTrackedWindowValid(target.Handle))
                    activated = navigation.Activate(target, out _); },
                destinationReady: () => service.IsRestoredVisible(target.Handle),
                sourceCovered: () =>
                {
                    if (epoch != _agentHandoffEpoch) return;
                    _agentHandoffSourceCovered = true; UpdateAgentStatusUi();
                });
            _agentHandoff = overlay; _agentHandoffPreparing = false; _agentHandoffOpening = true;
            _agentHandoffSourceCovered = false; _agentHandoffLanding = false;
            service.SnapshotCaptureSuspended = true;
            bool completed = await overlay.PlayAsync();
            if (!_exiting && epoch == _agentHandoffEpoch && service.IsTrackedWindowValid(target.Handle)
                && (!completed || !activated)) navigation.Activate(target, out _);
        }
        catch (Exception ex)
        {
            IslandLog.Warn("AgentWindow", "助手出岛动画降级: " + ex.GetType().Name);
            if (!_exiting && epoch == _agentHandoffEpoch && service.IsTrackedWindowValid(target.Handle)) navigation.Activate(target, out _);
        }
        finally
        {
            overlay?.Close();
            if (epoch == _agentHandoffEpoch)
            {
                _agentHandoff = null; _agentHandoffPreparing = false; _agentHandoffOpening = false;
                _agentHandoffSourceCovered = false; _agentHandoffLanding = false;
                service.SnapshotCaptureSuspended = false;
                _agentSourceForeground = navigation.IsForeground(target) && !NativeMethods.IsIconic(target.Handle); UpdateAgentStatusUi();
            }
        }
    }
}
