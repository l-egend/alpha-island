using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;

namespace alpha_island.Settings;
public partial class SettingsWindow
{
    private TextBlock? _launcherStatus;
    internal void SetLauncherStatus(string text) { if (_launcherStatus != null) _launcherStatus.Text = text; }
    private void AddWorkspaceSettings()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "快速启动与统计", Style = (Style)FindResource("W11CardTitle"), Margin = new Thickness(2, 26, 0, 8) });
        var rows = new StackPanel();
        foreach (var descriptor in SettingsRegistry.All.Where(item => item.Key.StartsWith("Launcher", StringComparison.Ordinal) || item.Key == "ProductivityEnabled"))
        {
            var row = new Grid { Style = (Style)FindResource("W11Row") };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var labels = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            labels.Children.Add(new TextBlock { Text = descriptor.Label, Style = (Style)FindResource("W11RowLabel") });
            labels.Children.Add(new TextBlock { Text = descriptor.Hint, Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap,
                MaxWidth = 350, HorizontalAlignment = System.Windows.HorizontalAlignment.Left }); row.Children.Add(labels);
            FrameworkElement input;
            if (descriptor.Kind == SettingKind.Bool)
            {
                var check = new CheckBox { Style = (Style)FindResource("W11Switch") };
                check.Checked += Setting_Changed; check.Unchecked += Setting_Changed; input = check;
            }
            else
            {
                var text = new TextBox { Style = (Style)FindResource("W11TextBox"), Width = 168, MinHeight = 27 };
                text.TextChanged += (sender, args) => Setting_Changed(sender, args); input = text;
            }
            input.Name = descriptor.ControlName; RegisterName(input.Name, input); Grid.SetColumn(input, 1); row.Children.Add(input);
            rows.Children.Add(SettingCard(row));
        }
        _launcherStatus = new TextBlock { Text = "默认 Ctrl+Alt+Space；在屏幕工具卡片也可打开启动器。", Style = (Style)FindResource("W11RowHint"), Margin = new Thickness(14, 9, 14, 12), TextWrapping = TextWrapping.Wrap };
        rows.Children.Add(_launcherStatus); panel.Children.Add(rows); Page_Behavior.Children.Add(panel);
    }
}
