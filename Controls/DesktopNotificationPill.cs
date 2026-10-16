using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Core;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;

namespace alpha_island.Controls;

/// <summary>System events use the same icon and two-line layout as the other island pills.</summary>
internal sealed partial class DesktopNotificationPill : Grid
{
    internal Border IconBadge { get; }
    internal SemanticIcon Icon { get; }
    internal Image IconImage { get; }
    internal Grid IconContent { get; }
    internal SemanticIcon WeatherArtwork { get; }
    private DesktopNotification? _displayed;
    internal TextBlock Title { get; }
    internal TextBlock Detail { get; }

    internal DesktopNotificationPill()
    {
        Width = 328; Height = 32;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Icon = new SemanticIcon
        {
            Width = 26, Height = 26, Shape = IconShape.Notification,
            FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        IconImage = new Image
        {
            Width = 26, Height = 26, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        IconContent = new Grid { IsHitTestVisible = false };
        IconContent.Children.Add(Icon); IconContent.Children.Add(IconImage);
        alpha_island.Animation.ActionIconMotion.SetKind(IconContent, alpha_island.Animation.ActionIconAnimation.Pulse);
        IconBadge = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Child = IconContent };
        Children.Add(IconBadge);
        var text = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        Title = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        Title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        Detail = new TextBlock { FontSize = 10.5, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        Detail.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        text.Children.Add(Title); text.Children.Add(Detail);
        SetColumn(text, 1); Children.Add(text);
        WeatherArtwork = new SemanticIcon { Width = 36, Height = 36, Shape = IconShape.Weather,
            Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed };
        SetColumn(WeatherArtwork, 2); Children.Add(WeatherArtwork);
        InitializeStack();
    }

    internal void SetNotification(DesktopNotification item)
    {
        bool changed = _displayed == null || _displayed.Key != item.Key || _displayed.Kind != item.Kind
            || _displayed.Title != item.Title || _displayed.Body != item.Body || _displayed.RadioEnabled != item.RadioEnabled
            || _displayed.WeatherCode != item.WeatherCode;
        _displayed = item;
        var appearance = DesktopEventAppearance.For(item);
        Color color = item.RadioEnabled == false ? Color.FromRgb(0xFF, 0x5C, 0x6A) : appearance.Color;
        Icon.Text = appearance.Glyph;
        Icon.Foreground = new SolidColorBrush(color);
        IconBadge.Background = new SolidColorBrush(Color.FromArgb(0x22, color.R, color.G, color.B));
        IconBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, color.R, color.G, color.B));
        IconImage.Source = DesktopNotificationIcons.GetPresentationIcon(item);
        bool hasLogo = IconImage.Source != null;
        // Keep the image as the existing logo data source; the semantic slot owns its drawing.
        IconImage.Visibility = Visibility.Collapsed;
        Icon.Visibility = Visibility.Visible;
        Icon.Source = IconImage.Source;
        Icon.Shape = ShapeFor(item.Kind);
        Icon.WeatherCode = item.Kind == DesktopEventKind.Weather ? item.WeatherCode : null;
        WeatherArtwork.Visibility = item.Kind == DesktopEventKind.Weather ? Visibility.Visible : Visibility.Collapsed;
        WeatherArtwork.WeatherCode = item.Kind == DesktopEventKind.Weather ? item.WeatherCode : null;
        WeatherArtwork.Foreground = Icon.Foreground;
        Icon.Active = item.RadioEnabled ?? (Icon.Shape == IconShape.Battery
            ? item.Kind == DesktopEventKind.PowerConnected : item.Kind != DesktopEventKind.Volume || !item.Title.Contains("静音"));
        Icon.Level = Icon.Shape is IconShape.Volume or IconShape.Battery ? ReadPercent(item.Body) : 1;
        IconBadge.CornerRadius = new CornerRadius(hasLogo ? 8 : 16);
        if (hasLogo)
        {
            IconBadge.Background = new SolidColorBrush(Color.FromArgb(0x16, 255, 255, 255));
            IconBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255));
        }
        bool qqMessage = item.Kind == DesktopEventKind.Message && MessengerIdentity.FromNotification(item) == MessengerClient.QQ
            && !string.IsNullOrWhiteSpace(item.Title) && item.Title.Trim() != "收到新消息";
        Height = qqMessage ? 34 : 32;
        Detail.FontSize = qqMessage ? 11.5 : 10.5;
        Title.Text = item.Kind switch
        {
            DesktopEventKind.WifiRadio => "Wi-Fi",
            DesktopEventKind.BluetoothRadio => "蓝牙",
            DesktopEventKind.Message => qqMessage ? OneLine(item.Title) : item.AppName,
            _ => item.Title
        };
        Detail.Text = item.RadioEnabled is bool enabled ? enabled ? "已开启" : "已关闭"
            : qqMessage ? string.IsNullOrWhiteSpace(item.Body) ? "收到一条消息" : OneLine(item.Body)
            : item.Kind == DesktopEventKind.Message ? item.PreviewText : item.Body;
        ToolTip = item.Title + "\n" + item.Body
            + (item.CanOpenApp && MessengerIdentity.FromNotification(item) is { } messenger
                ? "\n点击打开" + MessengerIdentity.Name(messenger) : "");
        if (changed)
        {
            alpha_island.Animation.ActionIconMotion.Stop(IconContent);
            alpha_island.Animation.ActionIconMotion.Play(IconContent);
        }
    }

    internal static IconShape ShapeFor(DesktopEventKind kind) => kind switch
    {
        DesktopEventKind.WifiRadio => IconShape.Wifi,
        DesktopEventKind.BluetoothRadio => IconShape.Bluetooth,
        DesktopEventKind.Volume => IconShape.Volume,
        DesktopEventKind.PowerConnected or DesktopEventKind.PowerDisconnected
            or DesktopEventKind.LowBattery or DesktopEventKind.CriticalBattery or DesktopEventKind.BluetoothLowBattery => IconShape.Battery,
        DesktopEventKind.BootTiming => IconShape.Timer,
        DesktopEventKind.Weather => IconShape.Weather,
        DesktopEventKind.Message => IconShape.Notification,
        _ => IconShape.Auto,
    };

    private static string OneLine(string value) => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static double ReadPercent(string text)
    {
        int end = text.IndexOf('%');
        if (end < 1) return 1;
        int start = end;
        while (start > 0 && char.IsAsciiDigit(text[start - 1])) start--;
        return int.TryParse(text.AsSpan(start, end - start), out int value) ? Math.Clamp(value / 100.0, 0, 1) : 1;
    }
}
