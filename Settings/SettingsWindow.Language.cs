using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using L = AlphaIsland.PluginSdk.Localization.UiText;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    public event EventHandler? RestartRequested;
    private System.Windows.Controls.ComboBox _interfaceLanguage = null!;
    private System.Windows.Controls.Button _restartForLanguage = null!;
    private TextBlock _languageHint = null!;

    private void AddLanguageSettings()
    {
        var row = new Grid { Style = (Style)FindResource("W11Row") };
        row.ColumnDefinitions.Add(new());
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var labels = new StackPanel { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = L.T("界面语言"), Style = (Style)FindResource("W11RowLabel") });
        _languageHint = new TextBlock { Text = L.T("重启后应用于全部界面与插件"), Style = (Style)FindResource("W11RowHint"), MaxWidth = 280 };
        labels.Children.Add(_languageHint); row.Children.Add(labels);
        var inputs = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _interfaceLanguage = new System.Windows.Controls.ComboBox
        {
            Name = "InterfaceLanguageBox", Width = 190, MinHeight = 34,
            DisplayMemberPath = "Value", SelectedValuePath = "Key",
            ItemsSource = new Dictionary<string, string>
            {
                [L.SystemLanguage] = L.T("跟随系统"), [L.Chinese] = "简体中文", [L.English] = "English"
            }
        };
        System.Windows.Automation.AutomationProperties.SetName(_interfaceLanguage, L.T("界面语言"));
        _interfaceLanguage.SelectionChanged += (_, _) =>
        {
            if (_suppress) return;
            SettingsRegistry.Write(_options, "InterfaceLanguage", L.NormalizeSelection(_interfaceLanguage.SelectedValue as string));
            ApplyChanges(); UpdateLanguageRestartHint();
        };
        RegisterName(_interfaceLanguage.Name, _interfaceLanguage);
        _restartForLanguage = new System.Windows.Controls.Button
        {
            Content = L.T("重启应用"), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed,
            Style = (Style)FindResource("W11Button"), HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        _restartForLanguage.Click += (_, _) =>
        {
            _saveDebounceTimer.Stop(); SaveOptions();
            if (!_savePending) RestartRequested?.Invoke(this, EventArgs.Empty);
        };
        inputs.Children.Add(_interfaceLanguage); inputs.Children.Add(_restartForLanguage);
        Grid.SetColumn(inputs, 1); row.Children.Add(inputs);
        var card = SettingCard(row); card.Margin = new Thickness(0, 18, 0, 0);
        Page_General.Children.Insert(1, card);
    }

    private void UpdateLanguageRestartHint()
    {
        bool pending = L.ResolveLanguage(SettingsRegistry.GetText(_options, "InterfaceLanguage", L.SystemLanguage)) != L.Language;
        _restartForLanguage.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        _languageHint.Text = pending ? L.T("语言已保存，重启后生效") : L.T("重启后应用于全部界面与插件");
    }
}
