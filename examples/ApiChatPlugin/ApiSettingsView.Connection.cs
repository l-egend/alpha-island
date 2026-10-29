using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Security.Cryptography;
using System.Text;
using alpha_island.Conversations;
using alpha_island.Services;
using System.Windows;
using System.Windows.Controls;

namespace AlphaIsland.Plugins.ApiChat;

public partial class ApiSettingsView
{
    private readonly ChatService _aiConnectionService;
    private CancellationTokenSource? _aiConnectionCts;
    private int _aiConnectionVersion;
    private string _aiModelIdentity = "";
    private string _aiRequestIdentity = "";

    private void SyncAiAutomaticState()
    {
        bool ready = AiConnectionState.Update(_options, _aiSecretsFile);
        bool suppressed = _suppress; _suppress = true;
        AiEnabledToggle.IsChecked = ready;
        OpenAiConversationButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        _suppress = suppressed;
    }

    private bool CommitAiConnection(ChatConfig config)
    {
        if (config.HasKey && !string.IsNullOrWhiteSpace(AiApiKeyBox.Password))
        {
            if (_aiSecretsFile == null || !AiSecretStore.TrySave(_aiSecretsFile, config.ApiKey))
            {
                AiConnectionState.Invalidate(_options); ApplyChanges();
                AiConnectionStatus.Text = L.T("接口已连接，但密钥保存失败，未启用对话");
                return false;
            }
            bool suppressed = _suppress; _suppress = true;
            AiApiKeyBox.Password = "";
            _suppress = suppressed;
            _options.SetValue("AiSecretEndpoint", AiBaseUrlBox.Text.Trim());
            RefreshAiKeyState();
        }
        ApplyChanges();
        AiConnectionState.Verify(_options, _aiSecretsFile, config);
        ApplyChanges();
        return true;
    }

    private ChatConfig AiConnectionConfig() => new(AiBaseUrlBox.Text.Trim(),
        !string.IsNullOrWhiteSpace(AiApiKeyBox.Password) ? AiApiKeyBox.Password.Trim()
            : _aiSecretsFile != null && SavedKeyMatchesEndpoint ? AiSecretStore.Load(_aiSecretsFile) : "",
        SelectedAiModel, "", .7, 1024);

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
        string current = _options.GetText("AiModel", "");
        AiModelBox.ItemsSource = string.IsNullOrWhiteSpace(current) ? Array.Empty<string>() : new[] { current };
        AiModelBox.SelectedItem = current;
        _suppress = suppressed;
        AiConnectionStatus.Text = L.T("获取模型，或填写后测试");
        UpdateAiConnectionControls();
    }

    private void UpdateAiConnectionControls()
    {
        bool busy = _aiConnectionCts != null;
        ConnectAiModelsButton.Content = busy ? L.T("取消测试") : L.T("连接并获取模型");
        AiModelBox.IsEnabled = !busy;
        TestAiModelButton.IsEnabled = !busy && SelectedAiModel.Length > 0;
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
        if (_aiConnectionCts != null) { CancelAiConnection(); AiConnectionStatus.Text = L.T("已取消测试"); return; }
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
        if (!fetchModels && SelectedAiModel.Length == 0)
        { AiConnectionStatus.Text = L.T("请填写或选择模型后测试"); return; }
        string identity = AiConnectionIdentity(config);
        int version = ++_aiConnectionVersion;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _aiConnectionCts = cancel; _aiRequestIdentity = identity;
        AiConnectionStatus.Text = fetchModels ? L.T("正在连接并获取模型…") : L.T("正在测试所选模型…");
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
                if (!CommitAiConnection(config with { Model = (string)AiModelBox.SelectedItem })) return;
                AiConnectionStatus.Text = L.F($"已连接，获取到 {models.Count} 个模型；选择后可测试");
            }
            else
            {
                var result = await _aiConnectionService.TestModelAsync(config, cancel.Token);
                if (version != _aiConnectionVersion) return;
                if (!CommitAiConnection(config)) return;
                AiConnectionStatus.Text = L.F($"测试成功：{result.Model} · {result.ElapsedMilliseconds} ms");
            }
        }
        catch (OperationCanceledException)
        {
            if (version != _aiConnectionVersion) return;
            AiConnectionState.Invalidate(_options); ApplyChanges();
            AiConnectionStatus.Text = L.T("连接测试超时，请检查网络后重试");
        }
        catch (Exception ex)
        {
            if (version != _aiConnectionVersion) return;
            AiConnectionState.Invalidate(_options); ApplyChanges();
            string error = string.IsNullOrEmpty(config.ApiKey) ? ex.Message : ex.Message.Replace(config.ApiKey, "[密钥已隐藏]", StringComparison.Ordinal);
            AiConnectionStatus.Text = L.T("测试失败：") + error;
            if (fetchModels) { _aiModelIdentity = ""; AiConnectionStatus.Text += L.T("；可手动填写模型后测试"); }
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
        if (_aiModelIdentity.Length != 0) AiConnectionStatus.Text = L.T("已选择模型，点击“测试模型”验证实际调用");
    }
}
