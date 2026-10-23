using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using alpha_island.Controls;
using alpha_island.Core;

namespace alpha_island.Animation;

/// <summary>Shared, silent raster playback. Only presented avatars participate in one 12 fps clock.</summary>
internal static class AgentAvatarMotion
{
    private sealed record Clip(BitmapSource[] Frames, double Fps, long DecodedBytes = 0)
    {
        internal long Used;
    }
    private const int MaxCachedClips = 8;
    private const long MaxCachedBytes = 32 * 1024 * 1024;
    private static long _cacheUse;
    private static readonly Dictionary<(string Family, AgentRunState State), Clip> Clips = new();
    private static readonly ConditionalWeakTable<SemanticIcon, Track> Tracks = new();
    private static readonly HashSet<Track> Playing = new();
    private static readonly Clip SuspendedClip = CreateSuspendedClip();
    private static bool _suspended;
    private static readonly Stopwatch Time = Stopwatch.StartNew();
    private static DispatcherTimer? _timer;
    internal static bool ClockRunning => _timer?.IsEnabled == true;
    private static Clip CreateSuspendedClip()
    {
        var frame = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, new byte[4], 4);
        frame.Freeze();
        return new([frame], 1);
    }

    internal static void SetSuspended(bool suspended)
    {
        if (_suspended == suspended) return;
        _suspended = suspended;
        foreach (var pair in Tracks) pair.Value.Change(pair.Value.Family, pair.Value.State);
        // Hidden icon sources must release their cropped sheets as well as the
        // dictionary; clearing only the cache would leave every track holding it.
        if (suspended) Clips.Clear();
        SyncClock();
    }
    private static string? FamilyFor(string source, ImageSource? custom) => custom != null ? null : source switch
    {
        "antigravity" => "Gemini", "codex" => "Codex",
        "deepseek-harness" or "dsh" => "DeepSeek", "claude-code" => "Claude", _ => null
    };
    internal static bool UsesDefaultAvatar(string source, ImageSource? custom) => FamilyFor(source, custom) != null;
    internal static bool IsAssigned(SemanticIcon icon) => Tracks.TryGetValue(icon, out _);
    internal static bool HasOverflowVisual(SemanticIcon icon) => Tracks.TryGetValue(icon, out var track) && track.HasOverflowVisual;

    static AgentAvatarMotion() => MotionPolicy.Changed += () =>
    {
        foreach (var pair in Tracks) pair.Value.Refresh();
        SyncClock();
    };

    internal static void Configure(SemanticIcon icon, string source, AgentRunState state, ImageSource? custom, bool snapshotSurface = false)
    {
        icon.VerifyAccess();
        if (FamilyFor(source, custom) is not { } family) { Release(icon); icon.Source = custom; return; }
        ActionIconMotion.SetKind(icon, ActionIconAnimation.None);
        if (Tracks.TryGetValue(icon, out var existing))
        {
            if (existing.State != state || existing.Family != family) existing.Change(family, state);
            return;
        }
        var track = new Track(icon, family, state, snapshotSurface);
        Tracks.Add(icon, track);
        track.Attach();
    }

    internal static void Release(SemanticIcon icon)
    {
        if (!Tracks.TryGetValue(icon, out var track)) return;
        track.Detach(); Tracks.Remove(icon); icon.Source = null; SyncClock();
    }

    private static Clip Load(string family, AgentRunState state)
    {
        if (_suspended) return SuspendedClip;
        if (state == AgentRunState.Working) return Load(family, AgentRunState.Streaming);
        if (Clips.TryGetValue((family, state), out var clip)) { clip.Used = ++_cacheUse; return clip; }
        string name = state switch
        {
            AgentRunState.Thinking => "thinking", AgentRunState.Streaming => "streaming",
            AgentRunState.ToolWait => "tool-wait", AgentRunState.Error => "error", _ => "idle"
        };
        string root = "pack://application:,,,/alpha-island;component/Assets/Agents/" + family + "/";
        using var manifestStream = (System.Windows.Application.GetResourceStream(new Uri(root + "manifest.json"))
            ?? throw new FileNotFoundException("Agent animation manifest missing")).Stream;
        using var manifest = JsonDocument.Parse(manifestStream);
        var data = manifest.RootElement;
        int size = data.GetProperty("frameSize").GetInt32(), columns = data.GetProperty("columns").GetInt32();
        double fps = data.GetProperty("fps").GetDouble();
        var description = data.GetProperty("clips").EnumerateArray().Single(c => c.GetProperty("state").GetString() == name);
        int count = description.GetProperty("frames").GetInt32();
        if (data.GetProperty("schema").GetInt32() != 1 || size is < 16 or > 512 || columns is < 1 or > 16
            || count is < 1 or > 300 || !double.IsFinite(fps) || fps is < 1 or > 30
            || description.GetProperty("file").GetString() != name + ".png") throw new InvalidDataException("Invalid agent animation manifest");
        var sheet = new BitmapImage();
        sheet.BeginInit(); sheet.CacheOption = BitmapCacheOption.OnLoad;
        sheet.UriSource = new Uri(root + name + ".png");
        sheet.EndInit(); sheet.Freeze();
        if (sheet.PixelWidth != size * columns || sheet.PixelHeight != (count + columns - 1) / columns * size)
            throw new InvalidDataException("Agent animation frame dimensions do not match manifest");
        var frames = new BitmapSource[count];
        for (int index = 0; index < count; index++)
        {
            var frame = new CroppedBitmap(sheet, new Int32Rect(index % columns * size, index / columns * size, size, size));
            frame.Freeze(); frames[index] = frame;
        }
        var cycle = AgentAnimationLoop.Close(frames, fps,
            state is AgentRunState.Idle or AgentRunState.Thinking or AgentRunState.ToolWait);
        long bytes = (long)sheet.PixelWidth * sheet.PixelHeight * 4
            + cycle.Where(frame => frame is not CroppedBitmap).Distinct().Sum(frame => (long)frame.PixelWidth * frame.PixelHeight * 4);
        clip = new(cycle, fps, bytes) { Used = ++_cacheUse };
        Clips[(family, state)] = clip;
        while (Clips.Count > 1 && (Clips.Count > MaxCachedClips || Clips.Values.Sum(cached => cached.DecodedBytes) > MaxCachedBytes))
        {
            // Active tracks keep their own frame references; evicting an old
            // cache entry never changes the animation currently on screen.
            var oldest = Clips.Where(pair => !ReferenceEquals(pair.Value, clip)).MinBy(pair => pair.Value.Used);
            Clips.Remove(oldest.Key);
        }
        return clip;
    }

    private static void SyncClock()
    {
        if (Playing.Count == 0) { _timer?.Stop(); return; }
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background, Playing.First().Icon.Dispatcher)
            { Interval = TimeSpan.FromSeconds(1d / 12) };
            _timer.Tick += (_, _) => AdvanceAt(Time.Elapsed.TotalSeconds);
        }
        if (!_timer.IsEnabled) _timer.Start();
    }

    internal static void Advance(double seconds)
    {
        foreach (var track in Playing.ToArray()) track.Draw(seconds);
    }

    internal static void AdvanceAt(double seconds)
    {
        foreach (var track in Playing.ToArray()) track.Draw(seconds - track.StartedAt);
    }

    private sealed class Track
    {
        private static readonly DependencyPropertyDescriptor Opacity =
            DependencyPropertyDescriptor.FromProperty(UIElement.OpacityProperty, typeof(UIElement));
        internal readonly SemanticIcon Icon;
        internal AgentRunState State;
        internal string Family;
        private Clip _clip;
        private readonly List<UIElement> _ancestors = new();
        private AdornerLayer? _adornerLayer;
        private AgentAvatarAdorner? _adorner;
        internal bool HasOverflowVisual => _adorner != null;
        private int _frame = -1;
        internal double StartedAt;
        private readonly bool _snapshotSurface;
        internal Track(SemanticIcon icon, string family, AgentRunState state, bool snapshotSurface) { _snapshotSurface = snapshotSurface; Icon = icon; Family = family; State = state; _clip = Load(family, state); StartedAt = Time.Elapsed.TotalSeconds; }
        internal void Attach()
        {
            Icon.Loaded += Loaded; Icon.Unloaded += Unloaded; Icon.IsVisibleChanged += VisibilityChanged;
            Draw(0); if (Icon.IsLoaded) Observe(); Refresh();
        }
        internal void Detach()
        {
            Playing.Remove(this); Unobserve(); RemoveAdorner();
            Icon.Loaded -= Loaded; Icon.Unloaded -= Unloaded; Icon.IsVisibleChanged -= VisibilityChanged;
        }
        private void Loaded(object sender, RoutedEventArgs e) { Observe(); Refresh(); }
        private void Unloaded(object sender, RoutedEventArgs e) { Unobserve(); RemoveAdorner(); Refresh(); }
        private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Refresh();
        private void PresentationChanged(object? sender, EventArgs e) => Refresh();
        private void Observe()
        {
            Unobserve();
            for (DependencyObject? current = Icon; current != null; current = VisualTreeHelper.GetParent(current))
                if (current is UIElement element) { _ancestors.Add(element); Opacity.AddValueChanged(element, PresentationChanged); }
        }
        private void Unobserve()
        {
            foreach (var element in _ancestors) Opacity.RemoveValueChanged(element, PresentationChanged);
            _ancestors.Clear();
        }
        internal void Change(string family, AgentRunState state)
        {
            var clip = Load(family, state); State = state;
            if (Family == family && ReferenceEquals(_clip, clip)) { Refresh(); return; }
            Family = family; _clip = clip; StartedAt = Time.Elapsed.TotalSeconds; _frame = -1; Draw(0); Refresh();
        }
        internal void Refresh()
        {
            bool presented = !_suspended && Icon.IsLoaded && Icon.IsVisible && _ancestors.Count > 0
                && _ancestors.All(e => e.Opacity > .1 || _snapshotSurface && e is Window);
            if (!_snapshotSurface && Icon.IsLoaded && _adorner == null && AdornerLayer.GetAdornerLayer(Icon) is { } layer)
            {
                _adornerLayer = layer;
                _adorner = new AgentAvatarAdorner(Icon, () => _ancestors.Aggregate(1d, (value, element) => value * element.Opacity));
                layer.Add(_adorner); Icon.InvalidateVisual();
            }
            if (_adorner != null)
            {
                _adorner.Visibility = presented ? Visibility.Visible : Visibility.Collapsed;
                _adorner.InvalidateVisual();
            }
            if (presented && !MotionPolicy.InstantMorph && !MotionPolicy.PowerSave)
            {
                if (Playing.Add(this)) StartedAt = Time.Elapsed.TotalSeconds;
            }
            else { Playing.Remove(this); Draw(0); }
            SyncClock();
        }
        internal void Draw(double seconds)
        {
            int index = (int)(Math.Max(0, seconds) * _clip.Fps) % _clip.Frames.Length;
            if (_frame == index) return;
            _frame = index; Icon.Source = _clip.Frames[index]; _adorner?.InvalidateVisual();
        }
        private void RemoveAdorner()
        {
            if (_adorner != null) _adornerLayer?.Remove(_adorner);
            _adorner = null; _adornerLayer = null; Icon.InvalidateVisual();
        }
    }
}
