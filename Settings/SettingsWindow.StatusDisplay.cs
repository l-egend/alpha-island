using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    private StackPanel _statusDisplayRows = null!;
    private readonly Dictionary<string, Grid> _statusDisplayControls = new();
    private readonly Dictionary<string, Border> _statusDisplayCards = new();
    private string[] _statusDisplayOrder = StatusDisplayOptions.All.ToArray();
    internal void UpdateHardwarePowerState(AlphaIsland.Hardware.HardwarePowerReading reading)
    {
        if (FindName("HardwarePowerStatus") is not TextBlock state) return;
        state.Text = reading.TotalWatts is double watts ? $"硬件读取已就绪：CPU＋GPU {watts:0.0} W" : reading.Reason;
        state.ToolTip = reading.Description;
    }
    private void AddStatusDisplaySettings()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "小岛显示内容", Style = (Style)FindResource("W11CardTitle"), Margin = new Thickness(2, 26, 0, 8) });
        var group = new StackPanel();
        group.Children.Add(new TextBlock { Text = "网速与系统状态各自显示。勾选内容，用 ↑ / ↓ 调整对应小岛内的顺序；宽度随内容调整。",
            Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 12, 14, 6) });
        _statusDisplayRows = new StackPanel(); group.Children.Add(_statusDisplayRows);
        foreach (string key in StatusDisplayOptions.All)
        {
            var row = new Grid { Style = (Style)FindResource("W11Row") };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = StatusDisplayOptions.Label(key), Style = (Style)FindResource("W11RowLabel"), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(name);
            var controls = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var toggle = new CheckBox { Name = StatusDisplayOptions.SettingKey(key) + "Toggle", Style = (Style)FindResource("W11Switch"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            RegisterName(toggle.Name, toggle); toggle.Checked += Setting_Changed; toggle.Unchecked += Setting_Changed;
            controls.Children.Add(toggle);
            foreach (int direction in new[] { -1, 1 })
            {
                var move = new Button { Content = direction < 0 ? "↑" : "↓", Style = (Style)FindResource("W11Button"),
                    Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(2, 0, 0, 0), Tag = direction,
                    ToolTip = direction < 0 ? "向前显示" : "向后显示" };
                move.Click += (_, _) => MoveStatusDisplayItem(key, direction); controls.Children.Add(move);
            }
            Grid.SetColumn(controls, 1); row.Children.Add(controls); _statusDisplayControls[key] = row; _statusDisplayCards[key] = SettingCard(row);
        }
        panel.Children.Add(group);
        panel.Children.Add(new TextBlock { Text = "硬件功耗", Style = (Style)FindResource("W11CardTitle"), Margin = new Thickness(2, 20, 0, 8) });
        var power = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        power.Children.Add(new TextBlock { Text = "显示 CPU 封装＋独立 GPU 的实际功耗，接通电源也可读取。核显包含在 CPU 封装中。",
            Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap });
        power.Children.Add(new TextBlock { Text = "勾选 CPU＋GPU 功耗后自动读取，无需额外安装驱动或管理员授权。设备未提供数据时显示 —W，悬停可查看分项与原因。",
            Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) });
        var state = new TextBlock { Name = "HardwarePowerStatus", Text = "等待功耗采样；请先勾选 CPU＋GPU 功耗。", Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
        RegisterName(state.Name, state);
        power.Children.Add(state); panel.Children.Add(new Border { Style = (Style)FindResource("W11Card"), Child = power });
        Page_System.Children.Insert(1, panel);
    }
    private void LoadStatusDisplayRows()
    {
        _statusDisplayOrder = StatusDisplayOptions.Order(SettingsRegistry.GetText(_options, "StatusDisplayOrder", StatusDisplayOptions.DefaultOrder));
        RefreshStatusDisplayRows();
    }
    private void MoveStatusDisplayItem(string key, int direction)
    {
        string[] group = _statusDisplayOrder.Where(item => IsNetworkStatusItem(item) == IsNetworkStatusItem(key)).ToArray();
        int index = Array.IndexOf(group, key), neighbor = index + direction;
        if (index < 0 || neighbor < 0 || neighbor >= group.Length) return;
        int from = Array.IndexOf(_statusDisplayOrder, key), to = Array.IndexOf(_statusDisplayOrder, group[neighbor]);
        if (from < 0 || to < 0 || to >= _statusDisplayOrder.Length) return;
        (_statusDisplayOrder[from], _statusDisplayOrder[to]) = (_statusDisplayOrder[to], _statusDisplayOrder[from]);
        SettingsRegistry.SetExtra(_options, "StatusDisplayOrder", string.Join(',', _statusDisplayOrder));
        RefreshStatusDisplayRows(); ApplyChanges();
    }
    private static bool IsNetworkStatusItem(string key) => key is "upload" or "download";
    private void RefreshStatusDisplayRows()
    {
        _statusDisplayRows.Children.Clear();
        foreach (bool network in new[] { true, false })
        {
            _statusDisplayRows.Children.Add(new TextBlock
            {
                Text = network ? "网速小岛" : "系统状态小岛", Style = (Style)FindResource("W11RowHint"),
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(14, 14, 14, 4)
            });
            string[] group = _statusDisplayOrder.Where(key => IsNetworkStatusItem(key) == network).ToArray();
            for (int i = 0; i < group.Length; i++)
            {
                var row = _statusDisplayControls[group[i]];
                var controls = (StackPanel)row.Children[1];
                ((Button)controls.Children[1]).IsEnabled = i > 0;
                ((Button)controls.Children[2]).IsEnabled = i < group.Length - 1;
                _statusDisplayRows.Children.Add(_statusDisplayCards[group[i]]);
            }
        }
    }
}