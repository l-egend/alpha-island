using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Interop;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace alpha_island.Animation;

/// <summary>A short-lived, click-through visual proxy; the real player keeps its own window.</summary>
internal sealed class MediaHandoffOverlay : Window
{
    private const int Columns = 12, Rows = 64;
    internal const double DurationMilliseconds = 360;
    private const double TransferMilliseconds = 40;
    private readonly Viewport3D _viewport = new() { IsHitTestVisible = false };
    private readonly OrthographicCamera _camera = new();
    private readonly MeshGeometry3D _mesh = new();
    private readonly GeometryModel3D _surface = new();
    private readonly VisualBrush _texture;
    private readonly Border _body;
    private readonly RectangleGeometry _clip = new();
    private readonly Image _windowImage;
    private readonly UIElement _islandContent;
    private readonly FrameworkElement? _fallbackIsland;
    private readonly Rect _from, _to, _extent;
    private readonly bool _opening;
    private readonly Action? _reveal;
    private readonly Action? _sourceCovered;
    private readonly Func<bool>? _destinationReady;
    private readonly CancellationTokenSource _deadline = new();
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IntPtr _handle;
    private long _started, _arrivedAt, _transferStarted;
    private bool _arrived, _finished, _sourceHandedOver, _playStarted;
    private Task? _sourceCommit, _destinationCommit;
    private static readonly object PresentationGate = new();
    private static Task? _presentation;
    private static Task PresentAsync()
    {
        lock (PresentationGate)
        {
            if (_presentation == null || _presentation.IsCompleted) _presentation = Task.Run(() => NativeMethods.DwmFlush());
            return _presentation;
        }
    }
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
    internal double Progress { get; private set; }

    internal MediaHandoffOverlay(Rect from, Rect to, ImageSource? snapshot, ImageSource? artwork,
        string title, bool opening, ImageSource? islandPreview = null, Action? reveal = null, Func<bool>? destinationReady = null,
        Action? sourceCovered = null, string fallbackSubtitle = "网易云音乐")
    {
        _from = from; _to = to; _opening = opening; _reveal = reveal; _destinationReady = destinationReady; _sourceCovered = sourceCovered;
        _extent = Rect.Union(from, to); _extent.Inflate(24, 24);
        WindowStyle = WindowStyle.None; AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; Focusable = false; IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "Alpha Island · 媒体过渡";
        _windowImage = new Image { Source = snapshot, Stretch = Stretch.Fill };
        var layers = new Grid(); layers.Children.Add(_windowImage);
        if (islandPreview != null)
            _islandContent = new Image { Source = islandPreview, Stretch = Stretch.Fill };
        else
        {
            var pill = new Grid { Margin = new Thickness(12, 0, 14, 0) };
            pill.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); pill.ColumnDefinitions.Add(new ColumnDefinition());
            var cover = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(9), VerticalAlignment = VerticalAlignment.Center,
                Background = artwork != null ? new ImageBrush(artwork) { Stretch = Stretch.UniformToFill } : new SolidColorBrush(Color.FromRgb(58, 65, 83)) };
            pill.Children.Add(cover);
            var text = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = fallbackSubtitle, FontSize = 10, Foreground = System.Windows.Media.Brushes.White, Opacity = .7 });
            Grid.SetColumn(text, 1); pill.Children.Add(text);
            _fallbackIsland = new Border { Child = pill };
            _islandContent = new Viewbox { Stretch = Stretch.Fill, Child = _fallbackIsland };
        }
        layers.Children.Add(_islandContent);
        _body = new Border { Background = snapshot == null && islandPreview != null ? System.Windows.Media.Brushes.Transparent
            : new SolidColorBrush(Color.FromRgb(25, 28, 39)), Child = layers, Clip = _clip };
        _texture = new VisualBrush(_body) { Stretch = Stretch.Fill, AutoLayoutContent = false,
            ViewboxUnits = BrushMappingMode.Absolute };
        var material = new DiffuseMaterial(_texture);
        _surface.Material = material; _surface.BackMaterial = material;
        var positions = new Point3DCollection((Columns + 1) * (Rows + 1));
        var coordinates = new PointCollection((Columns + 1) * (Rows + 1));
        var indices = new Int32Collection(Columns * Rows * 6);
        for (int row = 0; row <= Rows; row++)
        for (int column = 0; column <= Columns; column++)
        {
            positions.Add(new Point3D());
            coordinates.Add(new Point((double)column / Columns, (double)row / Rows));
            if (row == Rows || column == Columns) continue;
            int a = row * (Columns + 1) + column, b = a + 1, c = a + Columns + 1, d = c + 1;
            indices.Add(a); indices.Add(c); indices.Add(b);
            indices.Add(b); indices.Add(c); indices.Add(d);
        }
        coordinates.Freeze(); indices.Freeze();
        _mesh.Positions = positions; _mesh.TextureCoordinates = coordinates; _mesh.TriangleIndices = indices;
        _surface.Geometry = _mesh;
        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Colors.White)); scene.Children.Add(_surface);
        _viewport.Camera = _camera; _viewport.Children.Add(new ModelVisual3D { Content = scene });
        Content = _viewport;
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            int style = NativeMethods.GetWindowLong(_handle, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(_handle, NativeMethods.GWL_EXSTYLE,
                style | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
        };
        Closed += (_, _) => { Finish(false); _texture.Visual = null; };
    }

    internal Task<bool> PlayAsync()
    {
        Dispatcher.VerifyAccess();
        if (_playStarted || _finished) return _completion.Task;
        _playStarted = true;
        // Create and position the hidden HWND first. Showing an empty window and
        // subsequently resizing/texturing it can expose an uninitialised first frame.
        new WindowInteropHelper(this).EnsureHandle();
        NativeMethods.SetWindowPos(_handle, new IntPtr(-1), (int)Math.Floor(_extent.X), (int)Math.Floor(_extent.Y),
            (int)Math.Ceiling(_extent.Width), (int)Math.Ceiling(_extent.Height), 0x0010);
        PrepareTexture();
        ApplyFrame(0);
        Show();
        CompositionTarget.Rendering += Render;
        _ = WatchDeadlineAsync(_deadline.Token);
        return _completion.Task;
    }

    private void Render(object? sender, EventArgs args)
    {
        if (_finished) return;
        try
        {
            if (args is RenderingEventArgs frame)
            {
                if (frame.RenderingTime == _lastRenderingTime) return;
                _lastRenderingTime = frame.RenderingTime;
            }
            if (!_sourceHandedOver)
            {
                if (_sourceCommit == null) { _sourceCommit = CommitFrameAsync(); return; }
                if (!_sourceCommit.IsCompleted) return;
                _sourceHandedOver = true;
                _started = Stopwatch.GetTimestamp();
                _sourceCovered?.Invoke();
            }
            if (!_arrived)
            {
                double progress = Math.Clamp(Stopwatch.GetElapsedTime(_started).TotalMilliseconds / (DurationMilliseconds - TransferMilliseconds), 0, 1);
                ApplyFrame(progress);
                if (progress < 1) return;
                _arrived = true; _arrivedAt = Stopwatch.GetTimestamp();
                // Prepare the live destination while the complete proxy still covers it.
                _reveal?.Invoke();
                return;
            }
            if (_transferStarted == 0)
            {
                if (_destinationReady?.Invoke() == false)
                {
                    if (Stopwatch.GetElapsedTime(_arrivedAt).TotalMilliseconds >= 200) Finish(false);
                    return;
                }
                // WPF Rendering happens before presentation. Wait until layout/render
                // has left the dispatcher and DWM has committed a frame, not just HWND visibility.
                if (_destinationCommit == null) { _destinationCommit = CommitFrameAsync(); return; }
                if (!_destinationCommit.IsCompleted) return;
                _transferStarted = Stopwatch.GetTimestamp();
                return;
            }
            double transfer = Math.Clamp(Stopwatch.GetElapsedTime(_transferStarted).TotalMilliseconds / TransferMilliseconds, 0, 1);
            _viewport.Opacity = 1 - MediaHandoffPolicy.Smooth(transfer);
            if (transfer >= 1) Finish(true);
        }
        catch (Exception ex)
        {
            IslandLog.Warn("MediaWindow", "动画降级: " + ex.GetType().Name);
            Finish(false);
        }
    }

    private async Task CommitFrameAsync()
    {
        try
        {
            // Render has higher priority than Loaded. Yield past this frame's render
            // work without waiting for unrelated input/background work to become idle.
            var rendered = Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            try { await rendered.Task.WaitAsync(TimeSpan.FromMilliseconds(40)); }
            catch (TimeoutException) { rendered.Abort(); }
            await PresentAsync().WaitAsync(TimeSpan.FromMilliseconds(40));
        }
        catch (Exception ex) { IslandLog.Debug("MediaWindow", "合成交接等待降级: " + ex.GetType().Name); }
    }

    private async Task WatchDeadlineAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(DurationMilliseconds + 900), token).ConfigureAwait(false);
            if (!Dispatcher.HasShutdownStarted) await Dispatcher.InvokeAsync(() => Finish(false), DispatcherPriority.Send);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }
    }
    private void PrepareTexture()
    {
        Rect window = _opening ? _to : _from;
        double scale = Math.Min(1, 1400 / window.Width);
        if (_fallbackIsland != null)
        {
            Rect island = _opening ? _from : _to;
            var transform = HwndSource.FromHwnd(_handle)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            _fallbackIsland.Width = island.Width * transform.M11; _fallbackIsland.Height = island.Height * transform.M22;
        }
        _body.Width = window.Width * scale; _body.Height = window.Height * scale;
        _body.Measure(new Size(_body.Width, _body.Height));
        _body.Arrange(new Rect(0, 0, _body.Width, _body.Height)); _body.UpdateLayout();
        _texture.Viewbox = new Rect(0, 0, _body.Width, _body.Height);
    }

    private void ApplyFrame(double progress)
    {
        Progress = progress;
        var transform = HwndSource.FromHwnd(_handle)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        NativeMethods.GetWindowRect(_handle, out var bounds);
        double viewportWidth = (bounds.Right - bounds.Left) * transform.M11;
        double viewportHeight = (bounds.Bottom - bounds.Top) * transform.M22;
        _camera.Position = new Point3D(viewportWidth / 2, -viewportHeight / 2, 10);
        _camera.LookDirection = new Vector3D(0, 0, -1); _camera.UpDirection = new Vector3D(0, 1, 0);
        _camera.Width = viewportWidth;
        Rect window = _opening ? _to : _from, island = _opening ? _from : _to;
        double travel = MediaHandoffPolicy.HandoffTravelProgress(progress, _opening);
        double closing = _opening ? 1 - travel : travel;
        Point SurfacePoint(double u, double v) => MediaHandoffPolicy.HandoffPoint(window, island, progress, _opening, u, v);
        // Detach while updating to avoid recalculating mesh bounds for every vertex.
        _surface.Geometry = null;
        for (int row = 0; row <= Rows; row++)
        for (int column = 0; column <= Columns; column++)
        {
            Point point = SurfacePoint((double)column / Columns, (double)row / Rows);
            _mesh.Positions[row * (Columns + 1) + column] = new Point3D(
                (point.X - bounds.Left) * transform.M11, -(point.Y - bounds.Top) * transform.M22, 0);
        }
        _surface.Geometry = _mesh;
        Point top = SurfacePoint(.5, 0);
        Point bottom = SurfacePoint(.5, 1);
        Point left = SurfacePoint(0, .5);
        Point right = SurfacePoint(1, .5);
        double corner = 12 + 10 * closing;
        _clip.Rect = new Rect(0, 0, _body.Width, _body.Height);
        _clip.RadiusX = Math.Min(_body.Width / 2, corner * _body.Width / (right.X - left.X));
        _clip.RadiusY = Math.Min(_body.Height / 2, corner * _body.Height / (bottom.Y - top.Y));
        // Closing hands the squeezed window directly to the live island, avoiding a
        // synthetic pill with different text, colors and geometry just before the cut.
        double blend = _opening ? MediaHandoffPolicy.Smooth((closing - .76) / .24) : 0;
        _windowImage.Opacity = _windowImage.Source == null ? 0 : 1 - blend;
        _islandContent.Opacity = _windowImage.Source == null ? 1 : blend;
    }

    private void Finish(bool completed)
    {
        if (_finished) return; _finished = true; _deadline.Cancel(); _deadline.Dispose();
        CompositionTarget.Rendering -= Render;
        _completion.TrySetResult(completed);
    }
}
