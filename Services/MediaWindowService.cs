using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Interop;
using Drawing = System.Drawing;

namespace alpha_island.Services;

internal sealed record MediaWindowState(IntPtr Handle, MediaWindowKind Kind, Rect Bounds, bool Foreground, bool Minimized);

/// <summary>Tracks the selected media application's windows without injecting into the player.</summary>
internal sealed class MediaWindowService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IslandTimer _timer;
    private readonly NativeMethods.WinEventProc _callback;
    private readonly int _captureIntervalMilliseconds;
    private GCHandle _callbackRoot;
    private IntPtr _foregroundHook, _minimizeHook;
    private string _process = "";
    private MediaWindowKind _kind;
    private long _epoch, _nextResolve, _nextCapture, _captureEpoch;
    private bool _snapshotCaptureSuspended;
    private bool _automaticSnapshotCaptureEnabled = true;
    internal bool RequireSnapshotForTransitions { get; set; }
    internal bool AutomaticSnapshotCaptureEnabled
    {
        get => _automaticSnapshotCaptureEnabled;
        set
        {
            if (_automaticSnapshotCaptureEnabled == value) return;
            _automaticSnapshotCaptureEnabled = value; _captureEpoch++;
        }
    }
    private bool _resolving, _capturing, _disposed, _minimizeSignaled;
    private IntPtr _window;
    private IntPtr _explicitWindow;
    private uint _explicitProcess;
    // Retain three inactive frames plus the current one; no extra capture timers.
    private readonly Dictionary<IntPtr, (uint Process, BitmapSource Frame, long LastUsed)> _trackedSnapshots = new();
    private Rect _bounds;
    private DwmTransitionLease? _transitions;
    private IntPtr _transitionFailure;
    internal bool TransitionsSuppressed => _transitions?.Applied == true;
    internal bool ForegroundNow => !_disposed && _window != IntPtr.Zero && !NativeMethods.IsIconic(_window)
        && IsSourceForeground();
    internal bool IsRestoredVisible(IntPtr window) => !_disposed && window != IntPtr.Zero && window == _window
        && NativeMethods.IsWindow(window) && (_explicitWindow == IntPtr.Zero || MatchesTrackedWindow(window))
        && NativeMethods.IsWindowVisible(window) && !NativeMethods.IsIconic(window);
    internal bool IsTrackedWindowValid(IntPtr window) => !_disposed && window != IntPtr.Zero && window == _window
        && MatchesTrackedWindow(window);
    internal MediaWindowState Current { get; private set; } = new(IntPtr.Zero, MediaWindowKind.None, Rect.Empty, false, false);
    internal BitmapSource? Snapshot { get; private set; }
    internal bool SnapshotCaptureSuspended
    {
        get => _snapshotCaptureSuspended;
        set
        {
            if (_snapshotCaptureSuspended == value) return;
            _snapshotCaptureSuspended = value;
            _captureEpoch++; // In-flight captures cannot cross either edge of a handoff.
        }
    }
    internal event Action<MediaWindowState>? Changed;
    internal event Action<MediaWindowState>? Minimizing;
    internal event Action? WindowActivityChanged;

    internal MediaWindowService(Dispatcher dispatcher, int captureIntervalMilliseconds = 500, int refreshIntervalMilliseconds = 200)
    {
        _dispatcher = dispatcher;
        _captureIntervalMilliseconds = Math.Clamp(captureIntervalMilliseconds, 500, 10_000);
        _callback = OnWinEvent;
        _callbackRoot = GCHandle.Alloc(_callback);
        _foregroundHook = NativeMethods.SetWinEventHook(3, 3, IntPtr.Zero, _callback, 0, 0, 0);
        _minimizeHook = NativeMethods.SetWinEventHook(0x16, 0x17, IntPtr.Zero, _callback, 0, 0, 0);
        if (_foregroundHook == IntPtr.Zero || _minimizeHook == IntPtr.Zero)
            IslandLog.Warn("MediaWindow", "窗口事件监听不可用，使用状态采样");
        _timer = new IslandTimer(dispatcher, "media-window") { Interval = TimeSpan.FromMilliseconds(Math.Clamp(refreshIntervalMilliseconds, 200, 2000)) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    internal void SetSource(string? source)
    {
        var profile = MediaHandoffPolicy.Source(source ?? "");
        if (_kind == profile.Kind && _process == profile.Process && _explicitWindow == IntPtr.Zero) return;
        _trackedSnapshots.Clear();
        _explicitWindow = IntPtr.Zero; _explicitProcess = 0;
        ReleaseTransitions();
        _kind = profile.Kind; _process = profile.Process; _epoch++;
        _window = IntPtr.Zero; _bounds = Rect.Empty; Snapshot = null; _nextResolve = 0; _minimizeSignaled = false;
        Publish(); Refresh();
    }

    // External assistants share the transition/capture machinery but track one exact
    // existing conversation window. Another window in the same process must not hide its island.
    internal void SetTrackedWindow(AgentAppWindow target)
    {
        if (_disposed || !NativeMethods.IsWindow(target.Handle)) return;
        NativeMethods.GetWindowThreadProcessId(target.Handle, out uint process);
        if (target.ProcessId != 0 && target.ProcessId != process) return;
        if (_explicitWindow == target.Handle && _explicitProcess == process) return;
        var snapshot = SwitchTrackedSnapshot(target.Handle, process);
        ReleaseTransitions(); _epoch++; _captureEpoch++;
        _explicitWindow = target.Handle; _explicitProcess = process;
        _process = target.Process; _kind = MediaWindowKind.DesktopPlayer;
        _window = target.Handle; _bounds = ReadBounds(target.Handle);
        Snapshot = snapshot; _minimizeSignaled = false; _nextResolve = 0; _nextCapture = 0;
        SuppressTransitions(); Publish(); Refresh();
    }

    private BitmapSource? SwitchTrackedSnapshot(IntPtr window, uint process)
    {
        var next = _trackedSnapshots.Remove(window, out var cached) && cached.Process == process ? cached.Frame : null;
        if (Snapshot != null && _explicitWindow != IntPtr.Zero && MatchesTrackedWindow(_explicitWindow))
            _trackedSnapshots[_explicitWindow] = (_explicitProcess, Snapshot, Environment.TickCount64);
        foreach (var entry in _trackedSnapshots.ToArray())
        {
            NativeMethods.GetWindowThreadProcessId(entry.Key, out uint currentProcess);
            if (!NativeMethods.IsWindow(entry.Key) || currentProcess != entry.Value.Process)
                _trackedSnapshots.Remove(entry.Key);
        }
        while (_trackedSnapshots.Count > 3)
            _trackedSnapshots.Remove(_trackedSnapshots.MinBy(entry => entry.Value.LastUsed).Key);
        return next;
    }

    private bool MatchesTrackedWindow(IntPtr window)
    {
        if (_explicitWindow == IntPtr.Zero) return ProcessMatches(window, _process);
        if (window != _explicitWindow || !NativeMethods.IsWindow(window)) return false;
        NativeMethods.GetWindowThreadProcessId(window, out uint process);
        return process == _explicitProcess;
    }

    private bool IsSourceForeground()
        => _explicitWindow == IntPtr.Zero ? ProcessMatches(NativeMethods.GetForegroundWindow(), _process)
            : NativeMethods.GetForegroundWindow() == _window && MatchesTrackedWindow(_window);

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            if (_disposed) return;
            // MINIMIZESTART can precede the native minimized state. Sample that state
            // before notifying the island, otherwise a still-foreground sample cancels its animation.
            if (eventType == NativeMethods.EVENT_SYSTEM_MINIMIZEEND && window == _window) _minimizeSignaled = false;
            Refresh();
            WindowActivityChanged?.Invoke();
        }));
    }

    private static bool ProcessMatches(IntPtr window, string process)
    {
        if (window == IntPtr.Zero || process.Length == 0) return false;
        try
        {
            NativeMethods.GetWindowThreadProcessId(window, out uint id);
            using var app = Process.GetProcessById((int)id);
            return string.Equals(app.ProcessName, process, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private async void Resolve()
    {
        if (_resolving || _disposed || _kind == MediaWindowKind.None) return;
        _resolving = true; long epoch = _epoch; string process = _process;
        try
        {
            var found = await Task.Run(() =>
            {
                if (_explicitWindow != IntPtr.Zero) return MatchesTrackedWindow(_explicitWindow) ? _explicitWindow : IntPtr.Zero;
                IntPtr best = IntPtr.Zero; double bestArea = 0;
                NativeMethods.EnumWindows((window, _) =>
                {
                    if (!NativeMethods.IsWindowVisible(window) || NativeMethods.GetWindow(window, 4) != IntPtr.Zero
                        || !ProcessMatches(window, process)) return true;
                    Rect bounds = ReadBounds(window);
                    double area = bounds.IsEmpty ? 0 : bounds.Width * bounds.Height;
                    if (bounds.Width >= 250 && bounds.Height >= 180 && area > bestArea) { best = window; bestArea = area; }
                    return true;
                }, IntPtr.Zero);
                return best;
            });
            if (_disposed || epoch != _epoch) return;
            if (_window != found)
            {
                ReleaseTransitions();
                _window = found; _bounds = Rect.Empty; Snapshot = null; _minimizeSignaled = false;
            }
            SuppressTransitions(); // Apply before the user minimizes; MINIMIZESTART notification is already too late.
            Publish();
        }
        catch (Exception ex) { IslandLog.Debug("MediaWindow", "窗口识别: " + ex.GetType().Name); }
        finally { _resolving = false; }
    }

    internal static Rect ReadBounds(IntPtr window)
    {
        if (!NativeMethods.IsIconic(window) && NativeMethods.GetWindowRect(window, out var rect))
            return Rectangle(rect);
        var placement = new NativeMethods.WINDOWPLACEMENT { Length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
        if (!NativeMethods.GetWindowPlacement(window, ref placement)) return Rect.Empty;
        var info = new NativeMethods.MONITORINFO { Size = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(window, 2), ref info))
        {
            int dx = info.Work.Left - info.Monitor.Left, dy = info.Work.Top - info.Monitor.Top;
            placement.Normal.Left += dx; placement.Normal.Right += dx; placement.Normal.Top += dy; placement.Normal.Bottom += dy;
        }
        return Rectangle(placement.Normal);
    }
    private static Rect Rectangle(NativeMethods.RECT rect) => rect.Right > rect.Left && rect.Bottom > rect.Top
        ? new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top) : Rect.Empty;

    private void Refresh()
    {
        if (_disposed || _kind == MediaWindowKind.None) return;
        long now = Environment.TickCount64;
        if (now >= _nextResolve) { _nextResolve = now + 1500; Resolve(); }
        if (_window != IntPtr.Zero && (!NativeMethods.IsWindow(_window)
            || _explicitWindow != IntPtr.Zero && !MatchesTrackedWindow(_window)))
        { ReleaseTransitions(); _window = IntPtr.Zero; _bounds = Rect.Empty; Snapshot = null; _minimizeSignaled = false; _nextResolve = 0; }
        Publish();
        if (AutomaticSnapshotCaptureEnabled && _kind == MediaWindowKind.DesktopPlayer && Current.Foreground && !Current.Minimized && now >= _nextCapture)
        { _nextCapture = now + _captureIntervalMilliseconds; Capture(); }
    }

    private void Publish()
    {
        bool minimized = _window != IntPtr.Zero && NativeMethods.IsIconic(_window);
        if (_window != IntPtr.Zero && (!minimized || _bounds.IsEmpty)) _bounds = ReadBounds(_window);
        bool foreground = _kind != MediaWindowKind.None && (_kind == MediaWindowKind.Browser || !minimized)
            && IsSourceForeground();
        var next = new MediaWindowState(_window, _kind, _bounds, foreground, minimized);
        var previous = Current; Current = next;
        // Claim the transition before the general background callback can show the island.
        if (minimized && !previous.Minimized && previous.Handle == next.Handle) SignalMinimize();
        if (next != previous) Changed?.Invoke(next);
        if (previous.Minimized && !minimized) _minimizeSignaled = false;
    }

    private void SignalMinimize()
    {
        if (_minimizeSignaled || _kind != MediaWindowKind.DesktopPlayer || _window == IntPtr.Zero || _bounds.IsEmpty) return;
        _minimizeSignaled = true;
        Minimizing?.Invoke(Current with { Bounds = _bounds, Foreground = false, Minimized = true });
    }

    private async void Capture()
    {
        if (_capturing || _disposed || SnapshotCaptureSuspended || _window == IntPtr.Zero || _bounds.IsEmpty) return;
        _capturing = true; long epoch = _epoch, captureEpoch = _captureEpoch; IntPtr window = _window; Rect bounds = _bounds;
        try
        {
            var frame = await Task.Run(() =>
            {
                if (SnapshotCaptureSuspended || NativeMethods.GetForegroundWindow() != window || NativeMethods.IsIconic(window)) return null;
                var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
                var area = Drawing.Rectangle.Intersect(desktop, new Drawing.Rectangle((int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height));
                // A cropped window cannot be stretched into a convincing full-window transition.
                if (area.Width != (int)bounds.Width || area.Height != (int)bounds.Height || (long)area.Width * area.Height > 16_000_000) return null;
                using var bitmap = ScreenTextService.Capture(area);
                if (SnapshotCaptureSuspended || NativeMethods.GetForegroundWindow() != window || NativeMethods.IsIconic(window)) return null;
                using var bytes = new MemoryStream(); bitmap.Save(bytes, Drawing.Imaging.ImageFormat.Png); bytes.Position = 0;
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = Math.Min(1400, bitmap.Width); image.StreamSource = bytes; image.EndInit(); image.Freeze();
                return image;
            });
            PublishSnapshot(epoch, captureEpoch, window, frame);
        }
        catch (Exception ex) { IslandLog.Debug("MediaWindow", "窗口快照不可用: " + ex.GetType().Name); }
        finally { _capturing = false; }
    }

    private void PublishSnapshot(long epoch, long captureEpoch, IntPtr window, BitmapSource? frame)
    {
        if (!_disposed && !SnapshotCaptureSuspended && epoch == _epoch && captureEpoch == _captureEpoch
            && window == _window && (_explicitWindow == IntPtr.Zero || MatchesTrackedWindow(window)) && frame != null)
        {
            Snapshot = frame;
            if (RequireSnapshotForTransitions) SuppressTransitions();
        }
    }

    internal void RefreshSnapshot()
    {
        if (_disposed || !AutomaticSnapshotCaptureEnabled || _kind != MediaWindowKind.DesktopPlayer || !Current.Foreground || Current.Minimized) return;
        _nextCapture = Environment.TickCount64 + _captureIntervalMilliseconds;
        Capture();
    }

    internal async Task<BitmapSource?> CaptureWindowSnapshotAsync()
    {
        if (_disposed || SnapshotCaptureSuspended || !IsTrackedWindowValid(_window) || _bounds.IsEmpty) return Snapshot;
        long epoch = _epoch, captureEpoch = _captureEpoch;
        IntPtr window = _window; Rect bounds = _bounds;
        try
        {
            var frame = await WindowSnapshotCapture.CaptureAsync(window, bounds);
            PublishSnapshot(epoch, captureEpoch, window, frame);
        }
        catch (Exception ex) when (ex is TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { IslandLog.Debug("MediaWindow", "按需窗口快照降级: " + ex.GetType().Name); }
        return Snapshot;
    }

    internal bool Restore(IntPtr window)
    {
        if (_disposed || window == IntPtr.Zero || window != _window || !NativeMethods.IsWindow(window) || !MatchesTrackedWindow(window)) return false;
        if (NativeMethods.IsIconic(window) || !NativeMethods.IsWindowVisible(window)) NativeMethods.ShowWindowAsync(window, NativeMethods.SW_RESTORE);
        NativeMethods.SetForegroundWindow(window);
        _minimizeSignaled = false;
        return true;
    }

    internal void AllowNativeTransitions() => ReleaseTransitions();

    private void SuppressTransitions()
    {
        if (RequireSnapshotForTransitions && Snapshot == null) return;
        if (_kind != MediaWindowKind.DesktopPlayer || _window == IntPtr.Zero || _transitions != null) return;
        _transitions = DwmTransitionLease.TryAcquire(_window);
        if (_transitions != null)
            IslandLog.Debug("MediaWindow", $"已提前关闭 {_process} 窗口的系统最小化/还原过渡");
        else if (_transitionFailure != _window)
        {
            _transitionFailure = _window;
            IslandLog.Warn("MediaWindow", "无法关闭播放器系统过渡，改用系统动画避免双重动画");
        }
    }

    private void ReleaseTransitions()
    {
        _transitions?.Dispose(); _transitions = null; _transitionFailure = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _epoch++;
        ReleaseTransitions();
        _timer.Dispose();
        if (_foregroundHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_foregroundHook);
        if (_minimizeHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_minimizeHook);
        if (_callbackRoot.IsAllocated) _callbackRoot.Free();
        Snapshot = null; _trackedSnapshots.Clear(); Changed = null; Minimizing = null; WindowActivityChanged = null;
    }
}
