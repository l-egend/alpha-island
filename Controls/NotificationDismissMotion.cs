using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using alpha_island.Animation;

namespace alpha_island.Controls;

internal static class NotificationDismissMotion
{
    internal static void Play(FrameworkElement view, Action completed)
    {
        if (!view.IsVisible || view.ActualHeight <= 0 || MotionPolicy.InstantMorph || MotionPolicy.PowerSave)
        { completed(); return; }
        bool finished = false;
        var originalTransform = view.RenderTransform;
        var slide = new TranslateTransform();
        var transforms = new TransformGroup();
        if (originalTransform != null) transforms.Children.Add(originalTransform);
        transforms.Children.Add(slide); view.RenderTransform = transforms;
        var timeout = new DispatcherTimer(DispatcherPriority.Input, view.Dispatcher) { Interval = TimeSpan.FromMilliseconds(280) };
        void Finish()
        {
            if (finished) return;
            finished = true; timeout.Stop(); timeout.Tick -= Timeout;
            view.Unloaded -= Unloaded; view.IsVisibleChanged -= VisibilityChanged;
            view.BeginAnimation(UIElement.OpacityProperty, null);
            view.BeginAnimation(FrameworkElement.HeightProperty, null);
            view.BeginAnimation(FrameworkElement.MarginProperty, null);
            slide.BeginAnimation(TranslateTransform.XProperty, null);
            view.RenderTransform = originalTransform;
            completed();
        }
        void Timeout(object? sender, EventArgs args) => Finish();
        void Unloaded(object sender, RoutedEventArgs args) => Finish();
        void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) { if (!view.IsVisible) Finish(); }
        timeout.Tick += Timeout; view.Unloaded += Unloaded; view.IsVisibleChanged += VisibilityChanged;
        var duration = TimeSpan.FromMilliseconds(180);
        var fade = new DoubleAnimation(view.Opacity, 0, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => Finish();
        view.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(view.ActualHeight, 0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        view.BeginAnimation(FrameworkElement.MarginProperty, new ThicknessAnimation(view.Margin, new Thickness(0), duration));
        slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -14, duration));
        timeout.Start(); view.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
