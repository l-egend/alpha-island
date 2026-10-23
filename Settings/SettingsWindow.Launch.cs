using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Services;
using CheckBox = System.Windows.Controls.CheckBox;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    private readonly StartupRegistrationService? _startupRegistration;
    private readonly Func<Task> _authorizeBootLog;
    private CheckBox _startupToggle = null!;
    private TextBlock _startupHint = null!;
    private bool _startupUpdating;
    private bool _startupActual;
    private const string StartupHint = "登录 Windows 时自动启动；首次启用会申请开机日志只读权限";

    private Grid LaunchSettingRow(string glyph, string title, string hint, CheckBox toggle, out TextBlock description)
    {
        var row = new Grid { Style = (Style)FindResource("W11Row") };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Border { Style = (Style)FindResource("W11IconTile"),
            Child = new AlphaIsland.PluginSdk.GlyphIcon { Style = (Style)FindResource("W11IconGlyph"), Text = glyph } };
        row.Children.Add(icon);
        var text = new StackPanel { Margin = new Thickness(14, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("W11RowLabel") });
        description = new TextBlock { Text = hint, Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap };
        text.Children.Add(description); Grid.SetColumn(text, 1); row.Children.Add(text);
        toggle.Style = (Style)FindResource("W11Switch"); toggle.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(toggle, 2); row.Children.Add(toggle); RegisterName(toggle.Name, toggle);
        return row;
    }

    private void AddLaunchSettings()
    {
        var startup = new StackPanel();
        startup.Children.Add(new TextBlock { Text = "启动", Style = (Style)FindResource("W11CardTitle"), Margin = new Thickness(2, 26, 0, 8) });
        _startupToggle = new CheckBox { Name = "StartupEnabledToggle", IsEnabled = _startupRegistration != null };
        var row = LaunchSettingRow("\uE7E8", "开机自启动", StartupHint, _startupToggle, out _startupHint);
        _startupToggle.Checked += Startup_Changed; _startupToggle.Unchecked += Startup_Changed;
        var startupRows = new StackPanel(); startupRows.Children.Add(SettingCard(row));
        var bootToggle = new CheckBox { Name = "StartupBootTimeToggle" };
        startupRows.Children.Add(SettingCard(LaunchSettingRow("\uE916", "开机耗时提醒", "显示开机耗时；缺少精确记录时提供参考值", bootToggle, out _)));
        bootToggle.Checked += Setting_Changed; bootToggle.Unchecked += Setting_Changed;
        startup.Children.Add(startupRows);
        Page_General.Children.Insert(1, startup);

        var clipboardRow = (Grid)ClipboardNotifyToggle.Parent;
        var clipboardRows = ClipboardSettingsRows;
        int index = clipboardRows.Children.IndexOf((UIElement)clipboardRow.Parent) + 1;
        var linkToggle = new CheckBox { Name = "ClipboardLinkToggle" };
        var linkRow = LaunchSettingRow("\uE71B", "识别剪贴板链接", "复制单个网页链接后，点击岛用默认浏览器打开", linkToggle, out _);
        linkToggle.Checked += Setting_Changed; linkToggle.Unchecked += Setting_Changed;
        clipboardRows.Children.Insert(index, SettingCard(linkRow));
    }

    private void LoadStartupRegistrationState()
    {
        if (_startupRegistration == null) return;
        try
        {
            _startupActual = _startupRegistration.IsEnabled;
            SettingsRegistry.Write(_options, "StartupEnabled", _startupActual);
            _startupToggle.IsChecked = _startupActual;
            _startupToggle.IsEnabled = true; _startupHint.Text = StartupHint;
        }
        catch (Exception ex) { _startupToggle.IsEnabled = false; _startupHint.Text = "无法读取自启动状态：" + ex.Message; }
    }

    private async void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress || _startupUpdating || _startupRegistration == null) return;
        bool enabled = _startupToggle.IsChecked == true;
        _startupUpdating = true;
        _startupToggle.IsEnabled = false;
        try
        {
            if (enabled)
            {
                _startupHint.Text = "正在申请开机日志读取权限，请完成 Windows 授权…";
                await _authorizeBootLog();
            }
            _startupRegistration.SetEnabled(enabled);
            _startupActual = enabled; _startupHint.Text = StartupHint;
            _startupToggle.IsChecked = enabled;
            SettingsRegistry.Write(_options, "StartupEnabled", enabled);
            ApplyChanges();
        }
        catch (Exception ex)
        {
            _startupToggle.IsChecked = _startupActual;
            SettingsRegistry.Write(_options, "StartupEnabled", _startupActual);
            _startupHint.Text = ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 }
                ? "已取消 Windows 授权，自启动未启用" : "修改自启动失败：" + ex.Message;
            IslandLog.Warn("Startup", "修改自启动失败：" + ex.Message);
        }
        finally { _startupUpdating = false; _startupToggle.IsEnabled = true; }
    }
}
