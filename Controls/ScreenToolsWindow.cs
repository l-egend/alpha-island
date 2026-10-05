using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using alpha_island.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace alpha_island.Controls;

internal sealed class ScreenToolsWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    private static void ExcludeFromCapture(Window window) =>
        SetWindowDisplayAffinity(new System.Windows.Interop.WindowInteropHelper(window).Handle, 0x11);
    private readonly ScreenTextService _screen = new();
    private readonly Func<ChatConfig> _configuration;
    private readonly TextBox _original = Box();
    private readonly TextBox _result = Box();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Button _liveButton;
    private Rectangle? _region;
    private CancellationTokenSource? _operation;
    private int _generation;
    private bool _busy;
    private bool _closed;
    private string _lastText = "";
    private Window? _subtitle;
    private TextBlock? _subtitleText;

    internal ScreenToolsWindow(Func<ChatConfig> configuration)
    {
        _configuration = configuration;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/alpha-island;component/Themes/LiquidGlass.xaml") });
        Title = "屏幕翻译 / 字幕 / 扫码";
        Width = 600; Height = 470; MinWidth = 480; MinHeight = 390;
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
        actions.Children.Add(ActionButton("翻译文字", async () => await TranslateOriginalAsync()));
        _liveButton = ActionButton("开始字幕", ToggleSubtitles); actions.Children.Add(_liveButton);
        actions.Children.Add(ActionButton("屏幕扫码", async () => await ScanAsync()));
        root.Children.Add(actions);
        Grid.SetRow(_status, 1); root.Children.Add(_status);
        var body = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        body.ColumnDefinitions.Add(new()); body.ColumnDefinitions.Add(new());
        body.Children.Add(Pane("识别文字（可修改）", _original));
        var translated = Pane("译文 / 扫码结果", _result); Grid.SetColumn(translated, 1); body.Children.Add(translated);
        Grid.SetRow(body, 2); root.Children.Add(body);
        var footer = new StackPanel();
        var resultActions = new WrapPanel();
        resultActions.Children.Add(ActionButton("复制结果", CopyResult));
        resultActions.Children.Add(ActionButton("打开识别链接", OpenResultLink));
        footer.Children.Add(resultActions);
        footer.Children.Add(new TextBlock { Text = "识别在本机完成；翻译将文字发送到设置中的 AI 接口。字幕每 3 秒检查选区，停止或关窗即结束。",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 8, 0, 0) });
        Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        _result.IsReadOnly = true;
        _timer.Tick += async (_, _) => { if (!_busy) await ReadAndTranslateAsync(); };
        Closed += (_, _) => { _closed = true; StopSubtitles(); _operation?.Cancel(); };
        _status.Text = "框选文字后可翻译；先框选字幕区域，再开始持续字幕。";
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
    private async Task<Rectangle?> PickAsync()
    {
        Hide();
        try { await Task.Delay(120); return ScreenRegionPicker.Pick(); }
        finally { if (!_closed) { Show(); Activate(); } }
    }
    private async Task PickAndReadAsync()
    {
        if (_busy) return;
        StopSubtitles();
        var selected = await PickAsync();
        if (!selected.HasValue) return;
        _region = selected; _lastText = "";
        await RunAsync(async ct =>
        {
            Hide();
            try { await Task.Delay(120, ct); _original.Text = await _screen.ReadAsync(selected.Value, ct); }
            finally { if (!_closed) { Show(); Activate(); } }
            _status.Text = _original.Text.Length == 0 ? "没有识别到文字，请重新框选或放大文字。" : "文字已识别，可修改后翻译。";
        });
    }
    private async Task TranslateOriginalAsync()
    {
        if (_busy || string.IsNullOrWhiteSpace(_original.Text)) return;
        StopSubtitles();
        await RunAsync(async ct => { _result.Text = await _screen.TranslateAsync(_original.Text, _configuration(), ct); _status.Text = "翻译完成"; });
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
    private async void ToggleSubtitles()
    {
        if (_timer.IsEnabled) { StopSubtitles(); return; }
        if (!_region.HasValue) { _status.Text = "请先框选字幕区域。"; return; }
        if (_busy) return;
        _lastText = ""; _timer.Start(); _liveButton.Content = "停止字幕";
        _status.Text = "字幕识别中…";
        await ReadAndTranslateAsync();
    }
    private async Task ReadAndTranslateAsync()
    {
        if (!_region.HasValue || _busy || !_timer.IsEnabled) return;
        int generation = _generation;
        await RunAsync(async ct =>
        {
            string text = await _screen.ReadAsync(_region.Value, ct);
            if (generation != _generation || !_timer.IsEnabled || text.Length == 0 || text == _lastText) return;
            _original.Text = text;
            string translation = await _screen.TranslateAsync(text, _configuration(), ct);
            if (generation != _generation || !_timer.IsEnabled || _closed) return;
            _lastText = text; _result.Text = translation; ShowSubtitle(translation);
            _status.Text = "字幕已更新 · " + DateTime.Now.ToString("HH:mm:ss");
        });
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy || _closed) return;
        _busy = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45)); _operation = cts;
        try { await action(cts.Token); }
        catch (OperationCanceledException) { if (!_closed) _status.Text = "操作已停止或超时"; }
        catch (Exception ex) { if (!_closed) _status.Text = ex.Message; }
        finally { if (ReferenceEquals(_operation, cts)) _operation = null; _busy = false; }
    }
    private void StopSubtitles()
    {
        ++_generation; _timer.Stop(); _operation?.Cancel(); _liveButton.Content = "开始字幕";
        _subtitle?.Close(); _subtitle = null; _subtitleText = null;
    }
    private void ShowSubtitle(string text)
    {
        if (_subtitle == null)
        {
            _subtitleText = new TextBlock { Foreground = Brushes.White, FontSize = 20, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
            _subtitle = new Window { Title = "译文字幕", Width = 620, SizeToContent = SizeToContent.Height, MaxHeight = 200,
                Topmost = true, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None,
                Background = new SolidColorBrush(Color.FromRgb(20, 24, 32)), Opacity = 0.94,
                Content = new Border { Padding = new Thickness(16, 10, 16, 10), Child = _subtitleText } };
            Rect area = SystemParameters.WorkArea;
            _subtitle.Left = area.Left + (area.Width - _subtitle.Width) / 2; _subtitle.Top = area.Bottom - 170;
            _subtitle.MouseLeftButtonDown += (_, _) => _subtitle?.DragMove();
            _subtitle.Closed += (_, _) => { _subtitle = null; _subtitleText = null; };
            _subtitle.Show();
            ExcludeFromCapture(_subtitle);
        }
        if (_subtitleText != null) _subtitleText.Text = text;
    }
    private void CopyResult()
    {
        try { if (_result.Text.Length > 0) System.Windows.Clipboard.SetText(_result.Text); }
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
