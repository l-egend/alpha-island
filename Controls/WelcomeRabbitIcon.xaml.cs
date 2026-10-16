using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using alpha_island.Animation;

namespace alpha_island.Controls;

/// <summary>A vector badge with unclipped ears and a finite welcome gesture.</summary>
public partial class WelcomeRabbitIcon : System.Windows.Controls.UserControl
{
    public WelcomeRabbitIcon()
    {
        InitializeComponent();
        Loaded += (_, _) => { MotionPolicy.Changed += OnMotionPolicyChanged; PlayWelcome(); };
        Unloaded += (_, _) => { MotionPolicy.Changed -= OnMotionPolicyChanged; StopWelcome(); };
        IsVisibleChanged += (_, _) => { if (IsVisible && IsLoaded) PlayWelcome(); else StopWelcome(); };
    }

    private void PlayWelcome()
    {
        StopWelcome();
        if (!IsVisible || MotionPolicy.InstantMorph || MotionPolicy.PowerSave) return;
        AnimateEar(LeftEarRotation, 5, -6);
        AnimateEar(LeftInnerRotation, 5, -6);
        AnimateEar(RightEarRotation, -5, 6);
        AnimateEar(RightInnerRotation, -5, 6);
        var blink = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1350), FillBehavior = FillBehavior.Stop };
        blink.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(650))));
        blink.KeyFrames.Add(new LinearDoubleKeyFrame(0.12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(740))));
        blink.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(850))));
        blink.Completed += (_, _) =>
        {
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(LeftEyeScale,ScaleTransform.ScaleYProperty, null);
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(RightEyeScale,ScaleTransform.ScaleYProperty, null);
        };
        AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(LeftEyeScale,ScaleTransform.ScaleYProperty, blink);
        AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(RightEyeScale,ScaleTransform.ScaleYProperty, blink);
    }

    private static void AnimateEar(RotateTransform ear, double first, double second)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1350), FillBehavior = FillBehavior.Stop };
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)), ease));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(first, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600)), ease));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(second, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(950)), ease));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1350)), ease));
        animation.Completed += (_, _) => AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(ear,RotateTransform.AngleProperty, null);
        AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(ear,RotateTransform.AngleProperty, animation);
    }

    private void StopWelcome()
    {
        foreach (var ear in new[] { LeftEarRotation, LeftInnerRotation, RightEarRotation, RightInnerRotation })
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(ear,RotateTransform.AngleProperty, null);
        AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(LeftEyeScale,ScaleTransform.ScaleYProperty, null);
        AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(RightEyeScale,ScaleTransform.ScaleYProperty, null);
    }

    private void OnMotionPolicyChanged() { if (MotionPolicy.InstantMorph || MotionPolicy.PowerSave) StopWelcome(); }
}
