using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AlphaIsland.PluginSdk;
using alpha_island.Animation;
using Size = System.Windows.Size;
using Point = System.Windows.Point;

namespace alpha_island.Controls;

internal sealed partial class DesktopNotificationStackCard
{
    private readonly TranslateTransform _contentMotion = new();

    internal void SetExpanded(bool expanded, bool animate = true)
    {
        if (IsExpanded == expanded)
        {
            if (!animate) { ++_transition; StopMotion(); FinishMotion(); }
            return;
        }
        bool wasHidden = _expansion.Visibility != Visibility.Visible;
        double height = _expansion.ActualHeight, opacity = wasHidden ? 0 : _expansion.Opacity;
        double translation = wasHidden ? 10 : _contentMotion.Y;
        double rearOpacity = MiddleCard.IsVisible ? MiddleCard.Opacity : 0;
        IsExpanded = expanded;
        if (expanded && !_built)
        { _built = true; foreach (var message in Group.Items) _messages.Children.Add(_createCard(message)); }
        _toggleHint.Text = expanded ? "收起" : "展开"; _chevron.Text = expanded ? "\uE70E" : "\uE70D";
        ToggleButton.ToolTip = expanded ? "收起这叠通知" : "展开这叠通知"; UpdateAccessibility();
        long transition = ++_transition;
        StopMotion();
        if (!animate || !IsLoaded || MotionPolicy.InstantMorph || MotionPolicy.PowerSave)
            FinishMotion();
        else
        {
            _expansion.Visibility = Visibility.Visible;
            _expansion.IsHitTestVisible = expanded;
            _expansion.RenderTransform = _contentMotion;
            _messages.Measure(new Size(Math.Max(1, ActualWidth - 8), double.PositiveInfinity));
            double limit = VisibleExpansionHeight();
            var duration = TimeSpan.FromMilliseconds(expanded ? 240 : 220);
            var reveal = new DoubleAnimation(Math.Min(height, limit), expanded ? limit : 0, duration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
            reveal.Completed += (_, _) => { if (_transition == transition) FinishMotion(); };
            AnimationClockLifetime.Once(_expansion, HeightProperty, reveal, expanded ? double.NaN : 0d);
            Animate(_expansion, OpacityProperty, opacity, expanded ? 1 : 0, duration);
            Animate(_contentMotion, TranslateTransform.YProperty, translation, expanded ? 0 : 8, duration);
            MiddleCard.Visibility = Visibility.Visible;
            RearCard.Visibility = Group.Items.Length > 2 ? Visibility.Visible : Visibility.Collapsed;
            Animate(MiddleCard, OpacityProperty, rearOpacity, expanded ? 0 : 1, duration);
            Animate(RearCard, OpacityProperty, rearOpacity, expanded ? 0 : 1, duration);
            AnimationClockLifetime.Once(this, MarginProperty,
                new ThicknessAnimation(Margin, new Thickness(2, 0, 2, expanded ? 8 : 22), duration)
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } },
                new Thickness(2, 0, 2, expanded ? 8 : 22));
        }
        ExpandedChanged?.Invoke(expanded);
    }

    // The entire list can be thousands of pixels tall. Animate the visible reveal;
    // otherwise the viewport fills in one frame and the accordion looks instantaneous.
    private double VisibleExpansionHeight()
    {
        double height = 300;
        for (DependencyObject? node = VisualTreeHelper.GetParent(this); node != null; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollViewer scroll)
            {
                double viewport = scroll.ViewportHeight > 0 ? scroll.ViewportHeight : scroll.ActualHeight;
                double top = TranslatePoint(new Point(), scroll).Y + ToggleButton.ActualHeight + 9;
                height = Math.Clamp(viewport - Math.Max(0, top), 80, Math.Max(80, viewport));
                break;
            }
        return Math.Min(_messages.DesiredSize.Height, height);
    }
    private static void Animate(IAnimatable target, DependencyProperty property, double from, double to, TimeSpan duration)
        => AnimationClockLifetime.Once(target, property, new DoubleAnimation(from, to, duration)
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } }, to);
    private void StopMotion()
    {
        AnimationClockLifetime.Begin(_expansion, HeightProperty, null);
        AnimationClockLifetime.Begin(_expansion, OpacityProperty, null);
        AnimationClockLifetime.Begin(_contentMotion, TranslateTransform.YProperty, null);
        AnimationClockLifetime.Begin(MiddleCard, OpacityProperty, null);
        AnimationClockLifetime.Begin(RearCard, OpacityProperty, null);
        AnimationClockLifetime.Begin(this, MarginProperty, null);
    }
    private void FinishMotion()
    {
        StopMotion();
        _expansion.Height = IsExpanded ? double.NaN : 0;
        _expansion.Visibility = IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        _expansion.Opacity = IsExpanded ? 1 : 0;
        _expansion.IsHitTestVisible = IsExpanded;
        _contentMotion.Y = 0; _expansion.RenderTransform = Transform.Identity;
        MiddleCard.Visibility = IsExpanded ? Visibility.Collapsed : Visibility.Visible;
        RearCard.Visibility = !IsExpanded && Group.Items.Length > 2 ? Visibility.Visible : Visibility.Collapsed;
        MiddleCard.Opacity = RearCard.Opacity = 1;
        Margin = new Thickness(2, 0, 2, IsExpanded ? 8 : 22);
    }
}
