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
    private string[] _statusDisplayOrder = StatusDisplayOptions.All.ToArray();
    private bool _hardwarePowerSetupBusy;
    internal void UpdateHardwarePowerState(AlphaIsland.Hardware.HardwarePowerReading reading)
    {
        if (_hardwarePowerSetupBusy || FindName("HardwarePowerSetupStatus") is not TextBlock state) return;
        state.Text = reading.TotalWatts is double watts ? $"硬件读取已就绪：CPU＋GPU {watts:0.0} W" : reading.Reason;
        state.ToolTip = reading.Description;
    }
    private Func<Task<(bool Enabled, string Message)>>? _enableHardwarePower;
    internal void BindHardwarePowerActions(Func<Task<(bool Enabled, string Message)>> enable) => _enableHardwarePower = enable;
    private void AddStatusDisplaySettings()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "状态栏显示", Style = (Style)FindResource("W11CardTitle"), Margin = new Thickness(2, 26, 0, 8) });
        var group = new StackPanel();
        group.Children.Add(new TextBlock { Text = "勾选要显示的内容，用 ↑ / ↓ 调整顺序。先上后下，再从左到右；宽度随内容调整。",
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
            Grid.SetColumn(controls, 1); row.Children.Add(controls); _statusDisplayControls[key] = row;
        }
        panel.Children.Add(new Border { Style = (Style)FindResource("W11Card"), Child = group });
        panel.Children.Add(new TextBlock { Text = "硬件功耗", Style = (Style)FindResource("W11CardTitle"), Margin = new Thickness(2, 20, 0, 8) });
        var power = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        power.Children.Add(new TextBlock { Text = "显示 CPU 封装＋独立 GPU 的实际功耗，接通电源也可读取。核显包含在 CPU 封装中。",
            Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap });
        power.Children.Add(new TextBlock { Text = "首次启用会安装官方 PawnIO 2.2.0 内核驱动（系统范围），Windows 会请求管理员授权。独立读取进程负责采样，岛保持普通权限；缺少读数时显示 —W。",
            Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) });
        var startup = new CheckBox { Name = "HardwarePowerElevatedReaderToggle", Content = "启动时启用独立硬件读取（需要管理员授权）", Foreground = (System.Windows.Media.Brush)FindResource("W11TextPrimary"), Margin = new Thickness(0, 4, 0, 8) };
        RegisterName(startup.Name, startup); startup.Checked += Setting_Changed; startup.Unchecked += Setting_Changed; power.Children.Add(startup);
        var state = new TextBlock { Name = "HardwarePowerSetupStatus", Text = AlphaIsland.Hardware.HardwareDriverAccess.Description(AlphaIsland.Hardware.HardwareDriverAccess.Probe()), Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
        RegisterName(state.Name, state);
        var enable = new Button { Name = "HardwarePowerEnableButton", Content = "安装驱动并启用读取", Style = (Style)FindResource("W11Button"), HorizontalAlignment = HorizontalAlignment.Left };
        RegisterName(enable.Name, enable);
        enable.Click += async (_, _) =>
        {
            if (_enableHardwarePower == null) return;
            _hardwarePowerSetupBusy = true; enable.IsEnabled = false; state.Text = "正在准备驱动与读取进程，请完成 Windows 管理员授权…";
            try
            {
                SettingsRegistry.Write(_options, "StatusShowPower", true);
                ((CheckBox)FindName("StatusShowPowerToggle")).IsChecked = true; ApplyChanges();
                var result = await _enableHardwarePower(); state.Text = result.Message;
                if (result.Enabled) { startup.IsChecked = true; ApplyChanges(); }
            }
            catch (Exception ex) { state.Text = "硬件功耗启用失败：" + ex.Message; }
            finally { _hardwarePowerSetupBusy = false; enable.IsEnabled = true; }
        };
        power.Children.Add(enable); power.Children.Add(state);
        var download = new Button { Content = "PawnIO 官方下载与安装说明", Style = (Style)FindResource("W11Button"), HorizontalAlignment = HorizontalAlignment.Left };
        download.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/namazso/PawnIO.Setup/releases") { UseShellExecute = true }); }
            catch (Exception ex) { IslandLog.Warn("HardwarePower", "打开驱动说明失败：" + ex.Message); }
        };
        power.Children.Add(download); panel.Children.Add(new Border { Style = (Style)FindResource("W11Card"), Child = power });
        Page_System.Children.Insert(1, panel);
    }
    private void LoadStatusDisplayRows()
    {
        _statusDisplayOrder = StatusDisplayOptions.Order(SettingsRegistry.GetText(_options, "StatusDisplayOrder", StatusDisplayOptions.DefaultOrder));
        RefreshStatusDisplayRows();
    }
    private void MoveStatusDisplayItem(string key, int direction)
    {
        int from = Array.IndexOf(_statusDisplayOrder, key), to = from + direction;
        if (from < 0 || to < 0 || to >= _statusDisplayOrder.Length) return;
        (_statusDisplayOrder[from], _statusDisplayOrder[to]) = (_statusDisplayOrder[to], _statusDisplayOrder[from]);
        SettingsRegistry.SetExtra(_options, "StatusDisplayOrder", string.Join(',', _statusDisplayOrder));
        RefreshStatusDisplayRows(); ApplyChanges();
    }
    private void RefreshStatusDisplayRows()
    {
        _statusDisplayRows.Children.Clear();
        for (int i = 0; i < _statusDisplayOrder.Length; i++)
        {
            var row = _statusDisplayControls[_statusDisplayOrder[i]];
            var controls = (StackPanel)row.Children[1];
            ((Button)controls.Children[1]).IsEnabled = i > 0;
            ((Button)controls.Children[2]).IsEnabled = i < _statusDisplayOrder.Length - 1;
            _statusDisplayRows.Children.Add(row);
        }
    }
}
