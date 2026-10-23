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
        var rows = new StackPanel();
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var (id, title) in new[] { ("voice", "语音指令"), ("speech-settings", "Windows 语音设置"), ("reminders", "管理提醒"), ("habits", "学习记录与建议"), ("health", "健康详情") })
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
        rows.Children.Add(new TextBlock { Text = "语音唤醒使用上方所选识别引擎；需要允许桌面应用访问麦克风。云端识别会发送音频，Windows 本机语音只把识别文字提交给对话模型。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 12), Opacity = .7 });
        Page_Assistant.Children.Add(rows);
    }
}
