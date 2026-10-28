using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using AlphaIsland.PluginSdk;

namespace alpha_island.Controls;

public sealed class WelcomeLogoIcon : FrameworkElement
{
    internal static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(WelcomeLogoIcon),
        new FrameworkPropertyMetadata(.45, FrameworkPropertyMetadataOptions.AffectsRender));
    internal static readonly TimeSpan PlaybackDuration = TimeSpan.FromSeconds(3);
    internal TimeSpan AvailableDuration { get; set; } = PlaybackDuration;
    private static readonly BitmapSource Logo = LoadLogo();
    private static readonly Geometry DisplayArea = Frozen(new EllipseGeometry(new Rect(0, 0, 32, 32)));
    private bool _presentationActive, _played, _animationEnabled = true;

    internal double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    internal bool AnimationEnabled
    {
        get => _animationEnabled;
        set
        {
            if (_animationEnabled == value) return;
            _animationEnabled = value;
            if (!value) StopAndPark();
            else TryPlay();
        }
    }

    public WelcomeLogoIcon()
    {
        Width = Height = 32;
        IsHitTestVisible = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += (_, _) => { MotionPolicy.Changed += OnMotionPolicyChanged; TryPlay(); };
        Unloaded += (_, _) => { MotionPolicy.Changed -= OnMotionPolicyChanged; StopAndPark(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) TryPlay(); else StopAndPark(); };
    }

    internal void SetPresentation(bool active)
    {
        if (_presentationActive == active) return;
        _presentationActive = active;
        if (!active) { StopAndPark(); _played = false; }
        else TryPlay();
        InvalidateVisual();
    }

    private void TryPlay()
    {
        if (!_presentationActive || _played || !IsLoaded || !IsVisible) return;
        _played = true;
        if (!AnimationEnabled || MotionPolicy.InstantMorph || MotionPolicy.PowerSave) { StopAndPark(); return; }
        Progress = 0;
        AnimationClockLifetime.Begin(this, ProgressProperty,
            new DoubleAnimation(0, 1, AvailableDuration) { FillBehavior = FillBehavior.HoldEnd });
    }

    private void StopAndPark()
    {
        AnimationClockLifetime.Begin(this, ProgressProperty, null);
        Progress = .45;
    }

    private void OnMotionPolicyChanged()
    {
        if (MotionPolicy.InstantMorph || MotionPolicy.PowerSave) StopAndPark();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        context.PushTransform(new ScaleTransform(ActualWidth / 32, ActualHeight / 32));
        context.PushClip(DisplayArea);
        if (!_presentationActive || !AnimationEnabled || MotionPolicy.InstantMorph || MotionPolicy.PowerSave)
            context.DrawImage(Logo, new Rect(2, 2, 28, 28));
        else
        {
            double progress = Math.Clamp(Progress, 0, 1);
            bool returning = progress >= .8;
            double approach = Math.Clamp(returning ? (progress - .8) / .2 : progress / .22, 0, 1);
            double flight = returning ? 0 : Math.Clamp((progress - .46) / .34, 0, 1);
            double arrival = approach * approach * (3 - 2 * approach);
            double acceleration = flight * flight;
            double x = -30 * (1 - arrival) + 30 * acceleration;
            double y = 30 * (1 - arrival) - 30 * acceleration;
            context.PushOpacity(1 - Math.Clamp((flight - .6) / .4, 0, 1));
            context.DrawImage(Logo, new Rect(2 + x, 2 + y, 28, 28));
            context.Pop();
        }
        context.Pop(); context.Pop();
    }

    private static BitmapSource LoadLogo()
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri("pack://application:,,,/alpha-island;component/Assets/alpha-island-256.png");
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    }

    private static T Frozen<T>(T value) where T : Freezable { value.Freeze(); return value; }
}
