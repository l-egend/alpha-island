using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using alpha_island.Core;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using Control = System.Windows.Controls.Control;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    private DispatcherTimer? _saveStatusTimer;
    private bool _savePending;

    private FrameworkElement CreateRegisteredSettingRow(SettingDescriptor descriptor)
    {
        var row = new Grid { Style = (Style)FindResource("W11Row") };
        bool withIcon = descriptor.Key == "ReduceEffectsEnabled";
        if (withIcon)
        {
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(new Border { Style = (Style)FindResource("W11IconTile"),
                Child = new AlphaIsland.PluginSdk.GlyphIcon { Style = (Style)FindResource("W11IconGlyph"), Text = AlphaIsland.PluginSdk.GlyphArtwork.ReducedMotionGlyph } });
        }
        row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var labels = new StackPanel { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = descriptor.Label, Style = (Style)FindResource("W11RowLabel") });
        if (descriptor.Hint.Length > 0) labels.Children.Add(new TextBlock { Text = descriptor.Hint, Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap, MaxWidth = 340, HorizontalAlignment = System.Windows.HorizontalAlignment.Left });
        if (withIcon) { Grid.SetColumn(labels, 1); labels.Margin = new Thickness(14, 0, 12, 0); }
        row.Children.Add(labels);
        FrameworkElement input;
        if (descriptor.Kind == SettingKind.Bool)
        {
            var check = new CheckBox { Style = (Style)FindResource("W11Switch") };
            check.Checked += Setting_Changed; check.Unchecked += Setting_Changed; input = check;
        }
        else if (descriptor.Key == "SpeechModel") input = CreateSpeechModelEditor();
        else if (SpeechChoices(descriptor.Key) is { } choices)
        {
            var choice = new System.Windows.Controls.ComboBox { Width = 210, MinHeight = 32, ItemsSource = choices,
                DisplayMemberPath = "Value", SelectedValuePath = "Key" };
            choice.SelectionChanged += (_, _) => { if (!_suppress) ApplyChanges(); }; input = choice;
        }
        else if (descriptor.Kind == SettingKind.Text)
        {
            var box = new TextBox { Style = (Style)FindResource("W11TextBox"), Width = descriptor.Key.StartsWith("Speech") ? 250 : 190, MinHeight = 32, MaxLength = descriptor.Key == "SpeechBaseUrl" ? 512 : descriptor.Key == "SpeechModel" ? 128 : 64,
                VerticalContentAlignment = VerticalAlignment.Center };
            box.TextChanged += Setting_Changed; input = box;
        }
        else
        {
            var box = new TextBox { Style = (Style)FindResource("W11TextBox"), Width = 84, MinHeight = 32, MaxLength = 16, VerticalContentAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
            box.TextChanged += NumericSetting_Changed; input = box;
        }
        input.Name = descriptor.ControlName; RegisterName(input.Name, input);
        input.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(input, withIcon ? 2 : 1); row.Children.Add(input);
        System.Windows.Automation.AutomationProperties.SetName(input, descriptor.Label);
        return SettingCard(row);
    }
    private void AddConsolidatedEditors()
    {
        ReduceEffectsRows.Children.Add(CreateRegisteredSettingRow(SettingsRegistry.Find("ReduceEffectsEnabled")!));
        ClipboardSettingsRows.Children.Add(CreateRegisteredSettingRow(SettingsRegistry.Find("ClipboardSensitiveGuardEnabled")!));
        Closed += (_, _) => { _saveStatusTimer?.Stop(); _clipboardHistoryEditTimer?.Stop(); };
    }
    private static bool TryReadSettingNumber(SettingDescriptor descriptor, string text, out double value)
    {
        bool parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        return parsed && double.IsFinite(value) && value >= descriptor.Min && value <= descriptor.Max
            && (descriptor.Kind != SettingKind.Int || value == Math.Truncate(value));
    }
    private void NumericSetting_Changed(object sender, TextChangedEventArgs args)
    {
        if (_suppress || sender is not TextBox box || SettingsRegistry.FindByControl(box.Name) is not { } descriptor) return;
        if (!TryReadSettingNumber(descriptor, box.Text, out _))
        {
            box.BorderBrush = Brushes.OrangeRed;
            box.ToolTip = $"请输入 {descriptor.Min:0.###}～{descriptor.Max:0.###} {descriptor.Unit}";
            return;
        }
        box.ClearValue(Control.BorderBrushProperty); box.ToolTip = null;
        ApplyChanges();
    }
    private void MarkSettingsPendingSave()
    {
        _savePending = true;
        SettingsSaveStatus.Tag = null;
        SettingsSaveStatusText.Text = "正在保存…"; SettingsSaveStatus.Visibility = Visibility.Visible;
        _saveStatusTimer?.Stop();
    }
    internal void ReportSettingsSaved(bool success)
    {
        SettingsSaveStatus.Tag = success ? "saved" : null;
        SettingsSaveStatusText.Text = success ? "已保存" : "保存失败，请重试";
        if (success) { CleanupRetiredGreetingAssets(); CleanupRetiredNotificationSounds(); }
        SettingsSaveStatus.Visibility = Visibility.Visible;
        _saveStatusTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        _saveStatusTimer.Tick -= HideSaveStatus; _saveStatusTimer.Tick += HideSaveStatus;
        _saveStatusTimer.Stop(); _saveStatusTimer.Start();
    }
    private void HideSaveStatus(object? sender, EventArgs args)
    { _saveStatusTimer?.Stop(); SettingsSaveStatus.Visibility = Visibility.Collapsed; }
}
