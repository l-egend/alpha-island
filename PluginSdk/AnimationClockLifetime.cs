using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Animation;

namespace AlphaIsland.PluginSdk;

/// <summary>Owns property clocks, including completion, replacement and cancellation.</summary>
public static class AnimationClockLifetime
{
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, long>> Versions = new();
    public static void Begin(IAnimatable? target, DependencyProperty property, AnimationTimeline? animation,
        HandoffBehavior handoff = HandoffBehavior.SnapshotAndReplace)
    {
        if (target is not DependencyObject owner) return;
        owner.VerifyAccess();
        var versions = Versions.GetOrCreateValue(owner);
        long version = versions.GetValueOrDefault(property) + 1; versions[property] = version;
        if (animation == null || animation.BeginTime == null)
        { target.BeginAnimation(property, animation, handoff); return; }
        var clock = animation.CreateClock();
        if (animation.Duration != Duration.Forever && animation.RepeatBehavior != RepeatBehavior.Forever)
            clock.Completed += (_, _) =>
            {
                if (versions.GetValueOrDefault(property) != version) return;
                object value = owner.GetValue(property);
                target.BeginAnimation(property, null);
                if (animation.FillBehavior == FillBehavior.HoldEnd) Commit(owner, property, value);
            };
        target.ApplyAnimationClock(property, clock, handoff);
    }
    private static void Commit(DependencyObject owner, DependencyProperty property, object value)
    {
        if (DependencyPropertyHelper.GetValueSource(owner, property).IsExpression) owner.SetCurrentValue(property, value);
        else owner.SetValue(property, value);
    }
    public static void Once(IAnimatable target, DependencyProperty property, AnimationTimeline animation, object finalValue)
    {
        if (target is not DependencyObject owner) throw new ArgumentException("动画目标必须是依赖对象", nameof(target));
        Begin(target, property, null); Commit(owner, property, finalValue);
        animation.FillBehavior = FillBehavior.Stop; Begin(target, property, animation);
    }
}
