using System.Security.Cryptography;
using System.Text;
using alpha_island.Core;
using alpha_island.Services;
using System.Windows;
using System.Windows.Controls;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    private readonly ChatService _aiConnectionService;
    private CancellationTokenSource? _aiConnectionCts;
    private int _aiConnectionVersion;
    private string _aiModelIdentity = "";
    private string _aiRequestIdentity = "";

    private ChatConfig AiConnectionConfig() => new(AiBaseUrlBox.Text.Trim(),
        !string.IsNullOrWhiteSpace(AiApiKeyBox.Password) ? AiApiKeyBox.Password.Trim()
            : _aiSecretsFile != null ? AiSecretStore.Load(_aiSecretsFile) : "",
        AiModelBox.SelectedItem as string ?? SettingsRegistry.GetText(_options, "AiModel", ""), "", .7, 1024);

    private static string AiConnectionIdentity(ChatConfig config) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(config.ModelsEndpoint + "\n" + config.ApiKey)));

    private void RefreshAiModelConnection()
    {
        string identity = AiConnectionIdentity(AiConnectionConfig());
        if (identity != _aiModelIdentity && identity != _aiRequestIdentity) ResetAiModelConnection();
        UpdateAiConnectionControls();
    }

    private void ResetAiModelConnection()
    {
        CancelAiConnection(); _aiModelIdentity = "";
        bool suppressed = _suppress; _suppress = true;
        string current = SettingsRegistry.GetText(_options, "AiModel", "");
        AiModelBox.ItemsSource = string.IsNullOrWhiteSpace(current) ? Array.Empty<string>() : new[] { current };
        AiModelBox.SelectedItem = current;
        _suppress = suppressed;
        AiConnectionStatus.Text = "请先连接接口获取模型列表";
        UpdateAiConnectionControls();
    }

    private void UpdateAiConnectionControls()
    {
        bool busy = _aiConnectionCts != null;
        bool connected = _aiModelIdentity.Length != 0;
        ConnectAiModelsButton.Content = busy ? "取消测试" : "连接并获取模型";
        AiModelBox.IsEnabled = connected && !busy;
        TestAiModelButton.IsEnabled = connected && !busy && AiModelBox.SelectedItem is string;
    }

    private void CancelAiConnection()
    {
        ++_aiConnectionVersion;
        var request = _aiConnectionCts;
        _aiConnectionCts = null; _aiRequestIdentity = "";
        request?.Cancel();
        if (ConnectAiModelsButton != null) UpdateAiConnectionControls();
    }

    private async void ConnectAiModelsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_aiConnectionCts != null) { CancelAiConnection(); AiConnectionStatus.Text = "已取消测试"; return; }
        await ConnectAiModelsAsync();
    }

    private async void TestAiModelButton_Click(object sender, RoutedEventArgs e) => await TestAiModelAsync();
    internal Task ConnectAiModelsAsync() => RunAiConnectionAsync(fetchModels: true);
    internal Task TestAiModelAsync() => RunAiConnectionAsync(fetchModels: false);

    private async Task RunAiConnectionAsync(bool fetchModels)
    {
        if (_aiConnectionCts != null) return;
        RefreshAiModelConnection();
        var config = AiConnectionConfig();
        if (!fetchModels && (_aiModelIdentity.Length == 0 || AiModelBox.SelectedItem is not string))
        { AiConnectionStatus.Text = "请先连接接口并选择模型"; return; }
        string identity = AiConnectionIdentity(config);
        int version = ++_aiConnectionVersion;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _aiConnectionCts = cancel; _aiRequestIdentity = identity;
        AiConnectionStatus.Text = fetchModels ? "正在连接并获取模型…" : "正在测试所选模型…";
        UpdateAiConnectionControls();
        try
        {
            if (fetchModels)
            {
                var models = await _aiConnectionService.ListModelsAsync(config, cancel.Token);
                if (version != _aiConnectionVersion) return;
                _aiModelIdentity = identity;
                bool suppressed = _suppress; _suppress = true;
                AiModelBox.ItemsSource = models;
                AiModelBox.SelectedItem = models.Contains(config.Model, StringComparer.Ordinal) ? config.Model : models[0];
                _suppress = suppressed;
                ApplyChanges();
                AiConnectionStatus.Text = $"已连接，获取到 {models.Count} 个模型；选择后可测试";
            }
            else
            {
                var result = await _aiConnectionService.TestModelAsync(config, cancel.Token);
                if (version != _aiConnectionVersion) return;
                AiConnectionStatus.Text = $"测试成功：{result.Model} · {result.ElapsedMilliseconds} ms";
            }
        }
        catch (OperationCanceledException)
        { if (version == _aiConnectionVersion) AiConnectionStatus.Text = "连接测试超时，请检查网络后重试"; }
        catch (Exception ex)
        {
            if (version != _aiConnectionVersion) return;
            string error = string.IsNullOrEmpty(config.ApiKey) ? ex.Message : ex.Message.Replace(config.ApiKey, "[密钥已隐藏]", StringComparison.Ordinal);
            AiConnectionStatus.Text = "测试失败：" + error;
            if (fetchModels) _aiModelIdentity = "";
        }
        finally
        {
            if (version == _aiConnectionVersion)
            { _aiConnectionCts = null; _aiRequestIdentity = ""; UpdateAiConnectionControls(); }
        }
    }

    private void AiModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        ApplyChanges(); UpdateAiConnectionControls();
        if (_aiModelIdentity.Length != 0) AiConnectionStatus.Text = "已选择模型，点击“测试模型”验证实际调用";
    }
}
