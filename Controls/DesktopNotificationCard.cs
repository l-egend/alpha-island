using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Core;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace alpha_island.Controls;

internal static class DesktopEventAppearance
{
    public static (string Glyph, Color Color) For(DesktopEventKind kind) => kind switch
    {
        DesktopEventKind.Volume => ("\uE995", Color.FromRgb(0x22, 0xD3, 0xEE)),
        DesktopEventKind.PowerConnected => ("\uE945", Color.FromRgb(0x34, 0xD3, 0x99)),
        DesktopEventKind.PowerDisconnected => ("\uE945", Color.FromRgb(0xFB, 0x92, 0x3C)),
        DesktopEventKind.LowBattery => ("\uE850", Color.FromRgb(0xFB, 0x92, 0x3C)),
        DesktopEventKind.CriticalBattery => ("\uE850", Color.FromRgb(0xFF, 0x55, 0x55)),
        DesktopEventKind.Locked => ("\uE72E", Color.FromRgb(0xA7, 0x8B, 0xFA)),
        DesktopEventKind.Unlocked => ("\uE785", Color.FromRgb(0xA7, 0x8B, 0xFA)),
        DesktopEventKind.System => ("\uE950", Color.FromRgb(0xFB, 0xBF, 0x24)),
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
    public DesktopNotificationCard(DesktopNotification item)
    {
        Notification = item;
        var appearance = DesktopEventAppearance.For(item.Kind);
        CornerRadius = new CornerRadius(12); Padding = new Thickness(12, 10, 12, 10); Margin = new Thickness(2, 0, 2, 7);
        Background = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255));
        BorderBrush = new SolidColorBrush(Color.FromArgb(item.IsCritical ? (byte)0x90 : (byte)0x24, appearance.Color.R, appearance.Color.G, appearance.Color.B)); BorderThickness = new Thickness(1);
        var stack = new StackPanel(); Child = stack;
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        var icon = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromArgb(0x25, appearance.Color.R, appearance.Color.G, appearance.Color.B)) };
        icon.Child = new TextBlock { Text = appearance.Glyph, FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 12, Foreground = new SolidColorBrush(appearance.Color), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        if (item.Logo is { Length: > 0 })
        {
            try
            {
                using var bytes = new MemoryStream(item.Logo);
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = bytes; bitmap.EndInit(); bitmap.Freeze();
                icon.Child = new Image { Source = bitmap, Width = 20, Height = 20 };
            }
            catch { }
        }
        header.Children.Add(icon);
        var app = Text(item.AppName, 11, 0xCA); app.VerticalAlignment = VerticalAlignment.Center; app.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(app, 1); header.Children.Add(app);
        var time = Text(item.Created.ToLocalTime().ToString("HH:mm"), 10, 0x90); time.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(time, 2); header.Children.Add(time);
        var close = ActionButton("×", () => DismissRequested?.Invoke(item)); close.Margin = new Thickness(5, 0, 0, 0); close.ToolTip = "从岛内记录移除";
        Grid.SetColumn(close, 3); header.Children.Add(close); stack.Children.Add(header);
        TitleText = Text(item.Title, 13, 0xFF); TitleText.FontWeight = FontWeights.SemiBold; TitleText.Margin = new Thickness(0, 8, 0, 3); TitleText.TextWrapping = TextWrapping.Wrap; TitleText.MaxHeight = 38;
        BodyText = Text(item.Body, 11.5, 0xCC); BodyText.TextWrapping = TextWrapping.Wrap; BodyText.MaxHeight = 65; BodyText.TextTrimming = TextTrimming.CharacterEllipsis;
        stack.Children.Add(TitleText); stack.Children.Add(BodyText);
        if (item.CanOpenApp)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 0, 0) };
            OpenButton = ActionButton("打开应用", () => OpenRequested?.Invoke(item)); actions.Children.Add(OpenButton);
            actions.Children.Add(ActionButton("屏蔽此应用", () => BlockAppRequested?.Invoke(item))); stack.Children.Add(actions);
        }
    }
    private static TextBlock Text(string value, double size, byte alpha) => new() { Text = value, FontSize = size, Foreground = new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255)) };
    internal static Button ActionButton(string title, Action action)
    {
        var button = new Button { Content = title, FontSize = 10.5, Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 6, 0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        button.SetResourceReference(StyleProperty, "ThemedButton");
        button.Click += (_, e) => { e.Handled = true; action(); }; return button;
    }
}
