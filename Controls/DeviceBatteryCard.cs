using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AlphaIsland.PluginSdk;
using alpha_island.Core;
using TextBlock = System.Windows.Controls.TextBlock;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;

namespace alpha_island.Controls;

internal sealed class DeviceBatteryCard : Grid
{
    private readonly StackPanel _rows = new();
    private readonly TextBlock _count = new() { FontSize = 11, Opacity = .65 };
    private readonly TextBlock _empty = new() { Text = L.T("尚未发现已连接的设备\n连接蓝牙设备或启用 2.4G 设备电量插件后自动显示"),
        TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .7, Margin = new Thickness(0, 22, 0, 0) };
    private DeviceBatterySample[] _last = [];
    internal event Action? RefreshRequested;
    internal DeviceBatteryCard()
    {
        Margin = new Thickness(24, 18, 24, 18); Visibility = Visibility.Collapsed;
        SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextPrimary");
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new());
        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = L.T("电池"), FontSize = 17, FontWeight = FontWeights.SemiBold });
        titles.Children.Add(_count); header.Children.Add(titles);
        var refresh = new Button { Content = L.T("刷新"), Padding = new Thickness(10, 5, 10, 5),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        refresh.Click += (_, _) => RefreshRequested?.Invoke(); header.Children.Add(refresh); Children.Add(header);
        var scroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); Children.Add(scroll); _rows.Children.Add(_empty);
    }
    internal void Update(IReadOnlyList<DeviceBatterySample> samples)
        => UpdateGroups(DeviceBatteryCatalog.Group(samples, DateTimeOffset.UtcNow));
    internal void UpdateGroups(IReadOnlyList<DeviceBatteryGroup> groups)
    {
        var samples = groups.SelectMany(g => g.Readings.Select(r => r.Sample)).ToArray();
        _count.Text = groups.Count == 0 ? L.T("显示已连接的蓝牙、2.4G 与手柄设备") : L.F($"已连接设备 · {groups.Count} 项");
        if (_last.SequenceEqual(samples)) return;
        _last = samples.ToArray(); _rows.Children.Clear();
        if (groups.Count == 0) { _rows.Children.Add(_empty); return; }
        foreach (var group in groups)
        {
            var sample = group.Display;
            var d = sample.Device;
            var row = new Grid { Margin = new Thickness(12, 10, 12, 10) };
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(new GlyphIcon { Text = d.Kind switch { DeviceBatteryKind.Mouse => "\uE962", DeviceBatteryKind.Keyboard => "\uE765",
                DeviceBatteryKind.Audio => "\uE7F6", DeviceBatteryKind.Gamepad => "\uE7FC", _ => "\uE83F" },
                FontSize = 20, Width = 28, VerticalAlignment = VerticalAlignment.Center });
            var labels = new StackPanel { Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(new TextBlock { Text = d.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            string transport = d.Transport switch { DeviceBatteryTransport.Bluetooth => L.T("蓝牙"), DeviceBatteryTransport.WirelessReceiver => "2.4G", _ => L.T("USB / 接收器") };
            labels.Children.Add(new TextBlock { Text = transport + (d.Charging ? L.T(" · 充电中") : ""), FontSize = 10.5, Opacity = .65, Margin = new Thickness(0, 3, 0, 0) });
            if (group.Components.Any(r => r.Part != DeviceBatteryPart.Whole))
            {
                var details = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
                foreach (var component in group.Components)
                {
                    var battery = component.Sample.Device;
                    var part = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 3) };
                    part.Children.Add(new TextBlock { Text = DeviceBatteryGroup.PartName(component.Part) + " " + (battery.Percent is int value ? value + "%" : "—"), FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center });
                    if (battery.Charging) part.Children.Add(new DeviceBatteryIndicator { Percent=battery.Percent, IsCharging=true, Margin=new Thickness(4,0,0,0) });
                    details.Children.Add(part);
                }
                labels.Children.Add(details);
            }
            Grid.SetColumn(labels, 1); row.Children.Add(labels);
            var power = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            power.Children.Add(new DeviceBatteryIndicator { Percent = d.Percent, IsCharging = d.Charging });
            power.Children.Add(new TextBlock { Text = d.Percent is int p ? (d.Estimated ? L.T("约 ") : "") + p + "%" : d.Level.Length > 0 ? d.Level : L.T("未上报"),
                Width = 48, TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = d.Percent.HasValue ? 12 : 10.5, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(power, 2); row.Children.Add(power);
            _rows.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(18, 255, 255, 255)),
                Margin = new Thickness(0, 0, 0, 7), Child = row, ToolTip = d.Detail + (d.Millivolts is int mv ? $" · {mv / 1000d:0.000} V" : "") });
        }
    }
}
