using System.IO; using System.Windows; using System.Windows.Controls; using alpha_island.Services;
namespace AlphaIsland.Plugins.ApiChat;
public partial class ApiSettingsView {
        private void ApplyAiSecret()
        {
            if (FindName("AiApiKeyBox") is not PasswordBox box)
            {
                return;
            }

            string typed = box.Password?.Trim() ?? "";
            if (typed.Length > 0 && _aiSecretsFile != null)
            {
                bool ok = AiSecretStore.TrySave(_aiSecretsFile, typed);
                if (ok)
                {
                    bool wasSuppressed = _suppress;
                    _suppress = true;
                    box.Password = string.Empty;   // 收进加密文件后立刻清掉界面明文
                    _suppress = wasSuppressed;
                    AiConnectionState.Invalidate(_options);
                    ApplyChanges();
                }
                else
                {
                    System.Diagnostics.Trace.WriteLine("API Key 保存失败（详见日志）");
                    RefreshAiKeyState();
                    AiApiKeyState.Text = "保存失败，输入已保留（检查配置目录权限）";
                    return;
                }
            }

            _options.SetValue("AiApiKey", string.Empty);
            RefreshAiKeyState();
        }

        /// <summary>刷新"密钥已保存/未保存"那行小字。</summary>
        private void RefreshAiKeyState()
        {
            if (FindName("AiApiKeyState") is TextBlock state)
            {
                state.Text = _aiSecretsFile != null && File.Exists(_aiSecretsFile)
                    ? "已加密保存（更换后点击保存）"
                    : "尚未保存（本地模型可留空）";
            }
            if (SaveAiKeyButton != null) SaveAiKeyButton.IsEnabled = _aiSecretsFile != null && !string.IsNullOrWhiteSpace(AiApiKeyBox?.Password);
            if (ClearAiKeyButton != null) ClearAiKeyButton.IsEnabled = _aiSecretsFile != null && File.Exists(_aiSecretsFile);
        }

        private void PasswordBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppress) return;
            AiConnectionState.Invalidate(_options);
            ApplyChanges();
            RefreshAiModelConnection();
            RefreshAiKeyState();
            if (AiApiKeyState != null && !string.IsNullOrWhiteSpace(AiApiKeyBox.Password)) AiApiKeyState.Text = "尚未保存，请点击保存密钥";
        }

        private void SaveAiKeyButton_Click(object sender, RoutedEventArgs e) => ApplyAiSecret();

        private void ClearAiKeyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_aiSecretsFile == null || !AiSecretStore.Clear(_aiSecretsFile)) return;
            AiApiKeyBox.Password = "";
            AiConnectionState.Invalidate(_options);
            _options.SetValue("AiApiKey", string.Empty);
            RefreshAiKeyState();
            RefreshAiModelConnection();
            ApplyChanges();
        }

}
