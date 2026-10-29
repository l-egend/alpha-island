using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Services;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    private GreetingMediaStore _notificationAssets = null!;
    private GreetingAudioPlayer? _notificationSoundPreview;
    private TextBlock? _notificationSoundHint, _notificationSoundStatus;
    private readonly HashSet<string> _retiredNotificationSounds = new(StringComparer.OrdinalIgnoreCase);

    private void AddNotificationSoundSettings(StackPanel rows)
    {
        _notificationAssets = new GreetingMediaStore(Path.Combine(_greetingAssets.DirectoryPath, "notification-sounds"));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(GreetingMediaButton(L.T("选择音效"), "NotificationSoundChoose", ChooseNotificationSound));
        actions.Children.Add(GreetingMediaButton(L.T("试听"), "NotificationSoundPreview", PreviewNotificationSound));
        actions.Children.Add(GreetingMediaButton(L.T("恢复默认"), "NotificationSoundReset", ResetNotificationSound));
        rows.Children.Add(GreetingFileRow(L.T("通知音效文件"), L.T("WAV、MP3、M4A、FLAC、WMA；最长 10 秒。导入后保存独立副本。"), actions, out var hint, AlphaIsland.PluginSdk.GlyphArtwork.NotificationSoundGlyph));
        _notificationSoundHint = hint;
        _notificationSoundStatus = new TextBlock { Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 0, 14, 8) };
        rows.Children.Add(_notificationSoundStatus);
        IsVisibleChanged += (_, _) => { if (!IsVisible) _notificationSoundPreview?.Stop(); };
        Closed += (_, _) => _notificationSoundPreview?.Dispose();
    }

    private void ChooseNotificationSound()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = L.T("选择通知提示音"), CheckFileExists = true,
            Filter = L.T("音效|*.wav;*.mp3;*.m4a;*.flac;*.wma") };
        if (dialog.ShowDialog(this) != true) return;
        try { ImportNotificationSound(dialog.FileName); }
        catch (Exception ex) { _notificationSoundStatus!.Text = L.T("文件未更改：") + ex.Message; }
    }

    private void ImportNotificationSound(string source)
    {
        _notificationSoundPreview?.Stop();
        string imported = _notificationAssets.ImportSound(source);
        RetireNotificationSound();
        SettingsRegistry.Write(_options, "NotificationSoundPath", imported);
        _notificationSoundStatus!.Text = L.T("音效已导入；原文件移动后仍可使用。");
        RefreshNotificationSoundSettings(); ApplyChanges();
    }

    private void RetireNotificationSound()
    {
        string prior = SettingsRegistry.GetText(_options, "NotificationSoundPath", "");
        if (prior.Length > 0) _retiredNotificationSounds.Add(prior);
    }

    private void ResetNotificationSound()
    {
        _notificationSoundPreview?.Stop(); RetireNotificationSound();
        SettingsRegistry.Write(_options, "NotificationSoundPath", "");
        _notificationSoundStatus!.Text = L.T("已恢复内置轻柔提示音。");
        RefreshNotificationSoundSettings(); ApplyChanges();
    }

    private void PreviewNotificationSound()
    {
        try
        {
            _notificationSoundPreview ??= new GreetingAudioPlayer(Dispatcher, defaultSoundFactory: MainWindow.OpenNotificationSound);
            string path = SettingsRegistry.GetText(_options, "NotificationSoundPath", "");
            if (path.Length > 0 && !File.Exists(path)) path = "";
            try { _notificationSoundPreview.Play(path, SettingsRegistry.GetInt(_options, "NotificationSoundVolume", 25)); }
            catch when (path.Length > 0) { _notificationSoundPreview.Play("", SettingsRegistry.GetInt(_options, "NotificationSoundVolume", 25)); }
            _notificationSoundStatus!.Text = SettingsRegistry.GetInt(_options, "NotificationSoundVolume", 25) == 0
                ? L.T("通知音量为 0，当前静音。") : L.T("正在试听通知提示音。");
        }
        catch (Exception ex) { _notificationSoundStatus!.Text = L.T("无法试听：") + ex.Message; }
    }

    private void RefreshNotificationSoundSettings()
    {
        if (_notificationSoundHint == null) return;
        string path = SettingsRegistry.GetText(_options, "NotificationSoundPath", "");
        _notificationSoundHint.Text = path.Length == 0 ? L.T("内置轻柔提示音 · 约半秒")
            : File.Exists(path) ? L.T("已导入：") + Path.GetFileName(path) : L.T("自选文件不可用，通知时使用内置提示音");
        bool enabled = SettingsRegistry.GetBool(_options, "NotificationSoundEnabled", true);
        if (FindName("NotificationSoundPreview") is Button preview) preview.IsEnabled = enabled;
        if (!enabled) _notificationSoundPreview?.Stop();
        else _notificationSoundPreview?.SetVolume(SettingsRegistry.GetInt(_options, "NotificationSoundVolume", 25));
    }

    private void CleanupRetiredNotificationSounds()
    {
        string current = SettingsRegistry.GetText(_options, "NotificationSoundPath", "");
        foreach (string file in _retiredNotificationSounds)
            if (!string.Equals(file, current, StringComparison.OrdinalIgnoreCase)) _notificationAssets.RemoveOwnedAsset(file);
        _retiredNotificationSounds.Clear();
    }
}
