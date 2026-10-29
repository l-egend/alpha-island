using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using Button = System.Windows.Controls.Button;

namespace alpha_island.Settings;
public partial class SettingsWindow
{
    internal event Action<string>? AssistantToolsRequested;
    private TextBlock? _screenToolsStatus;
    private TextBlock? _voiceHotkeyStatus;
    internal void SetVoiceHotkeyStatus(string text) { if (_voiceHotkeyStatus != null) _voiceHotkeyStatus.Text = text; }
    internal void SetScreenToolsStatus(string text) { if (_screenToolsStatus != null) _screenToolsStatus.Text = text; }
    private void AddAssistantSettings()
    {
        var rows = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var (id, title) in new[] { ("voice", L.T("语音指令")), ("speech-settings", L.T("Windows 语音设置")), ("reminders", L.T("管理提醒")), ("habits", L.T("学习记录与建议")), ("health", L.T("健康详情")) })
        {
            var button = new Button { Content = title, Margin = new Thickness(0, 0, 9, 6), Padding = new Thickness(12, 6, 12, 6) };
            button.Click += (_, _) => AssistantToolsRequested?.Invoke(id); actions.Children.Add(button);
        }
        rows.Children.Add(actions);
        AddSpeechSettings(rows);
        foreach (var descriptor in SettingsRegistry.ByPage(SettingsPageIds.Assistant))
        {
            if (descriptor.Key.StartsWith("Speech") || descriptor.Key == "SubtitleTargetLanguage") continue;
            rows.Children.Add(CreateRegisteredSettingRow(descriptor));
        }
        _screenToolsStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 4, 12, 12), Opacity = .7 };
        rows.Children.Add(_screenToolsStatus);
        _voiceHotkeyStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 4, 12, 8), Opacity = .7 };
        rows.Children.Add(_voiceHotkeyStatus);
        rows.Children.Add(new TextBlock { Text = L.T("请允许麦克风访问。云端识别会上传音频。"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 12), Opacity = .7 });
        Page_Assistant.Children.Add(rows);
    }
}
