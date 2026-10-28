using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AlphaIsland.PluginSdk;
using alpha_island.Services;

namespace AlphaIsland.Plugins.ApiChat;

public partial class ApiSettingsView
{
    private ComboBox _providerPresets = null!;
    private Action? _presetModelEditorCleanup;
    private string SelectedAiModel => AiModelBox.Text.Trim();
    private bool SavedKeyMatchesEndpoint => _options.GetText("AiSecretEndpoint", "") is not { Length: > 0 } endpoint
        || AiServicePresets.SameAddress(endpoint, AiBaseUrlBox.Text);

    private void BindLegacyKeyEndpoint()
    {
        if (_options.GetText("AiSecretEndpoint", "").Length == 0 && _aiSecretsFile != null && File.Exists(_aiSecretsFile))
            _options.SetValue("AiSecretEndpoint", _options.GetText("AiBaseUrl", "https://api.deepseek.com/v1"));
    }

    private void InitializeProviderPresets()
    {
        _providerPresets = new ComboBox { Name = "AiProviderPresetBox", ItemsSource = AiServicePresets.Chat,
            DisplayMemberPath = "Name", SelectedValuePath = "Id", MinHeight = 36, FontSize = 12, MaxDropDownHeight = 330 };
        RegisterName(_providerPresets.Name, _providerPresets);
        var row = new Grid { Style = (Style)FindResource("W11Row") };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new GridLength(248) });
        row.Children.Add(new Border { Style = (Style)FindResource("W11IconTile"),
            Child = new GlyphIcon { Text = "\uE774", Style = (Style)FindResource("W11IconGlyph") } });
        var labels = new StackPanel { Margin = new Thickness(14, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = "服务商", Style = (Style)FindResource("AiLabel") });
        labels.Children.Add(new TextBlock { Text = "选择后填入接口地址", Style = (Style)FindResource("AiHint") });
        Grid.SetColumn(labels, 1); row.Children.Add(labels); Grid.SetColumn(_providerPresets, 2); row.Children.Add(_providerPresets);
        var card = (StackPanel)((Border)AiSettingsRoot.Children[1]).Child;
        card.Children.Insert(2, row); card.Children.Insert(3, new Border { Style = (Style)FindResource("W11RowDivider") });
        _providerPresets.SelectionChanged += (_, _) => ApplyProviderPreset();
        var property = DependencyPropertyDescriptor.FromProperty(ComboBox.TextProperty, typeof(ComboBox));
        EventHandler changed = (_, _) => { if (!_suppress) { ApplyChanges(); UpdateAiConnectionControls(); } };
        property.AddValueChanged(AiModelBox, changed);
        _presetModelEditorCleanup = () => property.RemoveValueChanged(AiModelBox, changed);
    }

    private void SyncProviderPreset()
    {
        bool suppressed = _suppress; _suppress = true;
        try { _providerPresets.SelectedValue = AiServicePresets.Chat.FirstOrDefault(item => item.BaseUrl.Length > 0 && AiServicePresets.SameAddress(item.BaseUrl, AiBaseUrlBox.Text))?.Id ?? "custom"; }
        finally { _suppress = suppressed; }
    }

    private void ApplyProviderPreset()
    {
        if (_suppress || _providerPresets.SelectedItem is not AiServicePreset { BaseUrl.Length: > 0 } preset) return;
        bool changed = !AiServicePresets.SameAddress(AiBaseUrlBox.Text, preset.BaseUrl);
        if (_options.GetText("AiSecretEndpoint", "").Length == 0 && _aiSecretsFile != null && File.Exists(_aiSecretsFile))
            _options.SetValue("AiSecretEndpoint", AiBaseUrlBox.Text);
        CancelAiConnection();
        _suppress = true;
        try
        {
            AiBaseUrlBox.Text = preset.BaseUrl;
            if (changed)
            {
                AiApiKeyBox.Clear(); _options.SetValue("AiModel", preset.Model);
                AiModelBox.ItemsSource = preset.Model.Length > 0 ? new[] { preset.Model } : Array.Empty<string>();
                AiModelBox.Text = preset.Model;
            }
        }
        finally { _suppress = false; }
        ResetAiModelConnection(); ApplyChanges(); RefreshAiKeyState();
        AiConnectionStatus.Text = "已填入接口地址；可获取模型或手动填写后测试。";
    }
}
