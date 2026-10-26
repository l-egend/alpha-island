using System.IO;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using Button = System.Windows.Controls.Button;
using PasswordBox = System.Windows.Controls.PasswordBox;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    private FrameworkElement? _speechSettingsPanel;
    internal string? SpeechSecretRoot { get; set; }
    private static IReadOnlyDictionary<string, string>? SpeechChoices(string key) => key switch
    {
        "SpeechProvider" => new Dictionary<string, string>
        {
            ["mimo"] = "小米 MiMo ASR", ["chat-audio"] = "兼容音频对话接口", ["transcription"] = "兼容语音转写接口",
            ["local-endpoint"] = "本地语音服务", ["windows-local"] = "Windows 本机语音"
        },
        "SpeechLanguage" => new Dictionary<string, string> { ["auto"] = "自动检测", ["zh"] = "中文", ["en"] = "英文" },
        "SubtitleTargetLanguage" => new Dictionary<string, string>
        { ["zh-CN"] = "简体中文", ["en"] = "英语", ["ja"] = "日语", ["ko"] = "韩语" },
        _ => null
    };
    private void AddSpeechSettings(StackPanel rows)
    {
        var panel = new StackPanel(); _speechSettingsPanel = panel;
        panel.Children.Add(new TextBlock { Text = "语音识别与区域翻译", Style = (Style)FindResource("W11CardTitle"), Margin = new Thickness(0, 12, 0, 12) });
        foreach (var descriptor in SettingsRegistry.ByPage(SettingsPageIds.Assistant).Where(d => d.Key.StartsWith("Speech") || d.Key == "SubtitleTargetLanguage"))
            panel.Children.Add(CreateRegisteredSettingRow(descriptor));
        var key = new PasswordBox { Name = "SpeechApiKeyBox", MinHeight = 32, Margin = new Thickness(0, 0, 0, 8), MaxLength = 1024 };
        RegisterName(key.Name, key);
        System.Windows.Automation.AutomationProperties.SetName(key, "语音识别 API Key");
        var status = new TextBlock { Style = (Style)FindResource("W11RowHint"), Margin = new Thickness(0, 6, 0, 0) };
        void RefreshKeyStatus() => status.Text = File.Exists(SpeechSettings.SecretPath(SpeechSecretRoot)) ? "语音密钥已加密保存。" : "未保存密钥；本地服务可留空。";
        RefreshKeyStatus();
        var actions = new WrapPanel();
        foreach (var (label, clear) in new[] { ("保存语音密钥", false), ("清除密钥", true) })
        {
            var button = new Button { Content = label, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
            button.Name = clear ? "SpeechClearKeyButton" : "SpeechSaveKeyButton"; RegisterName(button.Name, button);
            button.Click += (_, _) =>
            {
                if (!clear && string.IsNullOrWhiteSpace(key.Password)) { status.Text = "请先输入语音识别 API Key。"; return; }
                bool saved = SpeechSettings.SaveKey(clear ? "" : key.Password.Trim(), SpeechSecretRoot);
                if (saved) { key.Clear(); RefreshKeyStatus(); }
                else status.Text = "密钥保存失败，请重试。";
                ReportSettingsSaved(saved);
            };
            actions.Children.Add(button);
        }
        var keyPanel = new StackPanel { Margin = new Thickness(12) };
        keyPanel.Children.Add(new TextBlock { Text = "语音识别 API Key", Style = (Style)FindResource("W11RowLabel"), Margin = new Thickness(0, 0, 0, 8) });
        keyPanel.Children.Add(key); keyPanel.Children.Add(actions); keyPanel.Children.Add(status);
        panel.Children.Add(SettingCard(keyPanel));
        panel.Children.Add(CreateSpeechSetupActions());
        panel.Children.Add(new TextBlock
        {
            Text = "语音识别显示原文；区域翻译需框选。关闭工具窗口后继续运行，点 × 停止。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 2, 12, 18), Opacity = .7
        });
        rows.Children.Add(panel);
    }
    internal void OpenSpeechSettings()
    {
        SelectPageByTag(SettingsPageIds.Assistant);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => _speechSettingsPanel?.BringIntoView()));
    }
}
