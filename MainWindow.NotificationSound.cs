using System.IO;
using alpha_island.Core;
using alpha_island.Services;

namespace alpha_island;

public partial class MainWindow
{
    private GreetingAudioPlayer? _notificationAudio;
    private readonly NotificationSoundPolicy _notificationSoundPolicy = new();

    internal static Stream OpenNotificationSound() => typeof(MainWindow).Assembly.GetManifestResourceStream("AlphaIsland.NotificationGentle.wav")
        ?? throw new FileNotFoundException("内置通知音效不可用。");

    private void ApplyNotificationSoundOptions()
    {
        if (!SettingsRegistry.GetBool(_options, "NotificationSoundEnabled", true) || GameDoNotDisturbActive
            || SettingsRegistry.GetBool(_options, "NotificationQuietMode", false)) _notificationAudio?.Stop();
        else _notificationAudio?.SetVolume(SettingsRegistry.GetInt(_options, "NotificationSoundVolume", 25));
    }

    private void PlayNotificationSound(IslandNotification notification, bool alreadyActive)
    {
        if (!_runtimeEnabled || _exiting || !NotificationSoundPolicy.Enabled(_options, _sessionLocked, GameDoNotDisturbActive)
            || !_notificationSoundPolicy.TryAccept(notification, alreadyActive, Environment.TickCount64)) return;
        try
        {
            _notificationAudio ??= new GreetingAudioPlayer(Dispatcher, defaultSoundFactory: OpenNotificationSound);
            string path = SettingsRegistry.GetText(_options, "NotificationSoundPath", "");
            try { _notificationAudio.Play(path, SettingsRegistry.GetInt(_options, "NotificationSoundVolume", 25)); }
            catch (Exception ex) when (path.Length > 0)
            {
                IslandLog.Warn("NotificationSound", "自选音效不可用，使用默认提示音：" + ex.Message);
                _notificationAudio.Play("", SettingsRegistry.GetInt(_options, "NotificationSoundVolume", 25));
            }
        }
        catch (Exception ex) { IslandLog.Warn("NotificationSound", "提示音播放失败：" + ex.Message); }
    }
}
