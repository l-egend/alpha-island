using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Windows;
using System.Windows.Controls;
using AlphaIsland.Speech;
using alpha_island.Core;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    internal SpeechModelService SpeechEndpoints { get; set; } = new();
    private CancellationTokenSource? _speechSetupRequest;
    private TextBlock? _speechSetupStatus;
    private Button? _speechFetchButton, _speechTestButton, _speechCancelButton;

    private ComboBox CreateSpeechModelEditor()
    {
        var choice = new ComboBox { Width = 250, MinHeight = 32, IsEditable = true, IsTextSearchEnabled = false,
            MaxDropDownHeight = 240, ToolTip = L.T("获取模型后选择，也可直接输入服务提供的语音识别模型名称") };
        var textProperty = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ComboBox.TextProperty, typeof(ComboBox));
        EventHandler changed = (_, _) => { if (!_suppress) ApplyChanges(); };
        textProperty.AddValueChanged(choice, changed);
        Closed += (_, _) => textProperty.RemoveValueChanged(choice, changed);
        choice.SelectionChanged += (_, _) =>
        {
            if (!_suppress && choice.SelectedItem is string model) choice.Text = model;
        };
        choice.Loaded += (_, _) =>
        {
            if (choice.Template.FindName("PART_EditableTextBox", choice) is TextBox editor) editor.MaxLength = 128;
        };
        return choice;
    }

    private FrameworkElement CreateSpeechSetupActions()
    {
        var content = new StackPanel { Margin = new Thickness(12) };
        content.Children.Add(new TextBlock { Text = L.T("连接与模型测试"), FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock
        {
            Text = L.T("测试发送 1 秒静音，不采集麦克风。"),
            TextWrapping = TextWrapping.Wrap, Opacity = .75, Margin = new Thickness(0, 6, 0, 10)
        });
        var actions = new WrapPanel();
        Button Add(string name, string label)
        {
            var button = new Button { Name = name, Content = label, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 6) };
            RegisterName(name, button); actions.Children.Add(button); return button;
        }
        _speechFetchButton = Add("SpeechFetchModelsButton", L.T("获取模型"));
        _speechTestButton = Add("SpeechTestButton", L.T("测试识别"));
        _speechCancelButton = Add("SpeechCancelTestButton", L.T("取消")); _speechCancelButton.Visibility = Visibility.Collapsed;
        _speechFetchButton.Click += async (_, _) => await RunSpeechSetupAsync(models: true);
        _speechTestButton.Click += async (_, _) => await RunSpeechSetupAsync(models: false);
        _speechCancelButton.Click += (_, _) => _speechSetupRequest?.Cancel();
        _speechSetupStatus = new TextBlock { Name = "SpeechSetupStatus", TextWrapping = TextWrapping.Wrap, Opacity = .8 };
        RegisterName(_speechSetupStatus.Name, _speechSetupStatus);
        content.Children.Add(actions); content.Children.Add(_speechSetupStatus);
        IsVisibleChanged += (_, _) => { if (!IsVisible) _speechSetupRequest?.Cancel(); };
        Closed += (_, _) => _speechSetupRequest?.Cancel();
        return SettingCard(content);
    }

    private SpeechRecognitionOptions ReadSpeechSetupOptions()
    {
        var options = SpeechSettings.Read(_options, SpeechSecretRoot);
        if (FindName("SpeechApiKeyBox") is PasswordBox key && !string.IsNullOrWhiteSpace(key.Password))
            options = options with { ApiKey = key.Password.Trim() };
        return options;
    }

    private async Task RunSpeechSetupAsync(bool models)
    {
        if (_speechSetupRequest != null || _speechSetupStatus == null) return;
        var options = ReadSpeechSetupOptions();
        if (options.Provider == "windows-local")
        { _speechSetupStatus.Text = L.T("Windows 本机语音不使用 API 模型；请使用语音快捷键测试本机识别。"); return; }
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(40)); _speechSetupRequest = request;
        _speechFetchButton!.IsEnabled = _speechTestButton!.IsEnabled = false;
        _speechCancelButton!.Visibility = Visibility.Visible;
        _speechSetupStatus.Text = models ? L.T("正在获取模型…") : L.T("正在测试当前识别接口与模型…");
        try
        {
            if (models)
            {
                var available = await SpeechEndpoints.ListModelsAsync(options, request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (options != ReadSpeechSetupOptions()) { _speechSetupStatus.Text = L.T("配置已更改，请重新获取模型。"); return; }
                var choice = (ComboBox)FindName("SpeechModelBox");
                bool suppress = _suppress; _suppress = true;
                try { choice.ItemsSource = available; choice.Text = options.Model; }
                finally { _suppress = suppress; }
                _speechSetupStatus.Text = L.F($"已获取 {available.Count} 个模型，已保留当前选择。请选用支持语音识别的模型。");
            }
            else
            {
                var result = await SpeechEndpoints.TestRecognitionAsync(options, request.Token);
                request.Token.ThrowIfCancellationRequested();
                _speechSetupStatus.Text = options != ReadSpeechSetupOptions() ? L.T("配置已更改，请重新测试。")
                    : string.IsNullOrWhiteSpace(result.Text) ? L.T("测试通过：接口、密钥和模型可用（静音样本无文字）。实际准确率请用语音输入验证。")
                    : L.T("测试通过：识别接口已返回文字。实际准确率请用语音输入验证。");
            }
        }
        catch (OperationCanceledException) { _speechSetupStatus.Text = L.T("测试已取消或请求超时，可以重试。"); }
        catch (SpeechRecognitionException error) { _speechSetupStatus.Text = error.Message + (models ? L.T(" 也可手动填写模型名称。") : ""); }
        catch { _speechSetupStatus.Text = L.T("接口测试未完成，请检查识别协议、地址和模型配置。"); }
        finally
        {
            _speechSetupRequest = null;
            _speechFetchButton.IsEnabled = _speechTestButton.IsEnabled = true;
            _speechCancelButton.Visibility = Visibility.Collapsed;
        }
    }
}
