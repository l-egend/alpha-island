using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Core;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace alpha_island.Controls;

/// <summary>System events use the same icon and two-line layout as the other island pills.</summary>
internal sealed class DesktopNotificationPill : Grid
{
    internal Border IconBadge { get; }
    internal TextBlock Icon { get; }
    internal TextBlock Title { get; }
    internal TextBlock Detail { get; }

    internal DesktopNotificationPill()
    {
        Width = 328; Height = 32;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());
        Icon = new TextBlock
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        IconBadge = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Child = Icon };
        Children.Add(IconBadge);
        var text = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        Title = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        Title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        Detail = new TextBlock { FontSize = 10.5, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        Detail.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        text.Children.Add(Title); text.Children.Add(Detail);
        SetColumn(text, 1); Children.Add(text);
    }

    internal void SetNotification(DesktopNotification item)
    {
        var appearance = DesktopEventAppearance.For(item.Kind);
        Color color = item.RadioEnabled == false ? Color.FromRgb(0x94, 0xA3, 0xB8) : appearance.Color;
        Icon.Text = appearance.Glyph;
        Icon.Foreground = new SolidColorBrush(color);
        IconBadge.Background = new SolidColorBrush(Color.FromArgb(0x22, color.R, color.G, color.B));
        IconBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, color.R, color.G, color.B));
        Title.Text = item.Kind switch
        {
            DesktopEventKind.WifiRadio => "Wi-Fi",
            DesktopEventKind.BluetoothRadio => "蓝牙",
            DesktopEventKind.Message => item.AppName,
            _ => item.Title
        };
        Detail.Text = item.RadioEnabled is bool enabled ? enabled ? "已开启" : "已关闭"
            : item.Kind == DesktopEventKind.Message ? item.Title : item.Body;
        ToolTip = item.Title + "\n" + item.Body;
    }
}
