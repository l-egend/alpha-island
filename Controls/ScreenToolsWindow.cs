using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using alpha_island.Services;
using alpha_island.Core;
using AlphaIsland.Speech;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace alpha_island.Controls;

internal sealed partial class ScreenToolsWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    private static void ExcludeFromCapture(Window window) =>
        SetWindowDisplayAffinity(new System.Windows.Interop.WindowInteropHelper(window).Handle, 0x11);
    private readonly ScreenTextService _screen = new();
    private readonly Func<ChatConfig> _configuration;
    private readonly Func<CancellationToken, Rectangle?> _pickRegion;
    private readonly Func<ExternalTranslationOptions> _externalConfiguration;
    private readonly ShortcutSimulationService _shortcutSimulation;
    private readonly Button _externalButton;
    private readonly TextBox _original = Box();
    private readonly TextBox _result = Box();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.2) };
    private readonly Button _liveButton;
    private Rectangle? _region;
    private CancellationTokenSource? _operation;
    private int _generation;
    private bool _busy;
    private bool _closed;
    private string _lastText = "";
    private SubtitleIslandWindow? _subtitle;

    internal ScreenToolsWindow(Func<ChatConfig> configuration, Func<ExternalTranslationOptions>? externalConfiguration = null,
        Action? openExternalSettings = null, ShortcutSimulationService? shortcutSimulation = null,
        Action<string>? copyText = null, Action<ScreenToolOutput>? completed = null,
        Func<CancellationToken, Rectangle?>? pickRegion = null,
        Func<SpeechRecognitionOptions>? speechConfiguration = null, Func<string>? subtitleLanguage = null,
        Action<bool>? subtitleStateChanged = null, Func<ISpeechRecognizer, bool, ProviderVoiceCapture>? createSpeechCapture = null,
        Func<string, ChatConfig, CancellationToken, string, Task<string>>? translateCaption = null,
        Func<bool>? canUseTools = null)
    {
        _copyText = copyText ?? (text => Interop.ClipboardTextWriter.Copy(text)); _completed = completed;
        _configuration = configuration;
        _pickRegion = pickRegion ?? ScreenRegionPicker.Pick;
        _speechConfiguration = speechConfiguration ?? (() => SpeechSettings.Read(new IslandOptions()));
        _subtitleLanguage = subtitleLanguage ?? (() => "zh-CN"); _subtitleStateChanged = subtitleStateChanged;
        _createSpeechCapture = createSpeechCapture ?? ((recognizer, loopback) => new ProviderVoiceCapture(recognizer, new WasapiSpeechAudioSource(loopback), continuous: true));
        _translateCaption = translateCaption ?? ((text, config, token, language) => _screen.TranslateAsync(text, config, token, language));
        _canUseTools = canUseTools ?? (() => true);
        _externalConfiguration = externalConfiguration ?? (() => ExternalTranslationOptions.From(new IslandOptions()));
        _shortcutSimulation = shortcutSimulation ?? new ShortcutSimulationService();
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/alpha-island;component/Themes/LiquidGlass.xaml") });
        Title = "屏幕工具";
        Width = 720; Height = 530; MinWidth = 580; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SourceInitialized += (_, _) =>
        {
            ExcludeFromCapture(this);
            int darkMode = 1;
            alpha_island.Interop.NativeMethods.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle,
                alpha_island.Interop.NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
        };
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI, Microsoft YaHei");
        var root = new Grid { Margin = new Thickness(18), Background = Background };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var actions = new WrapPanel();
        actions.Children.Add(ActionButton("框选屏幕文字", async () => await PickAndReadAsync()));
        actions.Children.Add(ActionButton("识字并复制", async () => await RecognizeAsync(copy: true)));
        actions.Children.Add(ActionButton("颜色吸管", async () => await PickColorAsync()));
        actions.Children.Add(ActionButton("翻译文字", async () => await TranslateOriginalAsync()));
        _liveButton = ActionButton("开始字幕", ToggleSubtitles); actions.Children.Add(_liveButton);
        actions.Children.Add(ActionButton("字幕区域", async () => await SelectSubtitleRegionAsync()));
        actions.Children.Add(ActionButton("屏幕扫码", async () => await ScanAsync()));
        _externalButton = ActionButton("调用外部翻译", async () => await InvokeExternalTranslationAsync());
        actions.Children.Add(_externalButton);
        if (openExternalSettings != null) actions.Children.Add(ActionButton("外部翻译设置", openExternalSettings));
        _captionSource.ItemsSource = new[] { "系统声音（语音识别）", "麦克风（语音识别）", "屏幕文字（本机 OCR）" };
        _captionSource.SelectedIndex = 0;
        _captionSource.SelectionChanged += (_, _) => { if (_subtitleRunning) StopSubtitles(); };
        _captionSource.Margin = new Thickness(0, 0, 8, 4); _captionSource.MinHeight = 30; _captionSource.Width = 200;
        actions.Children.Add(_captionSource);
        root.Children.Add(actions);
        Grid.SetRow(_status, 1); root.Children.Add(_status);
        var body = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        body.ColumnDefinitions.Add(new()); body.ColumnDefinitions.Add(new());
        body.Children.Add(Pane("识别文字（可修改）", _original));
        var translated = Pane("译文 / 扫码结果", _result); Grid.SetColumn(translated, 1); body.Children.Add(translated);
        Grid.SetRow(body, 2); root.Children.Add(body);
        var footer = new StackPanel();
        var resultActions = new WrapPanel();
        resultActions.Children.Add(ActionButton("复制原文", CopyOriginal));
        resultActions.Children.Add(ActionButton("复制结果", CopyResult));
        resultActions.Children.Add(ActionButton("复制 HEX", () => CopyColor("hex")));
        resultActions.Children.Add(ActionButton("复制 RGB", () => CopyColor("rgb")));
        resultActions.Children.Add(ActionButton("复制 HSL", () => CopyColor("hsl")));
        resultActions.Children.Add(ActionButton("打开识别链接", OpenResultLink));
        footer.Children.Add(resultActions);
        footer.Children.Add(_palette);
        footer.Children.Add(new TextBlock { Text = "声音字幕读取默认输出或默认麦克风，使用语音模型识别、对话模型翻译；仅启动后分段发送，不保存录音。框选用于确定显示器；屏幕文字来源才会限制 OCR 区域。翻译岛只显示译文，右键可停止或复制。",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 8, 0, 0) });
        Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        _result.IsReadOnly = true;
        _timer.Tick += async (_, _) => { if (!_busy) await ReadAndTranslateAsync(); };
        Closed += (_, _) => { _closed = true; StopSubtitles(); _operation?.Cancel(); };
        _status.Text = "选择字幕来源后点“开始字幕”，可直接框选区域；上方主岛与下方翻译岛独立。";
        RefreshExternalTranslation();
    }

    private static TextBox Box() => new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(10), FontSize = 13
    };
    private static Grid Pane(string title, TextBox box)
    {
        var pane = new Grid { Margin = new Thickness(0, 0, 8, 0) };
        pane.RowDefinitions.Add(new() { Height = GridLength.Auto }); pane.RowDefinitions.Add(new());
        pane.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 0, 0, 6), FontSize = 12 });
        Grid.SetRow(box, 1); pane.Children.Add(box); return pane;
    }
    private Button ActionButton(string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 4), Padding = new Thickness(10, 5, 10, 5) };
        button.Click += (_, _) => action(); return button;
    }
    private async Task<Rectangle?> PickAsync(CancellationToken token = default)
    {
        Hide();
        try { await Task.Delay(120, token); return _pickRegion(token); }
        finally { if (!_closed && _canUseTools() && !token.IsCancellationRequested) { Show(); Activate(); } }
    }
    private Task PickAndReadAsync() => RecognizeAsync(copy: false);
    private async Task TranslateOriginalAsync()
    {
        if (_busy || string.IsNullOrWhiteSpace(_original.Text)) return;
        StopSubtitles();
        await RunAsync(async ct => { _result.Text = await _screen.TranslateAsync(_original.Text, _configuration(), ct); _status.Text = "翻译完成"; });
    }
    internal void RefreshExternalTranslation()
    {
        var options = _externalConfiguration();
        _externalButton.IsEnabled = options.Enabled && !_busy;
        _externalButton.ToolTip = options.DisplayName + " · " + options.Shortcut;
    }
    private async Task InvokeExternalTranslationAsync()
    {
        if (_busy || _closed) return;
        var options = _externalConfiguration();
        if (!options.Enabled) { _status.Text = "请先在设置中启用外部翻译快捷键。"; return; }
        if (!ShortcutSimulationService.TryParse(options.Shortcut, out _)) { _status.Text = "外部翻译快捷键无效，请在设置中修改。"; return; }
        StopSubtitles();
        await RunAsync(async ct =>
        {
            Hide();
            try
            {
                await Task.Delay(150, ct);
                var result = await ExternalTranslationLauncher.SendAsync(options, _shortcutSimulation, ct);
                _status.Text = result.Message;
                if (!result.Sent && !_closed) { Show(); Activate(); }
            }
            catch { if (!_closed) { Show(); Activate(); } throw; }
        });
    }
    private async Task ScanAsync()
    {
        if (_busy) return;
        StopSubtitles();
        var selected = await PickAsync(); if (!selected.HasValue) return;
        await RunAsync(async ct =>
        {
            Hide(); string? decoded;
            try { await Task.Delay(120, ct); decoded = await _screen.ScanAsync(selected.Value, ct); }
            finally { if (!_closed) { Show(); Activate(); } }
            _result.Text = decoded ?? "";
            _status.Text = decoded == null ? "没有识别到二维码，请重新框选完整二维码。" : "扫码完成，链接可由你选择打开。";
        });
    }
    private async void ToggleSubtitles() => await StartOrStopSubtitlesAsync();
    private async Task ReadAndTranslateAsync()
    {
        if (!_region.HasValue || _busy || !_timer.IsEnabled) return;
        int generation = _generation;
        await RunAsync(async ct =>
        {
            string text = await _screen.ReadAsync(_region.Value, ct);
            if (generation != _generation || !_timer.IsEnabled || text.Length == 0 || text == _lastText) return;
            _original.Text = text; _nextCaption = text;
            await TranslateCaptionQueueAsync(generation, _captionLifetime?.Token ?? ct);
        });
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy || _closed) return;
        _busy = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45)); _operation = cts;
        try { await action(cts.Token); }
        catch (OperationCanceledException) { if (!_closed) { _status.Text = "操作已停止或超时"; if (_subtitleRunning) CaptionFailed(_status.Text); else NotifyCompleted(new("error", _status.Text)); } }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            IslandLog.Warn("ScreenTools", "操作失败：" + ex.GetType().Name + " / 0x" + ex.HResult.ToString("X8"));
            if (!_closed) { _status.Text = ex.Message; if (_subtitleRunning) CaptionFailed(_status.Text); else NotifyCompleted(new("error", _status.Text)); }
        }
        finally { if (ReferenceEquals(_operation, cts)) _operation = null; _busy = false; RefreshExternalTranslation(); }
    }
    private void ShowSubtitle(string text)
    {
        if (_subtitle == null)
        {
            _subtitle = new SubtitleIslandWindow(_region, _captionSource.SelectedIndex == 2, StopSubtitles, _copyText);
            _subtitle.Closed += (_, _) => { _subtitle = null; if (_subtitleRunning) StopSubtitles(); };
        }
        _subtitle.UpdateText(text);
    }
    private void CopyResult()
    {
        try { if (_result.Text.Length > 0) _copyText(_result.Text); }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
    private void OpenResultLink()
    {
        if (!Uri.TryCreate(_result.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        { _status.Text = "结果不是可打开的网页链接。"; return; }
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
}
