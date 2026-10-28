using System.Windows;
using System.Windows.Threading;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Controls;
using alpha_island.Services;

namespace alpha_island;

public partial class MainWindow
{
    private GreetingAudioPlayer? _greetingAudio;
    private bool _greetingSoundPlayed;
    private string? _greetingImagePath;
    private GreetingGifFrame[] _greetingFrames = [];
    private DispatcherTimer? _greetingImageTimer;
    private int _greetingFrameIndex;
    private bool _greetingPresentationVisible;
    private bool _greetingAnimationLoaded;
    private bool _greetingAnimationCompleted;
    private string? _greetingRandomPreset;
    private string GreetingPresetForPresentation => GreetingPresetCatalog.GetId(_options) is var id && id == GreetingPresetCatalog.Random
        ? _greetingRandomPreset ??= GreetingPresetCatalog.ChooseRandomPreset() : id;

    private void ApplyGreetingMedia()
    {
        string path = GreetingPresetCatalog.ImageKey(_options, GreetingPresetForPresentation);
        bool animate = _greetingPresentationVisible && !MotionPolicy.InstantMorph && !MotionPolicy.PowerSave
            && !SettingsRegistry.GetBool(_options, "ReduceEffectsEnabled", false);
        if (_greetingImagePath != path || animate && !_greetingAnimationLoaded)
        {
            if (_greetingImagePath != path) RemoveGreetingPortrait();
            _greetingImagePath = path;
            StopGreetingImageAnimation();
            try { _greetingFrames = GreetingPresetCatalog.LoadFrames(path, firstFrameOnly: !animate); }
            catch { _greetingFrames = []; }
            _greetingAnimationLoaded = animate;
            GreetingCustomImage.Source = _greetingFrames.Length > 0 ? _greetingFrames[0].Image : null;
            bool custom = GreetingCustomImage.Source != null;
            GreetingCustomImage.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            GreetingIcon.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
        }
        GreetingIcon.AnimationEnabled = !SettingsRegistry.GetBool(_options, "ReduceEffectsEnabled", false);
        GreetingIcon.AvailableDuration = TimeSpan.FromSeconds(Math.Clamp(_options.GreetingSeconds - .35, 1.1, WelcomeLogoIcon.PlaybackDuration.TotalSeconds));
        GreetingIcon.SetPresentation(_greetingPresentationVisible && GreetingPresentationEnabled && GreetingIcon.Visibility == Visibility.Visible);
        SyncGreetingPortrait();
        SyncGreetingImageAnimation();
        if (!GreetingPresentationEnabled || !SettingsRegistry.GetBool(_options, "GreetingSoundEnabled", true)) _greetingAudio?.Stop();
        else _greetingAudio?.SetVolume(SettingsRegistry.GetInt(_options, "GreetingSoundVolume", 35));
    }

    private void SyncGreetingImageAnimation()
    {
        if (_greetingAnimationCompleted)
        {
            ReleaseGreetingImageTimer();
            return;
        }
        bool shouldAnimate = _greetingPresentationVisible
            && GreetingPresentationEnabled
            && _greetingFrames.Length > 1
            && !MotionPolicy.InstantMorph
            && !MotionPolicy.PowerSave
            && !SettingsRegistry.GetBool(_options, "ReduceEffectsEnabled", false);
        if (!shouldAnimate)
        {
            ReleaseGreetingImageTimer();
            if (_greetingFrames.Length > 0)
                GreetingCustomImage.Source = _greetingFrames[Math.Clamp(_greetingFrameIndex, 0, _greetingFrames.Length - 1)].Image;
            return;
        }
        if (_greetingImageTimer == null)
        {
            _greetingImageTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher);
            _greetingImageTimer.Tick += OnGreetingImageTick;
        }
        _greetingImageTimer.Interval = _greetingFrames[_greetingFrameIndex].Delay;
        if (!_greetingImageTimer.IsEnabled) _greetingImageTimer.Start();
    }

    private void OnGreetingImageTick(object? sender, EventArgs e)
    {
        if (_greetingAnimationCompleted || _greetingFrames.Length <= 1) { ReleaseGreetingImageTimer(); return; }
        _greetingFrameIndex = Math.Min(_greetingFrameIndex + 1, _greetingFrames.Length - 1);
        GreetingCustomImage.Source = _greetingFrames[_greetingFrameIndex].Image;
        _greetingPortrait?.InvalidateVisual();
        if (_greetingFrameIndex == _greetingFrames.Length - 1)
        {
            _greetingAnimationCompleted = true;
            ReleaseGreetingImageTimer();
            return;
        }
        if (_greetingImageTimer != null) _greetingImageTimer.Interval = _greetingFrames[_greetingFrameIndex].Delay;
    }

    private void StopGreetingImageAnimation()
    {
        ReleaseGreetingImageTimer();
        _greetingFrameIndex = 0;
        _greetingAnimationCompleted = false;
    }

    private void ReleaseGreetingImageTimer()
    {
        if (_greetingImageTimer != null)
        {
            _greetingImageTimer.Stop();
            _greetingImageTimer.Tick -= OnGreetingImageTick;
            _greetingImageTimer = null;
        }
    }

    private void OnGreetingPresentationChanged(bool visible)
    {
        _greetingPresentationVisible = visible;
        if (!visible)
        {
            GreetingIcon.SetPresentation(false);
            RemoveGreetingPortrait();
            StopGreetingImageAnimation();
            if (_greetingFrames.Length > 0) GreetingCustomImage.Source = _greetingFrames[0].Image;
            if (GreetingPresetCatalog.IsBuiltInAnimation(_greetingImagePath) && _greetingFrames.Length > 1)
                _greetingFrames = [_greetingFrames[0]];
            _greetingAnimationLoaded = false;
            _greetingAudio?.Stop();
            return;
        }
        ApplyGreetingMedia();
        if (!_runtimeEnabled || !GreetingPresentationEnabled || _greetingSoundPlayed || _sessionLocked || GameDoNotDisturbActive
            || !SettingsRegistry.GetBool(_options, "GreetingSoundEnabled", true)) return;
        _greetingSoundPlayed = true;
        try
        {
            _greetingAudio ??= new GreetingAudioPlayer(Dispatcher);
            string path = GreetingPresetCatalog.SoundKey(_options, GreetingPresetForPresentation);
            if (!GreetingPresetCatalog.IsBuiltInSound(path) && !string.IsNullOrWhiteSpace(path) && !System.IO.File.Exists(path)) path = "";
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
