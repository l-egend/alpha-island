using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Automation;
using AlphaIsland.PluginSdk;
using alpha_island.Animation;
using alpha_island.Core;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using Panel = System.Windows.Controls.Panel;
using Brushes = System.Windows.Media.Brushes;
using Size = System.Windows.Size;
using Point = System.Windows.Point;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace alpha_island.Controls;

/// <summary>Collapsed app cards visibly overlap; messages are built only when unfolded.</summary>
internal sealed partial class DesktopNotificationStackCard : Grid
{
    internal NotificationStackGroup Group { get; }
    internal Button ToggleButton { get; }
    internal TextBlock CountText { get; }
    internal TextBlock TimestampText { get; }
    internal Border MiddleCard { get; }
    internal Border RearCard { get; }
    internal bool IsExpanded { get; private set; }
    internal IEnumerable<DesktopNotificationCard> Cards => _messages.Children.OfType<DesktopNotificationCard>();
    internal event Action<bool>? ExpandedChanged;
    private readonly Func<DesktopNotification, DesktopNotificationCard> _createCard;
    private readonly StackPanel _messages = new();
    private readonly Border _expansion;
    private readonly TextBlock _toggleHint;
    private readonly GlyphIcon _chevron;
    private readonly Border _face;
    private const double MiddleOpacity = .82;
    private const double RearOpacity = .60;
    private long _transition;
    private bool _built;

    internal DesktopNotificationStackCard(NotificationStackGroup group, Func<DesktopNotification, DesktopNotificationCard> createCard)
    {
        if (group.Items.Length < 2) throw new ArgumentException("A visual stack needs at least two notifications.", nameof(group));
        Group = group; _createCard = createCard;
        Margin = new Thickness(2, 0, 2, 22);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RearCard = Plate(.94, 12); MiddleCard = Plate(.97, 6);
        MiddleCard.Opacity = MiddleOpacity; RearCard.Opacity = RearOpacity;
        Panel.SetZIndex(RearCard, -2); Panel.SetZIndex(MiddleCard, -1);
        Children.Add(RearCard); Children.Add(MiddleCard);
        RearCard.Visibility = group.Items.Length > 2 ? Visibility.Visible : Visibility.Collapsed;
        var face = _face = new Border { CornerRadius = new CornerRadius(17), BorderThickness = new Thickness(1) };
        face.SizeChanged += (_, _) => UpdatePlateClips();
        MiddleCard.SizeChanged += (_, _) => UpdatePlateClips(); RearCard.SizeChanged += (_, _) => UpdatePlateClips();
        Loaded += (_, _) => UpdatePlateClips();
        face.SetResourceReference(Border.BackgroundProperty, "ControlSurfaceBrush");
        face.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        Children.Add(face);
        ToggleButton = new Button { Padding = new Thickness(12), BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent,
            ToolTip = L.T("展开这叠通知"), Cursor = System.Windows.Input.Cursors.Hand };
        ToggleButton.SetResourceReference(StyleProperty, "NotificationStackButton");
        ToggleButton.Click += (_, args) => { args.Handled = true; SetExpanded(!IsExpanded); };
        face.Child = ToggleButton;
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new Grid { Width = 39, Height = 39, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        var badge = new Border { CornerRadius = new CornerRadius(10) };
        badge.SetResourceReference(Border.BackgroundProperty, "ControlHoverBrush");
        badge.Child = DesktopNotificationIcons.GetLogo(group.Latest) is { } logo
            ? new Image { Source = logo, Width = 34, Height = 34, Stretch = Stretch.Uniform }
            : new SemanticIcon { Shape = IconShape.Notification, Width = 26, Height = 26,
                Foreground = new SolidColorBrush(DesktopEventAppearance.For(group.Latest).Color),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.Children.Add(badge);
        var countBadge = new Border { CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1),
            Padding = new Thickness(5, 1, 5, 1), VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, -6, -5, 0) };
        countBadge.SetResourceReference(Border.BackgroundProperty, "ControlPopupBrush");
        countBadge.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        countBadge.Child = Label(group.Items.Length.ToString(), 10.5, "TextPrimary", FontWeights.SemiBold);
        icon.Children.Add(countBadge); header.Children.Add(icon);
        var text = new Grid(); Grid.SetColumn(text, 1); header.Children.Add(text);
        text.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        text.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        text.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new Grid(); top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(Label(group.Latest.AppName, 13, "TextPrimary", FontWeights.SemiBold));
        TimestampText = Label("", 10, "TextSecondary"); TimestampText.Margin = new Thickness(8, 1, 0, 0);
        Grid.SetColumn(TimestampText, 1); top.Children.Add(TimestampText); text.Children.Add(top);
        var summary = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        summary.ColumnDefinitions.Add(new ColumnDefinition()); summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        CountText = Label(L.F($"{group.Items.Length} 个通知"), 12.5, "TextPrimary"); summary.Children.Add(CountText);
        var affordance = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        _toggleHint = Label(L.T("展开"), 10.5, "TextSecondary"); affordance.Children.Add(_toggleHint);
        _chevron = new GlyphIcon { Text = "\uE70D", Width = 11, Height = 11, Margin = new Thickness(5, 2, 0, 0) };
        _chevron.SetResourceReference(GlyphIcon.ForegroundProperty, "TextSecondary"); affordance.Children.Add(_chevron);
        Grid.SetColumn(affordance, 1); summary.Children.Add(affordance); Grid.SetRow(summary, 1); text.Children.Add(summary);
        var preview = Label(string.Join(" · ", new[] { group.Latest.Title, group.Latest.Body }.Where(value => !string.IsNullOrWhiteSpace(value))), 10.5, "TextSecondary");
        preview.Margin = new Thickness(0, 5, 0, 0); Grid.SetRow(preview, 2); text.Children.Add(preview);
        ToggleButton.Content = header;
        _expansion = new Border { Child = _messages, Height = 0, Visibility = Visibility.Collapsed,
            ClipToBounds = true, Margin = new Thickness(6, 9, 0, 0) };
        Grid.SetRow(_expansion, 1); Children.Add(_expansion);
        Unloaded += (_, _) => { ++_transition; FinishMotion(); };
        RefreshTimestamp(DateTimeOffset.Now); UpdateAccessibility();
    }
    private static TextBlock Label(string text, double size, string resource, FontWeight? weight = null)
    {
        var label = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, resource); return label;
    }
    private static Border Plate(double scale, double offset)
    {
        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(scale, scale)); transform.Children.Add(new TranslateTransform(0, offset));
        transform.Freeze();
        var plate = new Border { CornerRadius = new CornerRadius(17), BorderThickness = new Thickness(1),
            RenderTransform = transform, RenderTransformOrigin = new Point(.5, 1), IsHitTestVisible = false };
        plate.SetResourceReference(Border.BackgroundProperty, "ControlSurfaceBrush");
        plate.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush"); return plate;
    }
    private void UpdatePlateClips()
    {
        if (_face.ActualWidth <= 0 || _face.ActualHeight <= 0) return;
        ClipPlate(MiddleCard, false); ClipPlate(RearCard, true);
    }
    private void ClipPlate(Border plate, bool rear)
    {
        if (plate.ActualWidth <= 0 || plate.ActualHeight <= 0) { plate.Clip = Geometry.Empty; return; }
        Geometry covered = ContourIn(_face, plate);
        if (rear && MiddleCard.ActualWidth > 0)
            covered = new CombinedGeometry(GeometryCombineMode.Union, covered, ContourIn(MiddleCard, plate));
        var clip = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(plate.RenderSize), plate.CornerRadius.TopLeft, plate.CornerRadius.TopLeft), covered);
        clip.Freeze(); plate.Clip = clip;
    }
    private static Geometry ContourIn(Border card, Visual target)
    {
        var transform = card.TransformToVisual(target);
        var origin = transform.Transform(new Point());
        var x = transform.Transform(new Point(1, 0)); var y = transform.Transform(new Point(0, 1));
        return new RectangleGeometry(new Rect(card.RenderSize), card.CornerRadius.TopLeft, card.CornerRadius.TopLeft)
        {
            Transform = new MatrixTransform(new Matrix(x.X - origin.X, x.Y - origin.Y, y.X - origin.X, y.Y - origin.Y, origin.X, origin.Y))
        };
    }
    private void UpdateAccessibility() => AutomationProperties.SetName(ToggleButton,
        L.F($"{Group.Latest.AppName}，{Group.Items.Length} 个通知，{(IsExpanded ? "收起" : "展开")}"));
    internal void RefreshTimestamp(DateTimeOffset now)
    {
        TimestampText.Text = NotificationTimestamp.Format(Group.Latest.Created, now);
        TimestampText.ToolTip = Group.Latest.Created.ToLocalTime().ToString(L.T("yyyy年M月d日 HH:mm:ss"));
        foreach (var card in Cards) card.RefreshTimestamp(now);
    }
}
