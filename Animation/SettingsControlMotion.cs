using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;

namespace alpha_island.Animation;

public static class SettingsControlMotion
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool),
        typeof(SettingsControlMotion), new PropertyMetadata(false, EnabledChanged));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(SwitchState), typeof(SettingsControlMotion));
    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);
    private static void EnabledChanged(DependencyObject obj, DependencyPropertyChangedEventArgs e)
    {
        if (obj is not CheckBox control) return;
        if (control.GetValue(StateProperty) is SwitchState old) { old.Detach(); control.ClearValue(StateProperty); }
        if ((bool)e.NewValue) control.SetValue(StateProperty, new SwitchState(control));
    }
    private sealed class SwitchState
    {
        private readonly CheckBox _control;
        private bool _subscribed;
        internal SwitchState(CheckBox control)
        {
            _control = control;
            control.Loaded += Loaded; control.Unloaded += Unloaded;
            control.IsVisibleChanged += VisibilityChanged;
            control.Checked += Changed; control.Unchecked += Changed; control.Indeterminate += Changed;
            if (control.IsLoaded) Loaded(control, new RoutedEventArgs());
        }
        private void Loaded(object sender, RoutedEventArgs e)
        { if (!_subscribed) { MotionPolicy.Changed += Snap; _subscribed = true; } Apply(false); }
        private void Unloaded(object sender, RoutedEventArgs e)
        { if (_subscribed) MotionPolicy.Changed -= Snap; _subscribed = false; Apply(false); }
        private void Snap() => Apply(false);
        private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Apply(false);
        private void Changed(object sender, RoutedEventArgs e) => Apply(_control.IsLoaded && _control.IsVisible
            && SystemParameters.ClientAreaAnimation && !MotionPolicy.InstantMorph && !MotionPolicy.PowerSave);
        private void Apply(bool animate)
        {
            _control.ApplyTemplate();
            if (_control.Template?.FindName("Knob", _control) is not Ellipse knob
                || _control.Template.FindName("Track", _control) is not Border track) return;
            if (knob.RenderTransform is not TranslateTransform) knob.RenderTransform = new TranslateTransform();
            var transform = (TranslateTransform)knob.RenderTransform;
            double from = transform.X, target = _control.IsChecked == true ? 20 : 0;
            AlphaIsland.PluginSdk.AnimationClockLifetime.Begin(transform,TranslateTransform.XProperty, null); transform.X = target;
            var color = (_control.TryFindResource(_control.IsChecked == true ? "W11AccentBrush" : "W11ControlFill") as SolidColorBrush)?.Color
                ?? (_control.IsChecked == true ? Color.FromRgb(77, 177, 255) : Color.FromRgb(51, 51, 51));
            Color fromColor = (track.Background as SolidColorBrush)?.Color ?? color;
            // 每个开关持有自己的笔刷，动画不会污染共享主题或其它开关。
            var brush = new SolidColorBrush(color); track.Background = brush;
            if (!animate) return;
            var duration = TimeSpan.FromMilliseconds(160);
            AnimationReset.Once(transform, TranslateTransform.XProperty, new DoubleAnimation(from, target, duration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }, target);
            AnimationReset.Once(brush, SolidColorBrush.ColorProperty, new ColorAnimation(fromColor, color, duration), color);
        }
        internal void Detach()
        {
            Unloaded(_control, new RoutedEventArgs());
            _control.Loaded -= Loaded; _control.Unloaded -= Unloaded;
            _control.IsVisibleChanged -= VisibilityChanged;
            _control.Checked -= Changed; _control.Unchecked -= Changed; _control.Indeterminate -= Changed;
        }
    }
}
