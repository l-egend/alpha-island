using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Core;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using TextBox = System.Windows.Controls.TextBox;

namespace alpha_island.Controls;

internal static class DesktopEventAppearance
{
    public static (string Glyph, Color Color) For(DesktopNotification item)
    {
        var appearance = For(item.Kind);
        if (item.Kind != DesktopEventKind.Weather) return appearance;
        var color = WeatherIconPolicy.Resolve(item.WeatherCode) switch
        {
            WeatherIconKind.Sunny or WeatherIconKind.PartlyCloudy => Color.FromRgb(0xFB, 0xBF, 0x24),
            WeatherIconKind.Thunderstorm => Color.FromRgb(0xC4, 0xB5, 0xFD),
            WeatherIconKind.Snow or WeatherIconKind.FreezingRain => Color.FromRgb(0xBA, 0xE6, 0xFD),
            WeatherIconKind.Cloudy or WeatherIconKind.Fog or WeatherIconKind.Unknown => Color.FromRgb(0xCB, 0xD5, 0xE1),
            _ => appearance.Color
        };
        return (appearance.Glyph, color);
    }

    public static (string Glyph, Color Color) For(DesktopEventKind kind) => kind switch
    {
        DesktopEventKind.Volume => ("\uE995", Color.FromRgb(0x22, 0xD3, 0xEE)),
        DesktopEventKind.PowerConnected => ("\uE945", Color.FromRgb(0x34, 0xD3, 0x99)),
        DesktopEventKind.PowerDisconnected => ("\uE945", Color.FromRgb(0xFB, 0x92, 0x3C)),
        DesktopEventKind.LowBattery => ("\uE850", Color.FromRgb(0xFB, 0x92, 0x3C)),
        DesktopEventKind.BluetoothLowBattery => ("\uE850", Color.FromRgb(0xFB, 0x92, 0x3C)),
        DesktopEventKind.CriticalBattery => ("\uE850", Color.FromRgb(0xFF, 0x55, 0x55)),
        DesktopEventKind.Locked => ("\uE72E", Color.FromRgb(0xA7, 0x8B, 0xFA)),
        DesktopEventKind.Unlocked => ("\uE785", Color.FromRgb(0xA7, 0x8B, 0xFA)),
        DesktopEventKind.System => ("\uE950", Color.FromRgb(0xFB, 0xBF, 0x24)),
        DesktopEventKind.BootTiming => ("\uE823", Color.FromRgb(0x60, 0xA5, 0xFA)),
        DesktopEventKind.Weather => ("\uE9BD", Color.FromRgb(0x38, 0xBD, 0xF8)),
        DesktopEventKind.WifiRadio => ("\uE701", Color.FromRgb(0x60, 0xA5, 0xFA)),
        DesktopEventKind.BluetoothRadio => ("\uE702", Color.FromRgb(0xA7, 0x8B, 0xFA)),
        _ => ("\uE8BD", Color.FromRgb(0x60, 0xA5, 0xFA))
    };
}

public sealed class DesktopNotificationCard : Border
{
    public event Action<DesktopNotification>? OpenRequested;
    public event Action<DesktopNotification>? DismissRequested;
    public event Action<DesktopNotification>? BlockAppRequested;
    public DesktopNotification Notification { get; }
    internal TextBlock TitleText { get; }
    internal TextBlock BodyText { get; }
    internal Button? OpenButton { get; }
    internal TextBlock TimestampText { get; }
    internal Button DismissButton { get; }
    private readonly TextBox _fullText;
    private readonly Border _fullTextPanel;
    private readonly Button _fullTextButton;
    private readonly StackPanel _summary;
    internal bool FullContentExpanded => _fullText.Visibility == Visibility.Visible;
    public DesktopNotificationCard(DesktopNotification item)
    {
        Notification = item;
        var appearance = DesktopEventAppearance.For(item);
        CornerRadius = new CornerRadius(12); Padding = new Thickness(12, 10, 12, 10); Margin = new Thickness(2, 0, 2, 7);
        Background = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255));
        BorderBrush = new SolidColorBrush(Color.FromArgb(item.IsCritical ? (byte)0x90 : (byte)0x24, appearance.Color.R, appearance.Color.G, appearance.Color.B)); BorderThickness = new Thickness(1);
        var stack = new StackPanel(); Child = stack;
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        var icon = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromArgb(0x25, appearance.Color.R, appearance.Color.G, appearance.Color.B)) };
        icon.Child = new AlphaIsland.PluginSdk.GlyphIcon { Text = appearance.Glyph, FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 14, Foreground = new SolidColorBrush(appearance.Color), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        if (item.Kind == DesktopEventKind.Weather)
            icon.Child = new SemanticIcon { Width = 20, Height = 20, Shape = IconShape.Weather, WeatherCode = item.WeatherCode,
                Foreground = new SolidColorBrush(appearance.Color), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        if (DesktopNotificationIcons.GetLogo(item) is { } logo)
            icon.Child = new Image { Source = logo, Width = 20, Height = 20, Stretch = Stretch.Uniform };
        header.Children.Add(icon);
        var app = Text(item.AppName, 11, 0xCA); app.VerticalAlignment = VerticalAlignment.Center; app.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(app, 1); header.Children.Add(app);
        var time = TimestampText = Text(NotificationTimestamp.Format(item.Created, DateTimeOffset.Now), 10.5, 0xB0);
        time.VerticalAlignment = VerticalAlignment.Center; time.Margin = new Thickness(8, 0, 7, 0);
        time.ToolTip = item.Created.ToLocalTime().ToString("yyyy年M月d日 HH:mm:ss");
        Grid.SetColumn(time, 2); header.Children.Add(time);
        var close = DismissButton = new Button { Width = 28, Height = 28, Padding = new Thickness(0),
            Content = new AlphaIsland.PluginSdk.GlyphIcon { Text = "\uE74D", Width = 18, Height = 18,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            ToolTip = "从岛内记录移除" };
        close.SetResourceReference(StyleProperty, "DeletionIconButton");
        System.Windows.Automation.AutomationProperties.SetName(close, "移除通知");
        close.Click += (_, e) => { e.Handled = true; DismissRequested?.Invoke(item); };
        Grid.SetColumn(close, 3); header.Children.Add(close); stack.Children.Add(header);
        bool hasTitle = !string.IsNullOrWhiteSpace(item.Title);
        bool hasBody = !string.IsNullOrWhiteSpace(item.Body);
        TitleText = Text(hasTitle ? item.Title.Trim() : "", 13, 0xFF); TitleText.FontWeight = FontWeights.SemiBold; TitleText.Margin = new Thickness(0, 8, 0, 3); TitleText.TextWrapping = TextWrapping.Wrap; TitleText.MaxHeight = 38;
        TitleText.Visibility = hasTitle ? Visibility.Visible : Visibility.Collapsed;
        BodyText = Text(hasBody ? item.Body.Trim() : hasTitle ? "" : item.PreviewText, 11.5, 0xCC);
        BodyText.Visibility = hasBody || !hasTitle ? Visibility.Visible : Visibility.Collapsed;
        BodyText.Margin = new Thickness(0, hasTitle ? 0 : 8, 0, 0);
        BodyText.TextWrapping = TextWrapping.Wrap; BodyText.MaxHeight = 65; BodyText.TextTrimming = TextTrimming.CharacterEllipsis;
        var content = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        _summary = new StackPanel();
        _summary.Children.Add(TitleText); _summary.Children.Add(BodyText);
        Grid.SetColumn(_summary, 1); content.Children.Add(_summary);
        if (DesktopNotificationIcons.GetContentImage(item) is { } contentImage)
        {
            var image = new Image { Source = contentImage, Stretch = Stretch.Uniform,
                Width = item.ContentImageIsHero ? 160 : 48, Height = item.ContentImageIsHero ? 96 : 48,
                VerticalAlignment = VerticalAlignment.Top,
                Clip = new RectangleGeometry(new Rect(0, 0, item.ContentImageIsHero ? 160 : 48,
                    item.ContentImageIsHero ? 96 : 48), 9, 9) };
            if (item.ContentImageIsHero) stack.Children.Add(image);
            else
            {
                image.Margin = new Thickness(0, 8, 12, 0); content.Children.Add(image);
            }
        }
        stack.Children.Add(content);
        _fullText = new TextBox
        {
            Text = string.Join("\n\n", new[] { item.Title, item.Body }.Where(value => !string.IsNullOrWhiteSpace(value))),
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Height = 120, FontSize = 12,
            Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0),
            Padding = new Thickness(2), Visibility = Visibility.Collapsed, IsReadOnlyCaretVisible = true
        };
        _fullText.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextPrimary");
        _fullTextPanel = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 255, 255, 255)), Child = _fullText,
            Margin = new Thickness(0, 5, 0, 0), Visibility = Visibility.Collapsed };
        _fullTextButton = ActionButton("查看全文", () => SetFullContentExpanded(!FullContentExpanded));
        _fullTextButton.Margin = new Thickness(0, 0, 8, 0);
        // Keep the body image alongside the full reader, not just its clipped preview.
        Grid.SetColumn(_fullTextPanel, 1); content.Children.Add(_fullTextPanel);
        _fullTextButton.FontSize = 12;
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 6) };
        toolbar.Children.Add(_fullTextButton);
        stack.Children.Insert(1, toolbar);
        if (item.CanOpenApp)
        {
            OpenButton = ActionButton("打开应用", () => OpenRequested?.Invoke(item)); toolbar.Children.Add(OpenButton);
            toolbar.Children.Add(ActionButton("屏蔽此应用", () => BlockAppRequested?.Invoke(item)));
        }
        else if (item.Kind == DesktopEventKind.Weather)
        {
            OpenButton = ActionButton("查看天气", () => OpenRequested?.Invoke(item)); toolbar.Children.Add(OpenButton);
        }
    }
    internal void RefreshTimestamp(DateTimeOffset now) => TimestampText.Text = NotificationTimestamp.Format(Notification.Created, now);
    internal void SetFullContentExpanded(bool expanded)
    {
        _fullText.Visibility = _fullTextPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        _summary.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        _fullTextButton.Content = expanded ? "收起全文" : "查看全文";
    }
    private static TextBlock Text(string value, double size, byte alpha)
    {
        var text = new TextBlock { Text = value, FontSize = size, Opacity = alpha / 255d };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary"); return text;
    }
    internal static Button ActionButton(string title, Action action)
    {
        var button = new Button { Content = title, FontSize = 10.5, Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 6, 0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        button.SetResourceReference(StyleProperty, "ThemedButton");
        if (title == "清空记录") button.Content = new StackPanel { Orientation = Orientation.Horizontal,
            Children = { new AlphaIsland.PluginSdk.GlyphIcon { Text = "\uE74D", Width = 16, Height = 16, Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center } } };
        button.Click += (_, e) => { e.Handled = true; action(); }; return button;
    }
}
