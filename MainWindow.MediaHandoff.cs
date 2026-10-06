using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;
using Point = System.Windows.Point;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace alpha_island;

public partial class MainWindow
{
    private MediaWindowService? _mediaWindows;
    private MediaHandoffOverlay? _mediaHandoff;
    private bool _mediaSourceForeground, _mediaHandoffInFlight, _mediaOpening, _mediaHandoffLanding, _mediaHandoffSourceCovered;
    private int _mediaHandoffEpoch;
    private IntPtr _mediaHandoffWindow;
    private Point? _mediaOpenPointer;
    private bool MediaWindowHandoffEnabled => SettingsRegistry.GetBool(_options, "MediaWindowHandoffEnabled", true);
    private bool ShouldHideForegroundMedia => MediaWindowHandoffEnabled && (_mediaSourceForeground
        || (_mediaHandoffInFlight && (!_mediaOpening || _mediaHandoffSourceCovered) && !_mediaHandoffLanding));

    private void SyncMediaWindowOptions()
    {
        if (!MediaWindowHandoffEnabled)
        {
            StopMediaWindowHandoff(); SyncPausedMediaVisibility(); return;
        }
        if (_runtimeEnabled && _mediaWindows == null)
        {
            _mediaWindows = new MediaWindowService(Dispatcher);
            _mediaWindows.Changed += OnMediaWindowChanged;
            _mediaWindows.Minimizing += OnMediaWindowMinimizing;
            _mediaWindows.SetSource(_smtc?.Current?.SourceApp);
        }
        if (MotionPolicy.InstantMorph || MotionPolicy.PowerSave) CancelMediaHandoff();
    }

    private void StopMediaWindowHandoff()
    {
        CancelMediaHandoff();
        _mediaWindows?.Dispose(); _mediaWindows = null;
        _mediaSourceForeground = false; _mediaOpenPointer = null;
    }

    private void CancelMediaHandoff()
    {
        _mediaHandoffEpoch++;
        var overlay = _mediaHandoff; _mediaHandoff = null;
        overlay?.Close(); _mediaHandoffInFlight = false; _mediaOpening = false; _mediaHandoffLanding = false; _mediaHandoffSourceCovered = false;
        if (_mediaWindows != null) _mediaWindows.SnapshotCaptureSuspended = false;
    }

    private void OnMediaWindowChanged(MediaWindowState state)
    {
        if (_exiting) return;
        bool wasForeground = _mediaSourceForeground;
        _mediaSourceForeground = state.Foreground && MusicIslandHasMedia;
        if (_mediaHandoff != null && !_mediaOpening && state.Foreground && !state.Minimized) CancelMediaHandoff();
        if (_mediaHandoff != null && (state.Kind != MediaWindowKind.DesktopPlayer || state.Handle != _mediaHandoffWindow))
        { CancelMediaHandoff(); SyncForegroundMediaVisibility(); }
        if (wasForeground == _mediaSourceForeground) return;
        SyncForegroundMediaVisibility();
        if (wasForeground && !_mediaSourceForeground && !_mediaHandoffInFlight && MusicIslandCanShow)
            ShowReturnedMediaIsland();
    }

    private void SyncForegroundMediaVisibility()
    {
        if (ShouldHideForegroundMedia)
        {
            _upNextTimer?.Stop(); _upNextShowing = false; _upNextUntil = DateTime.MinValue;
            SyncUpNextContent(); HideMusicOrb(immediate: true);
            // Keep the user's media pin; foreground suppression is temporary.
            SyncNotificationLayers();
            if (_state == IslandState.Card && _pages.ActiveId == IslandPageIds.Music)
                SetState(HasActiveNotification || _layers.AnyPinned ? IslandState.Pill : IslandState.Bar, force: true);
            else if (_state == IslandState.Pill && !HasActiveNotification && !_layers.AnyPinned && !_clipboardLocked
                && !IsIslandActive(_activeMainIslandKey)) SetState(IslandState.Bar, force: true);
        }
        else SyncPausedMediaVisibility();
    }

    private void ShowReturnedMediaIsland()
    {
        if (!MusicIslandCanShow || _approvalCurrent != null || _clipboardLocked || _sessionLocked) return;
        _musicIslandDismissed = false;
        if (_state == IslandState.Bar)
        {
            SwitchMainIsland("music"); SetState(IslandState.Pill);
        }
        else SyncNotificationLayers();
    }

    private void PrepareMediaIslandLanding(int epoch)
    {
        if (epoch != _mediaHandoffEpoch || _exiting) return;
        _mediaHandoffLanding = true;
        _mediaSourceForeground = _mediaWindows?.ForegroundNow == true;
        SyncForegroundMediaVisibility(); ShowReturnedMediaIsland();
        if (_approvalCurrent == null && !_clipboardLocked
            && (_state == IslandState.Bar || (_state == IslandState.Pill && _activeMainIslandKey == "music")))
        {
            // The proxy already performed the expansion. Place the live island behind
            // it at its final size instead of starting a second spring from the bar.
            _widthOnlyMorph = false;
            _animator?.SetImmediate(GeometryFor(_state));
            _reveal = 1; _layers.EndMorph(); ApplyReveal(); CancelSiriOrb();
            SyncNotificationLayers();
        }
        IslandBody.UpdateLayout();
    }

    private Rect MediaIslandScreenRect(bool landing = false)
    {
        if (!landing && IslandBody.ActualWidth > 0 && IslandBody.ActualHeight > 0)
            return new Rect(IslandBody.PointToScreen(new Point()), IslandBody.PointToScreen(new Point(IslandBody.ActualWidth, IslandBody.ActualHeight)));
        bool hiddenAfter = ShouldHidePausedMedia && !HasActiveNotification && !_layers.AnyPinned;
        double width = hiddenAfter ? IslandMetrics.BarWidth : CurrentPillWidth;
        double height = hiddenAfter ? IslandMetrics.BarHeight : IslandMetrics.PillHeight;
        double top = hiddenAfter ? IslandMetrics.BarTop : IslandMetrics.PillTop;
        var root = PresentationSource.FromVisual(IslandBody)?.RootVisual as FrameworkElement ?? this;
        double center = root.ActualWidth > 0 ? root.ActualWidth / 2 : Width / 2;
        return new Rect(root.PointToScreen(new Point(center - width / 2, top)),
            root.PointToScreen(new Point(center + width / 2, top + height)));
    }

    private async void OnMediaWindowMinimizing(MediaWindowState state)
    {
        if (_exiting || _mediaHandoffInFlight || !MediaWindowHandoffEnabled || state.Kind != MediaWindowKind.DesktopPlayer
            || !MusicIslandHasMedia || _approvalCurrent != null || _sessionLocked) return;
        _mediaSourceForeground = false;
        if (MotionPolicy.InstantMorph || MotionPolicy.PowerSave || state.Bounds.IsEmpty || _mediaWindows?.TransitionsSuppressed != true)
        { SyncForegroundMediaVisibility(); ShowReturnedMediaIsland(); return; }
        int epoch = ++_mediaHandoffEpoch;
        _mediaHandoffInFlight = true; _mediaOpening = false; _mediaHandoffLanding = false;
        if (_mediaWindows != null) _mediaWindows.SnapshotCaptureSuspended = true;
        var info = _smtc?.Current;
        MediaHandoffOverlay? overlay = null;
        try
        {
            overlay = new MediaHandoffOverlay(state.Bounds, MediaIslandScreenRect(landing: true),
                _mediaWindows?.Snapshot, info?.Artwork, info?.DisplayTitle ?? "音乐播放器", opening: false,
                reveal: () => PrepareMediaIslandLanding(epoch));
            _mediaHandoff = overlay; _mediaHandoffWindow = state.Handle;
            SyncForegroundMediaVisibility();
            IslandLog.Debug("MediaWindow", $"吸入动画开始（{MediaHandoffOverlay.DurationMilliseconds:0}ms，曲线=二次曲线，暂停隐藏={ShouldHidePausedMedia}，置顶层={_layers.AnyPinned}，通知={HasActiveNotification}）");
            await overlay.PlayAsync();
            if (epoch != _mediaHandoffEpoch) return;
            _mediaSourceForeground = _mediaWindows?.ForegroundNow == true;
        }
        catch (Exception ex) { IslandLog.Warn("MediaWindow", "收回动画失败: " + ex.GetType().Name); }
        finally
        {
            if (epoch == _mediaHandoffEpoch)
            {
                _mediaHandoff = null; _mediaHandoffInFlight = false; _mediaHandoffLanding = false;
                SyncForegroundMediaVisibility(); ShowReturnedMediaIsland();
            }
            overlay?.Close();
            if (epoch == _mediaHandoffEpoch && _mediaWindows != null) _mediaWindows.SnapshotCaptureSuspended = false;
        }
    }

    private bool IsMediaOpenSurface(DependencyObject? source)
    {
        if (_state != IslandState.Card || _pages.ActiveId != IslandPageIds.Music || _cardAnimating || !MediaWindowHandoffEnabled
            || _mediaWindows?.Current is not { Kind: MediaWindowKind.DesktopPlayer, Handle: var handle } || handle == IntPtr.Zero) return false;
        if (IsDescendantOf(source, ProgressTrack) || IsDescendantOf(source, ComponentHandle)) return false;
        for (var node = source; node is Visual; node = VisualTreeHelper.GetParent(node))
            if (node is ButtonBase || node is System.Windows.Controls.Slider) return false;
        return IsDescendantOf(source, IslandBody);
    }

    private BitmapSource? MediaCardPreview()
    {
        try
        {
            var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(IslandBody.ActualWidth)),
                Math.Max(1, (int)Math.Ceiling(IslandBody.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(IslandBody); bitmap.Freeze(); return bitmap;
        }
        catch { return null; }
    }

    private async Task OpenMediaWindowAsync()
    {
        if (_mediaHandoffInFlight || _mediaWindows?.Current is not { Kind: MediaWindowKind.DesktopPlayer } state
            || state.Handle == IntPtr.Zero) return;
        var service = _mediaWindows;
        if (MotionPolicy.InstantMorph || MotionPolicy.PowerSave || state.Bounds.IsEmpty || !service.TransitionsSuppressed)
        { service.Restore(state.Handle); return; }
        int epoch = ++_mediaHandoffEpoch;
        var info = _smtc?.Current;
        bool restored = false;
        var overlay = new MediaHandoffOverlay(MediaIslandScreenRect(), state.Bounds, service.Snapshot, info?.Artwork,
            info?.DisplayTitle ?? "音乐播放器", opening: true, islandPreview: MediaCardPreview(),
            reveal: () => { if (epoch == _mediaHandoffEpoch) restored = service.Restore(state.Handle); },
            destinationReady: () => service.IsRestoredVisible(state.Handle),
            sourceCovered: () =>
            {
                if (epoch != _mediaHandoffEpoch) return;
                _mediaHandoffSourceCovered = true;
                SyncForegroundMediaVisibility();
            });
        _mediaHandoff = overlay; _mediaHandoffWindow = state.Handle; _mediaHandoffInFlight = true; _mediaOpening = true; _mediaHandoffLanding = false;
        _mediaHandoffSourceCovered = false;
        service.SnapshotCaptureSuspended = true;
        try
        {
            bool completed = await overlay.PlayAsync();
            if (epoch == _mediaHandoffEpoch && (!completed || !restored)) service.Restore(state.Handle);
        }
        catch (Exception ex)
        {
            IslandLog.Warn("MediaWindow", "打开动画降级: " + ex.GetType().Name);
            if (epoch == _mediaHandoffEpoch) service.Restore(state.Handle);
        }
        finally
        {
            if (epoch == _mediaHandoffEpoch)
            {
                _mediaSourceForeground = service.ForegroundNow;
                _mediaHandoff = null; _mediaHandoffInFlight = false; _mediaOpening = false; _mediaHandoffLanding = false; _mediaHandoffSourceCovered = false;
                SyncForegroundMediaVisibility(); ShowReturnedMediaIsland();
            }
            overlay.Close();
            if (epoch == _mediaHandoffEpoch) service.SnapshotCaptureSuspended = false;
        }
    }
}
