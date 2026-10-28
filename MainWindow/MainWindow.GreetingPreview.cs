using alpha_island.Core;

namespace alpha_island;

public partial class MainWindow
{
    private bool _greetingPreviewActive;
    private bool _greetingPreviewSoundPlayed;
    private string? _greetingPreviewReturnKey, _greetingPreviewPreferredKey;
    private bool GreetingPresentationEnabled => _options.GreetingEnabled || _greetingPreviewActive;

    private void PreviewStartupGreeting()
    {
        if (_exiting || _sessionLocked) return;
        if (_greetingPending || GreetingActive) DismissGreeting();
        _greetingPreviewReturnKey = _activeMainIslandKey;
        _greetingPreviewPreferredKey = _layers.PreferredWinnerKey;
        _greetingPreviewSoundPlayed = _greetingSoundPlayed;
        _greetingPreviewActive = true;
        _greetingSoundPlayed = false;
        StopGreetingImageAnimation();
        ShowStartupGreeting();
        SetState(IslandState.Pill, force: true);
        ScheduleGreetingDismiss();
    }
}
