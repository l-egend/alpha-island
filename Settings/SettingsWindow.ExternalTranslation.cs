using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using alpha_island.Core;
using alpha_island.Services;
using alpha_island.Interop;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Binding = System.Windows.Data.Binding;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    private readonly ShortcutSimulationService _translationShortcut = new();
    private readonly CancellationTokenSource _translationShortcutLifetime = new();
    private FrameworkElement _externalTranslationPanel = null!;
    private TextBox _externalShortcutBox = null!;
    private Button _recordShortcutButton = null!;
    private Button _testShortcutButton = null!;
    private TextBlock _externalShortcutStatus = null!;
    private volatile bool _recordingShortcut;
    private int _shortcutRecordPending;
    private int _shortcutRecordGeneration;
    private KeyboardHook? _shortcutRecorder;
    private bool _sendingTranslationShortcut;

    private void AddExternalTranslationSettings()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "外部翻译软件", Style = (Style)FindResource("W11CardTitle"), Margin = new Thickness(2, 26, 0, 8) });
        var group = new StackPanel();
        CheckBox? enabled = null;
        foreach (var descriptor in SettingsRegistry.All.Where(d => d.Key.StartsWith("ExternalTranslation", StringComparison.Ordinal)))
        {
            var row = new Grid { Style = (Style)FindResource("W11Row") };
            row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var label = new StackPanel { Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = descriptor.Label, Style = (Style)FindResource("W11RowLabel") });
            label.Children.Add(new TextBlock { Text = descriptor.Hint, Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap,
                MaxWidth = 310, TextAlignment = TextAlignment.Left, HorizontalAlignment = HorizontalAlignment.Left });
            row.Children.Add(label);
            FrameworkElement control;
            if (descriptor.Kind == SettingKind.Bool)
            {
                enabled = new CheckBox { Style = (Style)FindResource("W11Switch"), VerticalAlignment = VerticalAlignment.Center };
                enabled.Checked += Setting_Changed; enabled.Unchecked += Setting_Changed; control = enabled;
            }
            else
            {
                var box = new TextBox { Style = (Style)FindResource("W11TextBox"), Width = 218, MaxLength = 80,
                    MinHeight = 32, VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
                box.TextChanged += TextBox_Changed;
                if (descriptor.Key == "ExternalTranslationShortcut")
                {
                    _externalShortcutBox = box;
                    box.PreviewKeyDown += RecordExternalShortcut;
                    box.LostKeyboardFocus += (_, _) => StopRecordingExternalShortcut();
                }
                control = box;
                control.SetBinding(IsEnabledProperty, new Binding("IsChecked") { Source = enabled, TargetNullValue = false });
            }
            control.Name = descriptor.ControlName; RegisterName(control.Name, control);
            Grid.SetColumn(control, 1); row.Children.Add(control);
            group.Children.Add(row); group.Children.Add(new Border { Style = (Style)FindResource("W11RowDivider") });
        }
        var actions = new StackPanel { Margin = new Thickness(16, 10, 16, 14) };
        actions.SetBinding(IsEnabledProperty, new Binding("IsChecked") { Source = enabled, TargetNullValue = false });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        _recordShortcutButton = new Button { Content = "录制快捷键", Style = (Style)FindResource("W11Button") };
        _recordShortcutButton.Click += (_, _) =>
        {
            StopRecordingExternalShortcut();
            _recordingShortcut = true; _recordShortcutButton.Content = "按下快捷键…";
            _externalShortcutStatus.Text = "按下组合键或 F1–F24，Esc 取消录制。";
            _externalShortcutBox.Focus();
            _shortcutRecorder = new KeyboardHook();
            _shortcutRecorder.KeyAction += CaptureExternalShortcut;
            if (!_shortcutRecorder.Hook())
            {
                StopRecordingExternalShortcut();
                _externalShortcutStatus.Text = "无法开始录制，请手动填写快捷键。";
            }
        };
        _testShortcutButton = new Button { Content = "测试调用", Style = (Style)FindResource("W11Button"), Margin = new Thickness(8, 0, 0, 0) };
        _testShortcutButton.Click += async (_, _) => await TestExternalTranslationAsync();
        buttons.Children.Add(_recordShortcutButton); buttons.Children.Add(_testShortcutButton); actions.Children.Add(buttons);
        _externalShortcutStatus = new TextBlock { Text = "调用时设置窗口会隐藏。此功能只唤起外部软件，不自动取回译文，也不调用岛内 AI。",
            Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(_externalShortcutStatus); group.Children.Add(actions);
        panel.Children.Add(new Border { Style = (Style)FindResource("W11Card"), Child = group });
        _externalTranslationPanel = panel; Page_Ai.Children.Insert(1, panel);
        Closed += (_, _) => { StopRecordingExternalShortcut(); _translationShortcutLifetime.Cancel(); };
        IsVisibleChanged += (_, e) => { if (e.NewValue is false) StopRecordingExternalShortcut(); };
        Deactivated += (_, _) => StopRecordingExternalShortcut();
    }

    private void RecordExternalShortcut(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_recordingShortcut) return;
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { StopRecordingExternalShortcut(); _externalShortcutStatus.Text = "已取消录制。"; return; }
        var shortcut = new HotkeyCombo(Keyboard.Modifiers, key);
        if (!ShortcutSimulationService.IsSupported(shortcut)) return;
        _externalShortcutBox.Text = ShortcutSimulationService.Format(shortcut);
        StopRecordingExternalShortcut(); _externalShortcutStatus.Text = "快捷键已记录；请确认它与翻译软件的全局快捷键一致。";
    }

    private void StopRecordingExternalShortcut()
    {
        _recordingShortcut = false;
        Interlocked.Increment(ref _shortcutRecordGeneration);
        var recorder = _shortcutRecorder; _shortcutRecorder = null;
        if (recorder != null) { recorder.KeyAction -= CaptureExternalShortcut; recorder.Dispose(); }
        Interlocked.Exchange(ref _shortcutRecordPending, 0);
        if (_recordShortcutButton != null) _recordShortcutButton.Content = "录制快捷键";
    }

    private void CaptureExternalShortcut(object? sender, KeyboardHookEventArgs e)
    {
        if (!_recordingShortcut || e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        int generation = Volatile.Read(ref _shortcutRecordGeneration);
        // Keep modifiers flowing so releasing a physical modifier cannot leave it stuck.
        // Suppress the primary key while recording, before another program handles its hotkey.
        e.Handled = true;
        if (!e.IsKeyDown) return;
        ModifierKeys modifiers = ModifierKeys.None;
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0) modifiers |= ModifierKeys.Control;
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0) modifiers |= ModifierKeys.Alt;
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0) modifiers |= ModifierKeys.Shift;
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_LWIN) & 0x8000) != 0
            || (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RWIN) & 0x8000) != 0) modifiers |= ModifierKeys.Windows;
        var shortcut = new HotkeyCombo(modifiers, e.Key);
        if (e.Key != Key.Escape && !ShortcutSimulationService.IsSupported(shortcut)) return;
        if (Interlocked.Exchange(ref _shortcutRecordPending, 1) != 0) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (!_recordingShortcut || generation != Volatile.Read(ref _shortcutRecordGeneration)) return;
            if (e.Key == Key.Escape) _externalShortcutStatus.Text = "已取消录制。";
            else
            {
                _externalShortcutBox.Text = ShortcutSimulationService.Format(shortcut);
                _externalShortcutStatus.Text = "快捷键已记录；请确认它与翻译软件的全局快捷键一致。";
            }
            StopRecordingExternalShortcut();
        }));
    }

    private async Task TestExternalTranslationAsync()
    {
        if (_sendingTranslationShortcut || !ExternalTranslationOptions.From(_options).Enabled) return;
        string shortcut = _externalShortcutBox.Text.Trim();
        if (!ShortcutSimulationService.TryParse(shortcut, out _)) { _externalShortcutStatus.Text = "快捷键格式无效，例如 Ctrl+Alt+D。"; return; }
        _sendingTranslationShortcut = true; _testShortcutButton.IsEnabled = false;
        try
        {
            SaveOptions(); Hide();
            await Task.Delay(150, _translationShortcutLifetime.Token);
            var result = await ExternalTranslationLauncher.SendAsync(ExternalTranslationOptions.From(_options) with { Shortcut = shortcut }, _translationShortcut, _translationShortcutLifetime.Token);
            _externalShortcutStatus.Text = result.Message;
            if (!result.Sent) ShowInForeground();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _externalShortcutStatus.Text = "调用失败：" + ex.Message; if (!_translationShortcutLifetime.IsCancellationRequested) ShowInForeground(); }
        finally { _sendingTranslationShortcut = false; _testShortcutButton.IsEnabled = true; }
    }

    internal void OpenExternalTranslationSettings()
    {
        SelectPageByTag(SettingsPageIds.Ai);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            _externalTranslationPanel.BringIntoView(new Rect(0, 0, Math.Max(1, _externalTranslationPanel.ActualWidth), 40))));
    }
}
