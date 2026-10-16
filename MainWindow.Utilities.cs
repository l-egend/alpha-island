using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;

using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace alpha_island;

public partial class MainWindow
{
    private readonly CancellationTokenSource _utilitiesLifetime = new();
    private PomodoroTimer _pomodoro => _workspace.Focus.Timer;
    private IslandTicker.Subscription? _pomodoroTicker;
    private TextBlock _focusValue = null!;
    private TextBlock _focusDetail = null!;
    private Button _focusButton = null!;
    private ScreenToolsWindow? _screenTools;
    private readonly ShortcutSimulationService _externalTranslationShortcut = new();
    private Button? _externalTranslationButton;
    private TextBlock? _externalTranslationHint;
    private bool _sendingExternalTranslation;

    private static TextBlock UtilityText(string text, double size = 12) => new()
    { Text = text, FontSize = size, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 3) };
    private static Button UtilityButton(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 2, 6, 2), FontSize = 11 };
        button.Click += (_, _) => action(); return button;
    }
    private static StackPanel UtilityButtons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in buttons) panel.Children.Add(button);
        return panel;
    }
    private void RegisterUtility(string id, string title, int order, StackPanel content, Action? activated = null)
    {
        var view = new Grid { Height = 194, VerticalAlignment = VerticalAlignment.Center, RenderTransform = new TranslateTransform() };
        view.Children.Add(content); CardViewport.Children.Add(view);
        _pages.Register(new DelegateIslandPage(id, title, view, order, IslandMetrics.CardHeightLyrics, activated: activated,
            viewportChanged: height => view.Height = height));
    }
    private void RegisterUtilityPages()
    {
        RegisterWeatherPage();

        var focus = new StackPanel(); focus.Children.Add(UtilityText("番茄钟", 15));
        _workspace.Focus.Configure(PomodoroOptions.From(_options));
        if (_runtimeEnabled && _pomodoro.Options.AutoStart) _pomodoro.Start(DateTime.UtcNow);
        _focusValue = UtilityText("25:00", 36); focus.Children.Add(_focusValue);
        _focusDetail = UtilityText("专注 · 已完成 0 轮"); focus.Children.Add(_focusDetail);
        _focusButton = UtilityButton("开始专注", ToggleFocus);
        focus.Children.Add(UtilityButtons(_focusButton, UtilityButton("重置", () => _workspace.Focus.ResetCommand.Execute(null)),
            UtilityButton("设置", OpenFocusSettings)));
        focus.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2 || IsFocusControl(e.OriginalSource)) return;
            e.Handled = true;
            if (_pomodoro.Options.DoubleClickOpensSettings) OpenFocusSettings(); else ToggleFocus();
        };
        RegisterUtility("focus", "番茄钟", 9, focus, SyncFocus);
        _focusValue.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(alpha_island.ViewModels.FocusViewModel.Remaining)) { Source = _workspace.Focus });
        _focusDetail.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(alpha_island.ViewModels.FocusViewModel.Detail)) { Source = _workspace.Focus });
        _focusButton.SetBinding(Button.ContentProperty, new System.Windows.Data.Binding(nameof(alpha_island.ViewModels.FocusViewModel.ButtonText)) { Source = _workspace.Focus });
        _workspace.Focus.PhaseCompleted += () =>
        {
            ShowSystemNotify("\uE916", _pomodoro.CycleFinished ? "番茄钟循环完成" : _pomodoro.IsBreak ? "专注完成" : "休息结束",
                _pomodoro.CycleFinished ? $"已完成 {_pomodoro.CompletedFocus} 轮专注"
                    : $"{(_pomodoro.IsLongBreak ? "长休息" : _pomodoro.IsBreak ? "短休息" : "专注")} {Math.Ceiling(_pomodoro.Remaining.TotalMinutes)} 分钟"
                        + (_pomodoro.Running ? " · 已自动开始" : " · 点击开始"), _pomodoro.IsBreak ? "休息" : "专注");
            if (_pomodoro.Options.PlaySound) PomodoroSound.Play(_pomodoro.Options.Sound);
        };
        _pomodoroTicker = IslandTicker.Shared(Dispatcher).Subscribe("focus", TimeSpan.FromSeconds(1), () => _workspace.Focus.Tick(DateTime.UtcNow));
        _workspace.Focus.Changed += () => { _pomodoroTicker?.SetPaused(!_pomodoro.Running); UpdateCardPinSummary("focus"); };
        SyncFocus();

        var tools = new StackPanel(); tools.Children.Add(UtilityText("屏幕工具", 15));
        tools.Children.Add(UtilityButtons(UtilityButton("翻译 / 字幕 / 扫码", OpenScreenTools), UtilityButton("快速启动", ShowQuickLauncher)));
        _externalTranslationButton = UtilityButton("调用外部翻译", async () => await InvokeExternalTranslationAsync());
        tools.Children.Add(UtilityButtons(_externalTranslationButton, UtilityButton("快捷键设置", OpenExternalTranslationSettings)));
        _externalTranslationHint = UtilityText("", 11); tools.Children.Add(_externalTranslationHint);
        RefreshExternalTranslation();
        RegisterUtility("screen-tools", "屏幕翻译与扫码", 10, tools);
    }

    private void OpenScreenTools()
    {
        if (_screenTools == null)
        {
            _screenTools = new ScreenToolsWindow(() => ChatConfig.From(_options), () => ExternalTranslationOptions.From(_options),
                OpenExternalTranslationSettings, _externalTranslationShortcut);
            _screenTools.Closed += (_, _) => _screenTools = null;
        }
        _screenTools.RefreshExternalTranslation(); _screenTools.Show(); _screenTools.Activate();
    }
    private void RefreshExternalTranslation()
    {
        var options = ExternalTranslationOptions.From(_options);
        if (_externalTranslationButton != null)
        {
            _externalTranslationButton.IsEnabled = options.Enabled && !_sendingExternalTranslation;
            _externalTranslationButton.ToolTip = options.DisplayName + " · " + options.Shortcut;
        }
        if (_externalTranslationHint != null) _externalTranslationHint.Text = options.Enabled
            ? $"{options.DisplayName} · {options.Shortcut}\n外部译文在对应软件查看；AI 翻译需配置接口"
            : "AI 翻译需配置接口；外部快捷键调用已关闭";
        _screenTools?.RefreshExternalTranslation();
    }
    private async Task InvokeExternalTranslationAsync()
    {
        if (_sendingExternalTranslation) return;
        var options = ExternalTranslationOptions.From(_options);
        if (!options.Enabled) return;
        _sendingExternalTranslation = true; RefreshExternalTranslation();
        try
        {
            var result = await ExternalTranslationLauncher.SendAsync(options, _externalTranslationShortcut, _utilitiesLifetime.Token);
            if (_externalTranslationHint != null) _externalTranslationHint.Text = result.Message;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (_externalTranslationHint != null) _externalTranslationHint.Text = "调用失败：" + ex.Message; }
        finally
        {
            _sendingExternalTranslation = false;
            if (_externalTranslationButton != null) _externalTranslationButton.IsEnabled = ExternalTranslationOptions.From(_options).Enabled;
        }
    }
    private void OpenExternalTranslationSettings()
    {
        _screenTools?.Hide(); ShowSettings(); _settingsWindow?.OpenExternalTranslationSettings();
    }
    private void ToggleFocus() => _workspace.Focus.ToggleCommand.Execute(null);
    private void SyncFocus() => _workspace.Focus.Refresh();
    private static bool IsFocusControl(object source)
    {
        var node = source as DependencyObject;
        while (node != null)
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase) return true;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : null;
        }
        return false;
    }
    private void OpenFocusSettings()
    {
        ShowSettings();
        _settingsWindow?.OpenPomodoroSettings();
    }
    private void StopUtilities()
    {
        StopLauncher();
        StopProductivity();
        _workspace.Notifications.Dispose();
        _pomodoroTicker?.Dispose(); _screenTools?.Close(); _utilitiesLifetime.Cancel(); StopWeatherPolling(); _weatherService.Dispose();
    }
}
