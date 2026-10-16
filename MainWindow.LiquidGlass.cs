using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Interop;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace alpha_island;

public partial class MainWindow
{
    private readonly LiquidIslandShape _liquidShape = new();
    private readonly Dictionary<Border, LiquidGlassChrome> _glassChrome = new();
    private readonly Dictionary<Border, (long Start, double Duration, LiquidMotionKind Kind)> _liquidOrbMotions = new();
    private readonly Dictionary<Border, LiquidIslandShape> _liquidContours = new();
    private readonly HashSet<Border> _glassBorders = new();
    private readonly Dictionary<Border, Brush?> _liquidJoinEdges = new();
    private LiquidBridgeAdorner? _liquidBridgeLayer;
    private IslandTimer? _glassLightTimer;
    private GlassLight _glassLight = GlassLight.Default;
    private Brush? _originalGlassEdge;
    private bool _glassSampling, _liquidFrameSubscribed, _liquidRefreshQueued, _glassDisposed;
    private bool _glassHostVisible;
    private int _glassGeneration;
    private Rect _liquidHostBounds = Rect.Empty;
    internal int GlassLightSamples { get; private set; }
    internal bool LiquidFrameLoopActive => _liquidFrameSubscribed;
    private bool LiquidMotionAllowed => _options.GelAnimation && !MotionPolicy.InstantMorph && !MotionPolicy.PowerSave;
    private bool NativeGlassAllowed => _options.LiquidGlassBackdrop && !MotionPolicy.SoftwareRendering && !MotionPolicy.PowerSave && !SystemParameters.HighContrast;

    private void InitializeLiquidGlass()
    {
        _originalGlassEdge = IslandBody.BorderBrush;
        IslandBody.Clip = _liquidShape.Geometry;
        RegisterLiquidChrome(IslandBody);
        MusicOrbBubble.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Border.Background)) { Source = IslandBody });
        RegisterLiquidChrome(MusicOrbBubble); RegisterLiquidChrome(PinBubble); RegisterLiquidChrome(SideBubble);
        _glassLightTimer = new IslandTimer(Dispatcher, "glass-light") { Interval = TimeSpan.FromMilliseconds(700) };
        _glassLightTimer.Tick += async (_, _) => await SampleGlassLightAsync();
        Loaded += (_, _) => { AttachLiquidAdorners(); ApplyLiquidGlassAppearance(); QueueLiquidRefresh(); };
        IsVisibleChanged += (_, _) => { if (!_glassDisposed) { SyncGlassLightPacing(); QueueLiquidRefresh(); } };
        LocationChanged += (_, _) => { _liquidHostBounds = Rect.Empty; QueueLiquidRefresh(); };
        MotionPolicy.Changed += OnLiquidMotionPolicyChanged;
        SatelliteOrbAnimator.MotionStarted += BeginLiquidOrbMotion;
    }

    private void RegisterLiquidChrome(Border? border)
    {
        if (border == null || !_glassBorders.Add(border)) return;
        var contour = ReferenceEquals(border, IslandBody) ? _liquidShape : new LiquidIslandShape();
        _liquidContours[border] = contour; border.Clip = contour.Geometry;
        border.Loaded += (_, _) => { AttachLiquidAdorners(); QueueLiquidRefresh(); };
        border.Unloaded += (_, _) =>
        {
            if (_glassChrome.Remove(border, out var chrome)) AdornerLayer.GetAdornerLayer(border)?.Remove(chrome);
            _liquidOrbMotions.Remove(border); RestoreLiquidEdge(border); LiquidMaterialOverlap.Apply(border, null);
            if (_liquidContours.TryGetValue(border, out var unloadedContour)) unloadedContour.SetContact();
        };
        border.SizeChanged += (_, _) => { if (!_liquidFrameSubscribed) QueueLiquidRefresh(); };
        border.IsVisibleChanged += (_, _) => QueueLiquidRefresh();
    }

    private void AttachLiquidAdorners()
    {
        if (_glassDisposed || IslandScene == null) return;
        if (_liquidBridgeLayer == null && AdornerLayer.GetAdornerLayer(IslandScene) is { } sceneLayer)
        {
            _liquidBridgeLayer = new LiquidBridgeAdorner(IslandScene) { IsHitTestVisible = false, Focusable = false, Fill = IslandBody.Background };
            sceneLayer.Add(_liquidBridgeLayer);
        }
        foreach (var border in _glassBorders)
        {
            if (_glassChrome.ContainsKey(border) || AdornerLayer.GetAdornerLayer(border) is not { } layer) continue;
            var chrome = new LiquidGlassChrome(border, _liquidContours[border]);
            _glassChrome.Add(border, chrome); layer.Add(chrome);
        }
        ApplyLiquidGlassAppearance();
    }

    private void ApplyLiquidGlassAppearance()
    {
        if (_glassDisposed || _options == null) return;
        bool enabled = _options.LiquidGlassBackdrop && !SystemParameters.HighContrast;
        IslandBody.BorderBrush = enabled ? Brushes.Transparent : _originalGlassEdge;
        Color accent = (TryFindResource("AccentCyan") as SolidColorBrush)?.Color ?? Colors.Cyan;
        foreach (var chrome in _glassChrome.Values)
        { chrome.SetMaterial(accent, _options.GlassOpacity, enabled); chrome.SetLight(_glassLight, false); }
        SyncGlassLightPacing();
    }

    private void OnLiquidMotionPolicyChanged()
    {
        _glassGeneration++; _liquidHostBounds = Rect.Empty;
        if (!LiquidMotionAllowed)
        {
            foreach (var border in _liquidOrbMotions.Keys.ToArray()) SatelliteOrbAnimator.CompleteMotion(border);
            _liquidOrbMotions.Clear();
            foreach (var contour in _liquidContours.Values) contour.SetContact();
        }
        ApplyLiquidGlassAppearance(); QueueLiquidRefresh();
    }

    private void OnLiquidMorphFrame()
    {
        if (_glassChrome.TryGetValue(IslandBody, out var chrome)) chrome.InvalidateVisual();
        if (!_liquidFrameSubscribed) QueueLiquidRefresh();
    }

    private void OnLiquidMorphRunning(bool running)
    {
        _liquidHostBounds = Rect.Empty;
        if (running) SyncLiquidBackdrop(); else QueueLiquidRefresh();
    }

    private void BeginLiquidOrbMotion(Border border, TimeSpan duration)
    {
        if (_glassDisposed || !LiquidMotionAllowed || !_glassBorders.Contains(border)) return;
        if (!_liquidJoinEdges.ContainsKey(border)) _liquidJoinEdges[border] = border.BorderBrush;
        _liquidOrbMotions[border] = (Environment.TickCount64, duration.TotalMilliseconds, SatelliteOrbAnimator.Kind(border));
        _liquidHostBounds = Rect.Empty;
        UpdateLiquidFrameSubscription();
    }

    private bool HasLiquidMotion => _animator?.IsRunning == true || _liquidOrbMotions.Count > 0;
    private void UpdateLiquidFrameSubscription()
    {
        bool needed = !_glassDisposed && IsVisible && HasLiquidMotion;
        if (needed == _liquidFrameSubscribed) return;
        _liquidFrameSubscribed = needed;
        if (needed) CompositionTarget.Rendering += OnBackdropTick;
        else CompositionTarget.Rendering -= OnBackdropTick;
    }

    private void QueueLiquidRefresh()
    {
        if (_glassDisposed || _liquidRefreshQueued || Dispatcher.HasShutdownStarted) return;
        _liquidRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _liquidRefreshQueued = false;
            if (!_glassDisposed) SyncLiquidBackdrop();
        }));
    }

    private Rect GlassRect(Border border, bool screen)
    {
        if (!border.IsVisible || border.Opacity < .01 || border.ActualWidth < 1 || border.ActualHeight < 1) return Rect.Empty;
        try
        {
            Point a = screen ? border.PointToScreen(new Point()) : border.TranslatePoint(new Point(), IslandScene);
            Point b = screen ? border.PointToScreen(new Point(border.ActualWidth, border.ActualHeight)) : border.TranslatePoint(new Point(border.ActualWidth, border.ActualHeight), IslandScene);
            return new Rect(a, b);
        }
        catch (InvalidOperationException) { return Rect.Empty; }
    }

    private void RestoreLiquidEdge(Border border)
    {
        if (_liquidJoinEdges.Remove(border, out var edge)) border.BorderBrush = edge;
    }

    private Geometry SceneSurface(Border border, Rect rect)
    {
        Geometry shape = _liquidContours[border].Geometry.Clone();
        shape.Transform = new MatrixTransform(rect.Width / Math.Max(1, border.ActualWidth), 0, 0,
            rect.Height / Math.Max(1, border.ActualHeight), rect.X, rect.Y);
        return shape;
    }

    private Geometry? UpdateLiquidBridges()
    {
        var bridges = new GeometryGroup { FillRule = FillRule.Nonzero };
        var contacts = new Dictionary<Border, (LiquidContact Left, LiquidContact Right)>();
        var joined = new HashSet<Border>();
        var rects = _glassBorders.Select(border => (Border: border, Rect: GlassRect(border, false)))
            .Where(item => !item.Rect.IsEmpty).ToDictionary(item => item.Border, item => item.Rect);
        foreach (var pair in _liquidContours)
        {
            var border = pair.Key;
            // The main animator owns its frame geometry; ActualWidth/Height can still be from the previous layout.
            if (!ReferenceEquals(border, IslandBody) && rects.ContainsKey(border))
                pair.Value.Update(new(Math.Max(1, border.ActualWidth), Math.Max(1, border.ActualHeight), 0, border.CornerRadius));
        }
        void Contact(Border border, LiquidContact value, bool onLeft)
        {
            if (!value.Active) return;
            var local = value.InLocal(rects[border], border.ActualWidth, border.ActualHeight);
            contacts.TryGetValue(border, out var prior);
            var existing = onLeft ? prior.Left : prior.Right;
            if (Math.Abs(local.Pull) > Math.Abs(existing.Pull))
                contacts[border] = onLeft ? (local, prior.Right) : (prior.Left, local);
            joined.Add(border);
        }
        var pairs = new HashSet<(Border Left, Border Right)>();
        long now = Environment.TickCount64;
        foreach (var item in _liquidOrbMotions.ToArray())
        {
            double phase = (now - item.Value.Start) / Math.Max(1, item.Value.Duration);
            if (phase >= 1 || !LiquidMotionAllowed || item.Key.Visibility != Visibility.Visible || item.Key.Opacity < .01)
            { _liquidOrbMotions.Remove(item.Key); continue; }
            if (!rects.TryGetValue(item.Key, out var other)) continue; // First layout has not arrived; retain the finite lease.
            Border? parent = null; double best = double.MaxValue;
            foreach (var candidate in rects)
            {
                if (ReferenceEquals(candidate.Key, item.Key)) continue;
                var a = candidate.Value;
                double cy = other.Top + other.Height / 2;
                if (cy < a.Top - 4 || cy > a.Bottom + 4) continue;
                bool before = other.Left + other.Width / 2 < a.Left + a.Width / 2;
                double gap = before ? a.Left - other.Right : other.Left - a.Right;
                double score = Math.Abs(gap);
                if (score < best) { best = score; parent = candidate.Key; }
            }
            if (parent == null) continue;
            var parentRect = rects[parent];
            bool orbLeft = other.Left + other.Width / 2 < parentRect.Left + parentRect.Width / 2;
            if (!pairs.Add(orbLeft ? (item.Key, parent) : (parent, item.Key))) continue;
            var frame = LiquidCellMotion.Evaluate(orbLeft ? other : parentRect, orbLeft ? parentRect : other, phase, item.Value.Kind);
            Contact(orbLeft ? item.Key : parent, frame.Left, false);
            Contact(orbLeft ? parent : item.Key, frame.Right, true);
            if (frame.Left.Active || frame.Right.Active || other.IntersectsWith(parentRect)) { joined.Add(item.Key); joined.Add(parent); }
            if (frame.Neck != null) bridges.Children.Add(frame.Neck);
        }
        foreach (var pair in _liquidContours)
        {
            contacts.TryGetValue(pair.Key, out var contact);
            pair.Value.SetContact(contact.Left, contact.Right);
            if (joined.Contains(pair.Key))
            {
                if (!_liquidJoinEdges.ContainsKey(pair.Key)) _liquidJoinEdges[pair.Key] = pair.Key.BorderBrush;
                pair.Key.BorderBrush = Brushes.Transparent;
            }
            else RestoreLiquidEdge(pair.Key);
        }
        foreach (var pair in _liquidContours)
        {
            if (ReferenceEquals(pair.Key, IslandBody) || !joined.Contains(pair.Key) || !rects.TryGetValue(pair.Key, out var rect))
            { LiquidMaterialOverlap.Apply(pair.Key, null); continue; }
            var overlap = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var other in rects.Where(p => !ReferenceEquals(p.Key, pair.Key) && (ReferenceEquals(p.Key, IslandBody) || p.Value.Left < rect.Left)))
                overlap.Children.Add(SceneSurface(other.Key, other.Value));
            overlap.Transform = new MatrixTransform(pair.Key.ActualWidth / rect.Width, 0, 0, pair.Key.ActualHeight / rect.Height,
                -rect.X * pair.Key.ActualWidth / rect.Width, -rect.Y * pair.Key.ActualHeight / rect.Height);
            LiquidMaterialOverlap.Apply(pair.Key, overlap.Children.Count == 0 ? null : overlap);
        }
        Geometry? result = bridges.Children.Count == 0 ? null : bridges;
        var surfaces = new GeometryGroup { FillRule = FillRule.Nonzero };
        var painted = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var pair in rects)
        {
            var surface = SceneSurface(pair.Key, pair.Value); surfaces.Children.Add(surface);
            var round = new RectangleGeometry(new Rect(0, 0, pair.Key.ActualWidth, pair.Key.ActualHeight), pair.Key.CornerRadius.TopLeft, pair.Key.CornerRadius.TopLeft);
            round.Transform = surface.Transform;
            painted.Children.Add(new CombinedGeometry(GeometryCombineMode.Intersect, surface, round));
        }
        if (_liquidBridgeLayer != null)
        {
            var all = new GeometryGroup { FillRule = FillRule.Nonzero };
            if (result != null) all.Children.Add(result);
            // Fill the bulging membrane beyond the old Border, without painting its translucent material twice.
            foreach (var pair in rects.Where(p => _liquidContours[p.Key].HasContact)) all.Children.Add(SceneSurface(pair.Key, pair.Value));
            _liquidBridgeLayer.Bridges = result;
            _liquidBridgeLayer.Surface = all.Children.Count == 0 ? null : new CombinedGeometry(GeometryCombineMode.Exclude, all, painted);
            _liquidBridgeLayer.Bodies = surfaces;
            _liquidBridgeLayer.Fill = IslandBody.Background;
            _liquidBridgeLayer.InvalidateVisual();
        }
        foreach (var pair in _glassChrome)
        {
            pair.Value.JoinMask = null;
            if (joined.Contains(pair.Key) && rects.TryGetValue(pair.Key, out var rect))
            {
                var mask = new GeometryGroup { FillRule = FillRule.Nonzero };
                if (result != null) mask.Children.Add(result);
                foreach (var other in rects.Where(p => !ReferenceEquals(p.Key, pair.Key))) mask.Children.Add(SceneSurface(other.Key, other.Value));
                mask.Transform = new MatrixTransform(pair.Key.ActualWidth / rect.Width, 0, 0, pair.Key.ActualHeight / rect.Height,
                    -rect.X * pair.Key.ActualWidth / rect.Width, -rect.Y * pair.Key.ActualHeight / rect.Height);
                pair.Value.JoinMask = mask;
            }
            pair.Value.InvalidateVisual();
        }
        return result;
    }

    private void SyncLiquidBackdrop()
    {
        if (_glassDisposed) return;
        AttachLiquidAdorners();
        RenderLiquidBackdrop();
        SyncGlassLightPacing();
        UpdateLiquidFrameSubscription();
    }

    private void RenderLiquidBackdrop()
    {
        if (_glassDisposed) return;
        Geometry? bridge = UpdateLiquidBridges();
        UpdateLiquidFrameSubscription();
        var backdrop = _backdrop;
        if (backdrop is not { IsAvailable: true }) return;
        bool show = NativeGlassAllowed && IsVisible && WindowState != System.Windows.WindowState.Minimized && (_state != IslandState.Bar || HasLiquidMotion);
        if (!show) { backdrop.SetVisible(false); _glassHostVisible = false; return; }
        var surfaces = _glassBorders.Select(border => (Border: border, Rect: GlassRect(border, true))).Where(item => !item.Rect.IsEmpty).ToArray();
        if (surfaces.Length == 0) { backdrop.SetVisible(false); _glassHostVisible = false; return; }
        var bounds = surfaces.Select(item => item.Rect).Aggregate(Rect.Union);
        if (HasLiquidMotion)
        {
            var anchor = PointToScreen(new Point(ActualWidth / 2, 0));
            var dpi = VisualTreeHelper.GetDpi(this);
            var envelope = _animator?.MotionBounds ?? new Rect(-bounds.Width / 2, 0, bounds.Width, bounds.Height);
            bounds.Union(new Rect(anchor.X + envelope.X * dpi.DpiScaleX, anchor.Y + envelope.Y * dpi.DpiScaleY,
                envelope.Width * dpi.DpiScaleX, envelope.Height * dpi.DpiScaleY));
            // The whole reserved rail is transparent outside the vector mask; its HWND does not chase each orb.
            double rail = _activeSatelliteOrder.Sum(key => SatelliteWidth(key) + 8) * dpi.DpiScaleX;
            bounds = new Rect(bounds.X - rail - 16, bounds.Y - 8, bounds.Width + rail + 32, bounds.Height + 16);
            bounds.Inflate(2, 2);
            if (!_liquidHostBounds.IsEmpty) bounds = _liquidHostBounds;
        }
        else bounds.Inflate(2, 2);
        var host = new Rect(Math.Floor(bounds.X), Math.Floor(bounds.Y), Math.Ceiling(bounds.Right) - Math.Floor(bounds.X), Math.Ceiling(bounds.Bottom) - Math.Floor(bounds.Y));
        if (host != _liquidHostBounds)
        { _liquidHostBounds = host; backdrop.SetBounds((int)host.X, (int)host.Y, (int)host.Width, (int)host.Height); }
        var outline = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var item in surfaces)
        {
            Geometry shape = _liquidContours[item.Border].Geometry.Clone();
            double sx = item.Rect.Width / Math.Max(1, item.Border.ActualWidth), sy = item.Rect.Height / Math.Max(1, item.Border.ActualHeight);
            shape.Transform = new MatrixTransform(sx, 0, 0, sy, item.Rect.X - host.X, item.Rect.Y - host.Y);
            outline.Children.Add(shape);
        }
        if (bridge != null)
        {
            var copy = bridge.Clone(); var a = IslandScene.PointToScreen(new Point()); var b = IslandScene.PointToScreen(new Point(1, 1));
            copy.Transform = new MatrixTransform(b.X - a.X, 0, 0, b.Y - a.Y, a.X - host.X, a.Y - host.Y); outline.Children.Add(copy);
        }
        // A failed exact clip hides the host, avoiding rectangular/native material leaks.
        if (!backdrop.SetOutline(outline)) { backdrop.SetVisible(false); _glassHostVisible = false; return; }
        if (!_glassHostVisible) backdrop.PlaceBelow(new WindowInteropHelper(this).Handle);
        backdrop.SetVisible(true);
        _glassHostVisible = true;
    }

    private void SyncGlassLightPacing()
    {
        if (_glassLightTimer == null) return;
        bool active = !_glassDisposed && _runtimeEnabled && IsVisible && _backdrop is { IsAvailable: true } && NativeGlassAllowed && !MotionPolicy.InstantMorph
            && WindowState != System.Windows.WindowState.Minimized
            && _state != IslandState.Bar && !GameDoNotDisturbActive && !System.Windows.Forms.SystemInformation.TerminalServerSession;
        if (active) _glassLightTimer.Start(); else { _glassLightTimer.Stop(); _glassGeneration++; }
    }

    private async Task SampleGlassLightAsync()
    {
        if (_glassSampling || _glassDisposed || _glassLightTimer?.IsEnabled != true || HasLiquidMotion || _powerTransitionPreparing) return;
        var island = GlassRect(IslandBody, true); if (island.IsEmpty) return;
        var exclusions = _glassBorders.Select(border => GlassRect(border, true)).Where(rect => !rect.IsEmpty).ToArray();
        var area = DesktopLightSampler.DesktopBounds();
        int generation = _glassGeneration;
        _glassSampling = true;
        try
        {
            var result = await Task.Run(() => DesktopLightSampler.Capture(island, area, exclusions));
            if (_glassDisposed || generation != _glassGeneration || result == null) return;
            GlassLightSamples++; _glassLight = GlassLight.Blend(_glassLight, result.Value.Light, .68);
            foreach (var chrome in _glassChrome.Values) chrome.SetLight(_glassLight, true);
            _glassLightTimer!.Interval = TimeSpan.FromMilliseconds(result.Value.Milliseconds > 25 ? 3000 : result.Value.Milliseconds > 10 ? 1500 : 700);
        }
        catch { if (!_glassDisposed && _glassLightTimer != null) _glassLightTimer.Interval = TimeSpan.FromSeconds(5); }
        finally { _glassSampling = false; }
    }

    private void DisposeLiquidGlass()
    {
        _glassDisposed = true; _glassGeneration++; _glassLightTimer?.Dispose();
        MotionPolicy.Changed -= OnLiquidMotionPolicyChanged;
        SatelliteOrbAnimator.MotionStarted -= BeginLiquidOrbMotion;
        CompositionTarget.Rendering -= OnBackdropTick; _liquidFrameSubscribed = false;
        foreach (var pair in _glassChrome) AdornerLayer.GetAdornerLayer(pair.Key)?.Remove(pair.Value);
        foreach (var border in _liquidJoinEdges.Keys.ToArray()) RestoreLiquidEdge(border);
        foreach (var pair in _liquidContours) { pair.Value.SetContact(); LiquidMaterialOverlap.Apply(pair.Key, null); }
        _glassChrome.Clear(); _glassBorders.Clear(); _liquidOrbMotions.Clear(); _liquidContours.Clear();
        if (_liquidBridgeLayer != null) AdornerLayer.GetAdornerLayer(IslandScene)?.Remove(_liquidBridgeLayer);
        _liquidBridgeLayer = null;
    }
}
