using System.IO;
using System.Windows.Controls;
using AlphaIsland.PluginSdk;
using alpha_island.Core;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    private Action? _refreshSpeechKeyStatus;
    private void BindLegacySpeechKeyEndpoint()
    {
        if (SettingsRegistry.GetText(_options, "SpeechSecretEndpoint", "").Length == 0 && File.Exists(SpeechSettings.SecretPath(SpeechSecretRoot)))
            SettingsRegistry.SetExtra(_options, "SpeechSecretEndpoint", SettingsRegistry.GetText(_options, "SpeechBaseUrl", SpeechSettings.DefaultUrl));
    }
    private void SpeechAddressChanged()
    {
        if (_suppress) return;
        BindLegacySpeechKeyEndpoint(); ApplyChanges(); _refreshSpeechKeyStatus?.Invoke();
    }
    private void SpeechProviderChanged(ComboBox choice)
    {
        if (_suppress) return;
        BindLegacySpeechKeyEndpoint();
        var preset = AiServicePresets.Speech.FirstOrDefault(item => item.Id == choice.SelectedValue as string);
        if (preset is { BaseUrl.Length: > 0 })
        {
            string previous = SettingsRegistry.GetText(_options, "SpeechBaseUrl", SpeechSettings.DefaultUrl);
            if (SettingsRegistry.GetText(_options, "SpeechSecretEndpoint", "").Length == 0 && File.Exists(SpeechSettings.SecretPath(SpeechSecretRoot)))
                SettingsRegistry.SetExtra(_options, "SpeechSecretEndpoint", previous);
            _speechSetupRequest?.Cancel();
            _suppress = true;
            try
            {
                ((TextBox)FindName("SpeechBaseUrlBox")).Text = preset.BaseUrl;
                var model = (ComboBox)FindName("SpeechModelBox"); model.ItemsSource = null; model.Text = preset.Model;
                ((PasswordBox)FindName("SpeechApiKeyBox")).Clear();
            }
            finally { _suppress = false; }
        }
        ApplyChanges(); _refreshSpeechKeyStatus?.Invoke();
        if (_speechSetupStatus != null && preset is { BaseUrl.Length: > 0 })
            _speechSetupStatus.Text = "已填入接口与模型，可修改后测试。";
    }
}
