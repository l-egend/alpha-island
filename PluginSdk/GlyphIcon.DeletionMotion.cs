using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AlphaIsland.PluginSdk;

public sealed partial class GlyphIcon
{
    private static readonly DependencyProperty DeletionProgressProperty = DependencyProperty.Register(
        "DeletionProgress", typeof(double), typeof(GlyphIcon), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly ConditionalWeakTable<GlyphIcon, ButtonBase> DeletionOwners = new();
    private static bool _deletionMotionEnabled = true;
    public double DeletionProgress => (double)GetValue(DeletionProgressProperty);
    public bool DeletionAnimating { get; private set; }

    public static void ConfigureInteractionAnimations(bool enabled)
    {
        _deletionMotionEnabled = enabled;
        if (!enabled) foreach (var pair in DeletionOwners) pair.Key.StopDeletionAnimation();
    }

    private void InitializeDeletionMotion()
    {
        Loaded += (_, _) => AttachDeletionOwner();
        Unloaded += (_, _) => { DetachDeletionOwner(); StopDeletionAnimation(); };
        IsVisibleChanged += (_, e) => { if (!(bool)e.NewValue) StopDeletionAnimation(); };
    }
    private static void OnGlyphChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        if (!Equals(e.OldValue, "\uE74D") && !Equals(e.NewValue, "\uE74D")) return;
        var icon = (GlyphIcon)owner;
        icon.DetachDeletionOwner(); icon.StopDeletionAnimation();
        if (icon.IsLoaded) icon.AttachDeletionOwner();
    }
    private void AttachDeletionOwner()
    {
        if (Text != "\uE74D" || DeletionOwners.TryGetValue(this, out _)) return;
        for (DependencyObject? node = VisualTreeHelper.GetParent(this); node is Visual; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not ButtonBase button) continue;
            DeletionOwners.Add(this, button);
            button.MouseEnter += OnDeletionHover;
            button.PreviewMouseLeftButtonDown += OnDeletionPress;
            return;
        }
    }
    private void DetachDeletionOwner()
    {
        if (!DeletionOwners.TryGetValue(this, out var button)) return;
        button.MouseEnter -= OnDeletionHover; button.PreviewMouseLeftButtonDown -= OnDeletionPress;
        DeletionOwners.Remove(this);
    }
    private void OnDeletionHover(object sender, System.Windows.Input.MouseEventArgs e) => PlayDeletionAnimation();
    private void OnDeletionPress(object sender, System.Windows.Input.MouseButtonEventArgs e) => PlayDeletionAnimation();

    public void PlayDeletionAnimation()
    {
        if (Text != "\uE74D" || !IsLoaded || !IsVisible || !IsEnabled || DeletionAnimating
            || !_deletionMotionEnabled || !SystemParameters.ClientAreaAnimation) return;
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(560), FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(560)),
            new CubicEase { EasingMode = EasingMode.EaseInOut }));
        DeletionAnimating = true;
        animation.Completed += (_, _) => StopDeletionAnimation();
        AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(this,DeletionProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
    private void StopDeletionAnimation()
    {
        AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(this,DeletionProgressProperty, null); SetValue(DeletionProgressProperty, 0d); DeletionAnimating = false;
    }
}
