using System.Windows;
using alpha_island.Core;
using alpha_island.Services;

namespace alpha_island;

public partial class MainWindow
{
    private GreetingAudioPlayer? _greetingAudio;
    private bool _greetingSoundPlayed;
    private string? _greetingImagePath;

    private void ApplyGreetingMedia()
    {
        string path = SettingsRegistry.GetText(_options, "GreetingImagePath", "");
        if (_greetingImagePath != path)
        {
            _greetingImagePath = path;
            try { GreetingCustomImage.Source = string.IsNullOrWhiteSpace(path) ? null : GreetingMediaStore.LoadImage(path); }
            catch { GreetingCustomImage.Source = null; }
            bool custom = GreetingCustomImage.Source != null;
            GreetingCustomImage.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            GreetingIcon.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
        }
        if (!_options.GreetingEnabled || !SettingsRegistry.GetBool(_options, "GreetingSoundEnabled", true)) _greetingAudio?.Stop();
        else _greetingAudio?.SetVolume(SettingsRegistry.GetInt(_options, "GreetingSoundVolume", 35));
    }

    private void OnGreetingPresentationChanged(bool visible)
    {
        if (!visible) { _greetingAudio?.Stop(); return; }
        ApplyGreetingMedia();
        if (!_runtimeEnabled || !_options.GreetingEnabled || _greetingSoundPlayed || _sessionLocked || GameDoNotDisturbActive
            || !SettingsRegistry.GetBool(_options, "GreetingSoundEnabled", true)) return;
        _greetingSoundPlayed = true;
        try
        {
            _greetingAudio ??= new GreetingAudioPlayer(Dispatcher);
            string path = SettingsRegistry.GetText(_options, "GreetingSoundPath", "");
            if (!string.IsNullOrWhiteSpace(path) && !System.IO.File.Exists(path)) path = "";
            try { _greetingAudio.Play(path, SettingsRegistry.GetInt(_options, "GreetingSoundVolume", 35)); }
            catch (Exception ex) when (!string.IsNullOrWhiteSpace(path))
            {
                IslandLog.Warn("Greeting", "自选音效不可用，使用默认音效：" + ex.Message);
                _greetingAudio.Play("", SettingsRegistry.GetInt(_options, "GreetingSoundVolume", 35));
            }
        }
        catch (Exception ex) { IslandLog.Warn("Greeting", "欢迎音效播放失败：" + ex.Message); }
    }
}
