using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AlphaIsland.PluginSdk;

namespace AlphaIsland.Plugins.OppoPods;

internal sealed class EarbudView : Grid
{
    private readonly EarbudController _controller;
    private readonly TextBlock _title = Text(L.T("耳机控制"), 15, true);
    private readonly TextBlock _status = Text("", 11);
    private readonly TextBlock _hint = Text("", 11);
    private readonly TextBlock _wearing = Text("", 11);
    private readonly TextBlock _info = Text("", 10);
    private readonly TextBlock[] _battery = [Text("—", 16, true), Text("—", 16, true), Text("—", 16, true)];
    private readonly DeviceBatteryIndicator[] _batteryMeters = [new(), new(), new()];
    private readonly ComboBox _device = new() { MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11, MaxDropDownHeight = 220 };
    private readonly Button _refresh, _connect, _disconnect;
    private readonly StackPanel _settings = new();
    private readonly Grid _batteryRow;
    private readonly TextBlock[] _batteryLabels = new TextBlock[3];
    private readonly StackPanel _peers = new();
    private readonly ComboBox _anc, _eq, _spatial;
    private readonly FrameworkElement _ancRow, _eqRow, _spatialRow;
    private readonly Dictionary<EarbudCommandKind, CheckBox> _toggles = new();
    private bool _updating;
    private IReadOnlyList<EarbudDevice> _lastDevices = Array.Empty<EarbudDevice>();

    internal EarbudView(EarbudController controller)
    {
        _controller = controller;
        _device.SetResourceReference(FrameworkElement.StyleProperty, typeof(ComboBox));
        Margin = new Thickness(8, 2, 8, 2);
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new());
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new()); titleRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _title.TextTrimming = TextTrimming.CharacterEllipsis; _title.Margin = new Thickness(0, 0, 10, 0);
        titleRow.Children.Add(_title); Grid.SetColumn(_status, 1); titleRow.Children.Add(_status); header.Children.Add(titleRow);
        _hint.TextWrapping = TextWrapping.Wrap; _hint.MaxHeight=36; _hint.Margin = new Thickness(0, 3, 0, 6); header.Children.Add(_hint);
        var connection = new Grid();
        connection.ColumnDefinitions.Add(new());
        for (int i = 0; i < 3; i++) connection.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        connection.Children.Add(_device);
        _refresh = Button(L.T("刷新"), () => _controller.Refresh());
        _connect = Button(L.T("连接"), () => { if (_device.SelectedItem is EarbudDevice device) _controller.Connect(device); });
        _disconnect = Button(L.T("断开"), () => _controller.Disconnect());
        foreach (var (button, column) in new[] { (_refresh, 1), (_connect, 2), (_disconnect, 3) })
        { Grid.SetColumn(button, column); connection.Children.Add(button); }
        _device.SelectionChanged += (_, _) => { if (!_updating) _connect.IsEnabled = _device.SelectedItem != null && !_controller.Snapshot.Busy; };
        AutomationProperties.SetName(_device, L.T("选择耳机"));
        header.Children.Add(connection); Children.Add(header);
        var content = new StackPanel();
        var batteryRow = _batteryRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        for (int i = 0; i < 3; i++)
        {
            batteryRow.ColumnDefinitions.Add(new());
            var tile = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            tile.Children.Add(_batteryLabels[i] = Text(new[] { L.T("左耳"), L.T("充电盒"), L.T("右耳") }[i], 11));
            var reading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center };
            _batteryMeters[i].Name = new[] { "LeftBatteryMeter", "CaseBatteryMeter", "RightBatteryMeter" }[i];
            _batteryMeters[i].VerticalAlignment = VerticalAlignment.Center;
            _batteryMeters[i].Margin = new Thickness(0, 0, 12, 0); reading.Children.Add(_batteryMeters[i]);
            reading.Children.Add(_battery[i]); tile.Children.Add(reading);
            Grid.SetColumn(tile, i); batteryRow.Children.Add(tile);
        }
        content.Children.Add(batteryRow);
        _wearing.Margin = new Thickness(0, 0, 0, 5); content.Children.Add(_wearing);
        content.Children.Add(_settings);
        (_ancRow, _anc) = ChoiceRow(L.T("降噪"), EarbudCommandKind.Anc);
        (_eqRow, _eq) = ChoiceRow("EQ", EarbudCommandKind.Eq);
        (_spatialRow, _spatial) = ChoiceRow(L.T("空间音频"), EarbudCommandKind.SpatialAudio);
        AddToggle(L.T("游戏模式"), EarbudCommandKind.GameMode);
        AddToggle(L.T("游戏音效"), EarbudCommandKind.GameSound);
        AddToggle(L.T("空间音效"), EarbudCommandKind.SpatialSound);
        AddToggle(L.T("双设备连接"), EarbudCommandKind.DualDevice);
        content.Children.Add(_peers);
        _info.TextWrapping = TextWrapping.Wrap; _info.Margin = new Thickness(0, 7, 0, 0); content.Children.Add(_info);
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false };
        Grid.SetRow(scroll, 1); Children.Add(scroll);
    }
    private static TextBlock Text(string text, double size, bool bold = false) => new()
    {
        Text = text, FontSize = size, Foreground = bold ? Brushes.White : new SolidColorBrush(Color.FromRgb(184, 198, 214)),
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center
    };
    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, FontSize = 11, Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(4, 0, 0, 0), MinWidth = 38 };
        button.SetResourceReference(FrameworkElement.StyleProperty, typeof(Button));
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => action(); return button;
    }
    private Grid Row(string label, FrameworkElement editor)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3), MinHeight = 27 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(90) }); row.ColumnDefinitions.Add(new());
        row.Children.Add(Text(label, 12)); Grid.SetColumn(editor, 1); row.Children.Add(editor);
        _settings.Children.Add(row); return row;
    }
    private (FrameworkElement Row, ComboBox Editor) ChoiceRow(string label, EarbudCommandKind kind)
    {
        var editor = new ComboBox { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 220 };
        editor.SetResourceReference(FrameworkElement.StyleProperty, typeof(ComboBox));
        AutomationProperties.SetName(editor, label);
        editor.SelectionChanged += (_, _) =>
        {
            if (_updating || editor.SelectedItem == null) return;
            string value = editor.SelectedItem is EarbudChoice choice ? choice.Key : editor.SelectedItem.ToString()!;
            _controller.Execute(new(kind, value));
        };
        return (Row(label, editor), editor);
    }
    private void AddToggle(string label, EarbudCommandKind kind)
    {
        var toggle = new CheckBox { Content = "", VerticalAlignment = VerticalAlignment.Center };
        toggle.SetResourceReference(FrameworkElement.StyleProperty, "W11Switch");
        AutomationProperties.SetName(toggle, label);
        toggle.Click += (_, _) =>
        {
            if (!_updating) _controller.Execute(new(kind, toggle.IsChecked == true ? "true" : "false"));
        };
        _toggles[kind] = toggle; Row(label, toggle);
    }
    internal void Update(EarbudSnapshot snapshot, IReadOnlyList<EarbudDevice> devices)
    {
        _batteryLabels[0].Text = snapshot.SingleBattery ? L.T("耳机电量") : L.T("左耳");
        for (int column = 1; column < 3; column++)
        {
            _batteryRow.Children[column].SetValue(VisibilityProperty, snapshot.SingleBattery ? Visibility.Collapsed : Visibility.Visible);
            _batteryRow.ColumnDefinitions[column].Width = snapshot.SingleBattery ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        }
        _updating = true;
        try
        {
            _title.Text = snapshot.DeviceName;
            _status.Text = snapshot.Status;
            _status.Foreground = snapshot.Connected ? Brushes.LightGreen : snapshot.Busy ? Brushes.LightSkyBlue : Brushes.LightGray;
            _hint.Text = snapshot.Hint;
            _hint.ToolTip = snapshot.Hint;
            if (!_lastDevices.SequenceEqual(devices))
            {
                ulong selected = (_device.SelectedItem as EarbudDevice)?.Address ?? 0;
                _lastDevices = devices.ToArray(); _device.ItemsSource = _lastDevices;
                _device.SelectedItem = _lastDevices.FirstOrDefault(d => d.Address == selected) ?? _lastDevices.FirstOrDefault();
            }
            _device.IsEnabled = !snapshot.Busy;
            _refresh.IsEnabled = !snapshot.Busy;
            _connect.IsEnabled = !snapshot.Busy && _device.SelectedItem != null;
            if(snapshot.Connected && _device.SelectedItem is EarbudDevice selectedDevice && selectedDevice.Name==snapshot.DeviceName)
                _connect.IsEnabled=false;
            _disconnect.IsEnabled = snapshot.Connected || snapshot.Busy;
            var batteries = new[] { snapshot.LeftPower, snapshot.CasePower, snapshot.RightPower };
            for (int i = 0; i < batteries.Length; i++)
            {
                _batteryMeters[i].Percent = snapshot.Connected ? batteries[i].Percent : null;
                _batteryMeters[i].IsCharging = snapshot.Connected && batteries[i].Charging;
                _battery[i].Text = _batteryMeters[i].Percent.HasValue ? _batteryMeters[i].Percent + "%" : "—";
                _battery[i].FontSize = _batteryMeters[i].Percent.HasValue ? 16 : 14;
                _battery[i].FontWeight = _batteryMeters[i].Percent.HasValue ? FontWeights.SemiBold : FontWeights.Normal;
            }
            _wearing.Text = snapshot.Wearing;
            SetChoice(_ancRow, _anc, snapshot.AncChoices, snapshot.Anc, snapshot.CanAnc, snapshot);
            SetChoice(_eqRow, _eq, snapshot.EqChoices, snapshot.Eq, snapshot.CanEq, snapshot);
            SetChoice(_spatialRow, _spatial, snapshot.SpatialChoices, snapshot.SpatialAudio, snapshot.CanSpatialAudio, snapshot);
            SetToggle(EarbudCommandKind.GameMode, snapshot.CanGameMode, snapshot.GameMode, snapshot);
            SetToggle(EarbudCommandKind.GameSound, snapshot.CanGameSound, snapshot.GameSound, snapshot);
            SetToggle(EarbudCommandKind.DualDevice, snapshot.CanDualDevice, snapshot.DualDevice, snapshot);
            SetToggle(EarbudCommandKind.SpatialSound, snapshot.CanSpatialSound, snapshot.SpatialSound, snapshot);
            UpdatePeers(snapshot);
            _info.Text = string.Join(" · ", new[] { snapshot.ModelName, snapshot.Firmware.Length > 0 ? L.T("固件 ") + snapshot.Firmware : "", snapshot.Codec }.Where(s => s.Length > 0));
        }
        finally { _updating = false; }
    }
    private static void SetChoice<T>(FrameworkElement row, ComboBox editor, IReadOnlyList<T> values, string selected, bool available, EarbudSnapshot snapshot)
    {
        row.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        // Preserve the item objects while values are unchanged, including open drop-downs.
        if (editor.ItemsSource is not IReadOnlyList<T> old || !old.SequenceEqual(values)) editor.ItemsSource = values;
        editor.SelectedItem = values.FirstOrDefault(v => (v is EarbudChoice choice ? choice.Key : v?.ToString()) == selected);
        editor.IsEnabled = snapshot.Connected && available && !snapshot.WaitingForReadback;
    }
    private void SetToggle(EarbudCommandKind kind, bool available, bool selected, EarbudSnapshot snapshot)
    {
        var toggle = _toggles[kind]; ((FrameworkElement)toggle.Parent).Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        toggle.IsChecked = selected; toggle.IsEnabled = available && snapshot.Connected && !snapshot.WaitingForReadback;
    }
    private EarbudPeer[] _lastPeers = [];
    private bool _lastPeerEnabled, _lastPriorityEnabled;
    private string _lastPriority = "";
    private void UpdatePeers(EarbudSnapshot snapshot)
    {
        bool enabled = snapshot.CanMultiControl && !snapshot.WaitingForReadback;
        bool priority = snapshot.CanPriorityControl && !snapshot.WaitingForReadback;
        if (_lastPeers.SequenceEqual(snapshot.Peers) && enabled == _lastPeerEnabled && priority == _lastPriorityEnabled && snapshot.PriorityAddress == _lastPriority) return;
        _lastPeers = snapshot.Peers; _lastPeerEnabled = enabled; _lastPriorityEnabled = priority; _lastPriority = snapshot.PriorityAddress;
        _peers.Children.Clear();
        if (snapshot.Peers.Length == 0) return;
        _peers.Children.Add(Text(L.T("已关联设备"), 12, true));
        foreach (var peer in snapshot.Peers)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var label = Text(peer.Name + (peer.IsCurrent ? L.T(" · 本机") : peer.AudioActive ? L.T(" · 正在播放") : peer.ConnectionState == 2 ? L.T(" · 已连接") : L.T(" · 未连接")), 11);
            label.TextTrimming = TextTrimming.CharacterEllipsis; row.Children.Add(label);
            var actions = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetColumn(actions, 1); row.Children.Add(actions);
            if (!peer.IsCurrent && peer.ConnectionState is 0 or 2)
            {
                var kind = peer.ConnectionState == 2 ? EarbudCommandKind.MultiDisconnect : EarbudCommandKind.MultiConnect;
                var button = Button(peer.ConnectionState == 2 ? L.T("断开") : L.T("连接"), () => _controller.Execute(new(kind, peer.Address)));
                button.IsEnabled = enabled; actions.Children.Add(button);
            }
            if (snapshot.CanPriorityControl)
            {
                var button = Button(peer.Address == snapshot.PriorityAddress ? L.T("优先") : L.T("设优先"), () => _controller.Execute(new(EarbudCommandKind.MultiPriority, peer.Address)));
                button.IsEnabled = priority; actions.Children.Add(button);
            }
            _peers.Children.Add(row);
        }
        if (snapshot.CanPriorityControl)
        {
            var button = Button(L.T("恢复自动选择"), () => _controller.Execute(new(EarbudCommandKind.AutoPriority, "")));
            button.IsEnabled = priority; button.HorizontalAlignment = HorizontalAlignment.Right; _peers.Children.Add(button);
        }
    }
}
