using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace alpha_island.Animation;

/// <summary>Animates a border's own brush without changing shared theme resources.</summary>
public static class ControlBrushTransition
{
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.RegisterAttached(
        "Background", typeof(Brush), typeof(ControlBrushTransition), new PropertyMetadata(null, BackgroundChanged));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(TransitionState), typeof(ControlBrushTransition));

    public static Brush? GetBackground(DependencyObject target) => (Brush?)target.GetValue(BackgroundProperty);
    public static void SetBackground(DependencyObject target, Brush? value) => target.SetValue(BackgroundProperty, value);

    private static void BackgroundChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not Border border) return;
        if (border.GetValue(StateProperty) is not TransitionState state)
        {
            state = new TransitionState(border);
            border.SetValue(StateProperty, state);
        }
        state.Update(animate: true);
    }

    private sealed class TransitionState
    {
        private readonly Border _border;
        private SolidColorBrush? _brush;
        private bool _subscribed;

        internal TransitionState(Border border)
        {
            _border = border;
            border.Loaded += Loaded;
            border.Unloaded += Unloaded;
            border.IsVisibleChanged += VisibilityChanged;
            if (border.IsLoaded) Loaded(border, new RoutedEventArgs());
        }

        private void Loaded(object sender, RoutedEventArgs e)
        {
            if (!_subscribed) { MotionPolicy.Changed += Snap; _subscribed = true; }
            Snap();
        }

        private void Unloaded(object sender, RoutedEventArgs e)
        {
            if (_subscribed) MotionPolicy.Changed -= Snap;
            _subscribed = false;
            Snap();
        }

        private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Snap();
        private void Snap() => Update(animate: false);

        internal void Update(bool animate)
        {
            Brush? target = GetBackground(_border);
            if (target is not SolidColorBrush solid)
            {
                if (_brush != null) AnimationReset.Clear(_brush, SolidColorBrush.ColorProperty);
                _brush = null;
                _border.Background = target;
                return;
            }

            if (_brush == null)
            {
                _brush = new SolidColorBrush(solid.Color);
                _border.Background = _brush;
                return;
            }

            Color from = _brush.Color;
            Color to = solid.Color;
            AnimationReset.Clear(_brush, SolidColorBrush.ColorProperty);
            _brush.Color = to;
            if (!animate || from == to || !_border.IsLoaded || !_border.IsVisible
                || !SystemParameters.ClientAreaAnimation || MotionPolicy.InstantMorph || MotionPolicy.PowerSave) return;

            AnimationReset.Once(_brush, SolidColorBrush.ColorProperty,
                new ColorAnimation(from, to, TimeSpan.FromMilliseconds(100))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } }, to);
        }
    }
}
