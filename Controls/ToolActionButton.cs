using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AlphaIsland.PluginSdk;
using alpha_island.Animation;

using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
namespace alpha_island.Controls;

/// <summary>Compact tool tile with bounded hover/press feedback and keyboard focus.</summary>
internal sealed class ToolActionButton : Button
{
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _lift = new();
    private readonly Border _surface;
    private static SolidColorBrush Ink(byte alpha) => new(Color.FromArgb(alpha, 255, 255, 255));

    internal ToolActionButton(string title, string glyph, string? detail, Action action)
    {
        Margin = new Thickness(3); Padding = new Thickness(10, 7, 10, 7);
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Center;
        Foreground = Brushes.White; Cursor = System.Windows.Input.Cursors.Hand;
        ToolTip = detail == null ? title : title + " · " + detail;
        System.Windows.Automation.AutomationProperties.SetName(this, title);
        RenderTransformOrigin = new Point(.5, .5);
        var motion = new TransformGroup(); motion.Children.Add(_scale); motion.Children.Add(_lift); RenderTransform = motion;
        var panel = new Grid(); panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) }); panel.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new GlyphIcon { Text = glyph, Width = 20, Height = 20, FontSize = 20,
            Foreground = new SolidColorBrush(Color.FromRgb(110, 230, 245)), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(icon);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = title, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        if (detail != null) labels.Children.Add(new TextBlock { Text = detail, FontSize = 9.5, Foreground = Ink(175), Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(labels, 1); panel.Children.Add(labels);
        _surface = new Border { CornerRadius = new CornerRadius(10), Background = Ink(20), BorderBrush = Ink(30), BorderThickness = new Thickness(1), Child = panel, Padding = Padding };
        Content = _surface;
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Stretch);
        Template = new ControlTemplate(typeof(Button)) { VisualTree = presenter };
        Click += (_, _) => action();
        Loaded += (_, _) => { MotionPolicy.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => { MotionPolicy.Changed -= Refresh; ResetMotion(); };
        IsVisibleChanged += (_, _) => Refresh();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_surface != null && (e.Property == IsMouseOverProperty || e.Property == IsPressedProperty
            || e.Property == IsKeyboardFocusWithinProperty || e.Property == IsEnabledProperty)) Refresh();
    }

    private void Refresh()
    {
        bool over = IsEnabled && (IsMouseOver || IsKeyboardFocusWithin);
        _surface.Background = Ink(IsPressed ? (byte)48 : over ? (byte)34 : (byte)20);
        _surface.BorderBrush = over ? new SolidColorBrush(Color.FromArgb(115, 110, 230, 245)) : Ink(30);
        _surface.Opacity = IsEnabled ? 1 : .45;
        if (!IsLoaded || !IsVisible || MotionPolicy.InstantMorph || MotionPolicy.PowerSave || !SystemParameters.ClientAreaAnimation)
        { ResetMotion(); return; }
        double scale = IsPressed ? .97 : 1, lift = over && !IsPressed ? -1.5 : 0;
        void Move(Animatable target, DependencyProperty property, double from, double to) =>
            AnimationClockLifetime.Once(target, property, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(130))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }, to);
        Move(_scale, ScaleTransform.ScaleXProperty, _scale.ScaleX, scale);
        Move(_scale, ScaleTransform.ScaleYProperty, _scale.ScaleY, scale);
        Move(_lift, TranslateTransform.YProperty, _lift.Y, lift);
    }

    private void ResetMotion()
    {
        AnimationClockLifetime.Begin(_scale, ScaleTransform.ScaleXProperty, null);
        AnimationClockLifetime.Begin(_scale, ScaleTransform.ScaleYProperty, null);
        AnimationClockLifetime.Begin(_lift, TranslateTransform.YProperty, null);
        _scale.ScaleX = _scale.ScaleY = 1; _lift.Y = 0;
    }
}
