using alpha_island.Core;
using alpha_island.Services;

namespace alpha_island;

public partial class MainWindow
{
    private NowPlayingInfo? _gameDeferredMedia;
    private bool _gameHasDeferredMedia;
    private bool _gameBatteryTimerWasRunning;
    private void SyncGameResourceBudget(bool active)
    {
        _ticker.SetExecutionFilter(active ? GameResourcePolicy.AllowsTick : null);
        _services.SetSuspended(GameResourcePolicy.SuspendedServices, active);
        _plugins?.SetGameSuspended(active);
        _insights?.SetSuspended(active);
        if (active)
        {
            _lyricsCts?.Cancel();
            _lyricsFetchingKey = "";
            _mediaLostTimer?.Stop();
            _gameBatteryTimerWasRunning = _deviceBatteryViewTimer?.IsEnabled == true;
            _deviceBatteryViewTimer?.Stop();
        }
        else if (_gameBatteryTimerWasRunning)
        { _gameBatteryTimerWasRunning = false; _deviceBatteryViewTimer?.Start(); }
        ConfigureVoiceAssistant();
        SyncVolumeFlyoutTakeover();
        ApplyPowerSavePacing(); SyncHoverPacing();
        if (!active && _gameHasDeferredMedia)
        {
            var deferred = _gameDeferredMedia;
            _gameDeferredMedia = null; _gameHasDeferredMedia = false;
            OnNowPlayingChanged(this, deferred);
        }
        if (!active && _lyricsDoc == null && _smtc?.Current is { HasMedia: true } current) StartLyricsFetch(current);
    }
}
