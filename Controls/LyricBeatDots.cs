using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace alpha_island.Controls;

/// <summary>由现有歌词播放时钟驱动，不注册额外计时器或渲染回调。</summary>
public sealed class LyricBeatDots : FrameworkElement
{
    private const double CycleSeconds = 2.4;
    private const double DotDelaySeconds = .32;
    private const double BounceSeconds = .96;
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(LyricBeatDots),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    private double _frameSeconds, _energy;
    internal bool IsMoving { get; private set; }
    internal double FrameSeconds => _frameSeconds;

    public LyricBeatDots()
    {
        IsHitTestVisible = false;
        System.Windows.Automation.AutomationProperties.SetName(this, "前奏或间奏");
    }

    internal void SetFrame(TimeSpan position, bool moving, float energy = 0)
    {
        double seconds = moving ? Math.Floor(Math.Max(0, position.TotalSeconds) * 30) / 30 : 0;
        double level = moving && float.IsFinite(energy) ? Math.Clamp(energy, 0, 1) : 0;
        bool changed = seconds != _frameSeconds || IsMoving != moving || Math.Abs(level - _energy) > .04;
        _frameSeconds = seconds; _energy = level; IsMoving = moving;
        if (changed && IsVisible) InvalidateVisual();
    }

    internal (double Offset, double Opacity) DotPose(int index)
    {
        if (index is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(index));
        bool outside = index is 0 or 3;
        if (!IsMoving) return (0, outside ? .56 : .8);
        double localTime = (_frameSeconds - index * DotDelaySeconds) % CycleSeconds;
        if (localTime < 0) localTime += CycleSeconds;
        double pulse = localTime < BounceSeconds ? .5 - .5 * Math.Cos(localTime / BounceSeconds * Math.Tau) : 0;
        return (-pulse * (1.7 + _energy * .45), .56 + pulse * .3);
    }

    protected override Size MeasureOverride(Size availableSize) => new(double.IsNaN(Width) ? 48 : Width, double.IsNaN(Height) ? 22 : Height);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double radius = Math.Min(3.4, ActualHeight * .17), step = Math.Min(13, (ActualWidth - 2 * radius) / 3);
        if (radius <= 0 || step <= 0) return;
        for (int index = 0; index < 4; index++)
        {
            var pose = DotPose(index);
            dc.PushOpacity(pose.Opacity);
            dc.DrawEllipse(Foreground, null, new Point(ActualWidth / 2 + (index - 1.5) * step, ActualHeight / 2 + pose.Offset), radius, radius);
            dc.Pop();
        }
    }
}
