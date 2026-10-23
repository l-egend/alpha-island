using System.Globalization;
using System.Windows;
using System.Windows.Input;
using alpha_island.Core;
using Button = System.Windows.Controls.Button;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    private System.Windows.Threading.DispatcherTimer? _clipboardHistoryEditTimer;
    private bool _clipboardHistoryLimitEditing;

    private void ClipboardHistoryLimit_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs args)
    {
        if (_suppress) return;
        _clipboardHistoryLimitEditing = true;
        _clipboardHistoryEditTimer ??= new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(350), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => CommitClipboardHistoryLimit(), Dispatcher);
        _clipboardHistoryEditTimer.Stop();
        // Wait for the number to settle before trimming history: typing 100 must not first trim to 1.
        if (int.TryParse(ClipboardHistoryLimitBox.Text, out int value) && value is >= 0 and <= 100)
            _clipboardHistoryEditTimer.Start();
    }

    private void CommitClipboardHistoryLimit()
    {
        if (_suppress) return;
        _clipboardHistoryEditTimer?.Stop();
        int current = SettingsRegistry.GetInt(_options, "ClipboardHistoryLimit", 10);
        int value = int.TryParse(ClipboardHistoryLimitBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? Math.Clamp(parsed, 0, 100) : current;
        _suppress = true;
        ClipboardHistoryLimitBox.Text = value.ToString(CultureInfo.InvariantCulture);
        _suppress = false;
        _clipboardHistoryLimitEditing = false;
        if (value == current) return;
        SettingsRegistry.Write(_options, "ClipboardHistoryLimit", value);
        ApplyChanges();
    }
    private void ClipboardHistoryLimit_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs args) => CommitClipboardHistoryLimit();
    private void ClipboardHistoryLimit_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs args)
    {
        if (args.Key == Key.Enter) { CommitClipboardHistoryLimit(); ClipboardHistoryLimitBox.SelectAll(); args.Handled = true; }
        if (args.Key == Key.Escape)
        {
            ClipboardHistoryLimitBox.Text = SettingsRegistry.GetInt(_options, "ClipboardHistoryLimit", 10).ToString(CultureInfo.InvariantCulture);
            args.Handled = true;
        }
    }
    private void ClipboardHistoryLimitStep_Click(object sender, RoutedEventArgs args)
    {
        if (_suppress || sender is not Button { Tag: string tag } || !int.TryParse(tag, out int delta)) return;
        int current = int.TryParse(ClipboardHistoryLimitBox.Text, out int typed) ? typed : SettingsRegistry.GetInt(_options, "ClipboardHistoryLimit", 10);
        ClipboardHistoryLimitBox.Text = Math.Clamp(current + delta, 0, 100).ToString(CultureInfo.InvariantCulture);
        CommitClipboardHistoryLimit();
    }
}
