using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AlphaIsland.PluginSdk;
using AlphaIsland.Speech;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;
using Border = System.Windows.Controls.Border;
using Panel = System.Windows.Controls.Panel;
using Brush = System.Windows.Media.Brush;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using Binding = System.Windows.Data.Binding;
using Point = System.Windows.Point;

namespace alpha_island;

public partial class MainWindow
{
    private const string VoiceIslandKey = "voice-assistant", VoiceChatPage = "plugin:ai.api-chat/chat";
    private VoiceAssistantSession? _voiceAssistant;
    private VoiceAssistantPill? _voicePill;
    private VoiceAssistantHelpWindow? _voiceHelpWindow;
    private Action<string> _voiceOpenSettings = uri => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
    private Border? _voiceStopOrb;
    private GlyphIcon? _voiceStopGlyph;
    private TranslateTransform? _voiceStopTranslate;
    private IDisposable? _voiceShortcut;
    private HotkeyService? _voiceKeys;
    private bool _voiceKeysRunning, _voiceHoldEnabled, _voiceWasVisible;
    private string _voiceConfiguration = "", _voiceHotkeyStatus = "", _voicePreviousKey = "music";
    private IslandState _voicePreviousState;
    private readonly VoiceWakeGesture _voiceGesture = new();
    private DispatcherTimer? _voiceHoldTimer;
    private Key _voiceHoldKey = Key.RightCtrl;
    private readonly float[] _voiceSpectrumFrame = new float[MicrophoneSpectrum.Bands];
    private readonly object _voiceSpectrumGate = new();
    private int _voiceFrameQueued;
    private bool VoiceIslandVisible => _voiceAssistant?.Visible == true;
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);

    private void InitializeVoiceAssistant()
    {
        if (_voiceAssistant != null) return;
        _voicePill = new VoiceAssistantPill { Visibility = Visibility.Collapsed };
        ((Panel)NetSpeedNotifyLayer.Parent).Children.Add(_voicePill);
        _voiceAssistant = new(Dispatcher, CreateVoiceCapture,
            (id, text) => _plugins?.SubmitTextAsync(VoiceChatPage, id, text) ?? Task.FromResult(new PluginTextInputResult(false, "API 对话插件未就绪。")),
            id => _plugins?.CancelTextAsync(VoiceChatPage, id) ?? Task.CompletedTask,
            () => !_exiting && !_sessionLocked && !GameDoNotDisturbActive && _approvalCurrent == null
                && SettingsRegistry.GetBool(_options, "VoiceWakeEnabled", true) && _pages.IndexOf(VoiceChatPage) >= 0
                && SettingsRegistry.GetBool(_options, "AiEnabled", false),
            () => SettingsRegistry.GetBool(_options, "VoiceWakeSoundEnabled", true) ? VoiceWakeChime.PlayAsync() : Task.CompletedTask);
        _voiceAssistant.Changed += OnVoiceAssistantChanged;
        _voiceAssistant.Spectrum += OnVoiceSpectrum;
        _layers.Register(new IslandLayerSpec(VoiceIslandKey, _voicePill, Priority: 88, ForceActive: () => VoiceIslandVisible,
            OnVisibilityChanged: _ => SyncVoiceStopPosition()));
        _voiceStopGlyph = new GlyphIcon { Text = "\uE71A", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center };
        _voiceStopGlyph.SetResourceReference(GlyphIcon.ForegroundProperty, "TextPrimary");
        _voiceStopTranslate = new TranslateTransform();
        _voiceStopOrb = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1),
            BorderBrush = (Brush)FindResource("GlassEdgeBrush"), HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Top, Margin = new Thickness(0, IslandMetrics.PillTop + (IslandMetrics.PillHeight - 36) / 2, 0, 0),
            Child = _voiceStopGlyph, RenderTransform = _voiceStopTranslate, Visibility = Visibility.Collapsed, Cursor = Cursors.Hand };
        _voiceStopOrb.SetBinding(Border.BackgroundProperty, new Binding(nameof(Border.Background)) { Source = IslandBody });
        _voiceStopOrb.PreviewMouseLeftButtonDown += (_, args) =>
        {
            args.Handled = true;
            _ = _voiceAssistant.CancelAsync();
        };
        _voicePill.PreviewMouseLeftButtonUp += async (_, args) =>
        {
            args.Handled = true;
            if (_voiceAssistant.Snapshot.State == VoiceAssistantState.Complete)
            { await _voiceAssistant.CancelAsync(); SelectComponent(VoiceChatPage); }
            else if (_voiceAssistant.Snapshot.State == VoiceAssistantState.Error) ShowVoiceHelp();
        };
        Panel.SetZIndex(_voiceStopOrb, 5); IslandScene.Children.Add(_voiceStopOrb); RegisterLiquidChrome(_voiceStopOrb);
        _voicePill.IsVisibleChanged += (_, _) => SyncVoiceStopPosition();
        IslandBody.SizeChanged += (_, _) => SyncVoiceStopPosition();
        _voiceHoldTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(40) };
        _voiceHoldTimer.Tick += (_, _) =>
        {
            if (GetAsyncKeyState(KeyInterop.VirtualKeyFromKey(_voiceHoldKey)) >= 0 || _sessionLocked || GameDoNotDisturbActive)
            { _voiceGesture.Reset(); _voiceHoldTimer.Stop(); return; }
            if (_voiceGesture.Tick(Environment.TickCount64)) { _voiceHoldTimer.Stop(); _ = ToggleVoiceAssistantAsync(); }
        };
    }
    private IVoiceCapture CreateVoiceCapture()
    {
        var config = SpeechSettings.Read(_options);
        if (config.Provider == "windows-local") return new VoiceRecognitionService();
        SpeechSettings.Validate(config);
        return new ProviderVoiceCapture(SpeechRecognizerRegistry.Default.Create(config), new WasapiSpeechAudioSource());
    }
    private void ConfigureVoiceAssistant()
    {
        if (_voiceAssistant == null) return;
        bool enabled = !GameDoNotDisturbActive && !_sessionLocked && !_exiting
            && SettingsRegistry.GetBool(_options, "VoiceWakeEnabled", true) && SettingsRegistry.GetBool(_options, "AiEnabled", false);
        string shortcut = SettingsRegistry.GetText(_options, "VoiceWakeShortcut", "Ctrl+Alt+V");
        _voiceHoldEnabled = enabled && SettingsRegistry.GetBool(_options, "VoiceWakeHoldEnabled", false);
        string signature = $"{enabled}|{shortcut}|{_voiceHoldEnabled}";
        if (_voiceConfiguration != signature)
        {
            _voiceConfiguration = signature; _voiceShortcut?.Dispose(); _voiceShortcut = null;
            _voiceGesture.Configure(_voiceHoldKey = Key.RightCtrl);
            _voiceHoldTimer?.Stop();
            if (!enabled)
            {
                if (_voiceAssistant.Visible) _ = _voiceAssistant.CancelAsync();
                _voiceHotkeyStatus = GameDoNotDisturbActive ? "游戏期间暂停语音唤醒，结束后自动恢复。" : "配置并启用 API 对话后，可使用语音唤醒。";
            }
            else if (_runtimeEnabled)
            {
                try
                {
                    if (new KeyGestureConverter().ConvertFromInvariantString(shortcut) is not KeyGesture gesture || gesture.Key == Key.None
                        || (gesture.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0) throw new ArgumentException();
                    _voiceHotkeyStatus = HotkeyService.TryRegisterSystem(new(gesture.Modifiers, gesture.Key), () => _ = ToggleVoiceAssistantAsync(), out _voiceShortcut, out string error)
                        ? shortcut + " 已注册" : shortcut + "：" + error;
                }
                catch (Exception error) when (error is ArgumentException or NotSupportedException or FormatException)
                { _voiceHotkeyStatus = "语音快捷键格式无效，请使用 Ctrl+Alt+V 这样的组合。"; }
                if (_voiceHoldEnabled) _voiceHotkeyStatus += "；长按右 Ctrl 0.6 秒唤醒";
            }
        }
        SyncVoiceKeyHook(); _settingsWindow?.SetVoiceHotkeyStatus(_voiceHotkeyStatus);
    }
    private void SyncVoiceKeyHook()
    {
        bool required = _runtimeEnabled && !_exiting && !_sessionLocked && !GameDoNotDisturbActive && (_voiceHoldEnabled || VoiceIslandVisible);
        if (required == _voiceKeysRunning) return;
        if (required)
        {
            if (_voiceKeys == null)
            {
                _voiceKeys = new HotkeyService(Dispatcher);
                _voiceKeys.PreviewKeyPressed += (_, args) =>
                {
                    if (args.Key != Key.Escape || args.IsInjected || !VoiceIslandVisible) return;
                    args.Handled = true;
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { _voiceGesture.Reset(); _ = _voiceAssistant!.CancelAsync(); }));
                };
                _voiceKeys.KeyChanged += (_, args) =>
                {
                    if (!_voiceHoldEnabled || _sessionLocked || GameDoNotDisturbActive) { _voiceGesture.Reset(); return; }
                    _voiceGesture.KeyChanged(args.Key, args.IsKeyDown, args.Modifiers, Environment.TickCount64, args.IsInjected);
                    if (_voiceGesture.Pending) _voiceHoldTimer!.Start(); else _voiceHoldTimer!.Stop();
                };
            }
            _voiceKeys.Start();
        }
        else { _voiceKeys?.Stop(); _voiceHoldTimer?.Stop(); _voiceGesture.Reset(); }
        _voiceKeysRunning = required;
    }
    private Task ToggleVoiceAssistantAsync()
    {
        if (_exiting || _sessionLocked || GameDoNotDisturbActive || _approvalCurrent != null) return Task.CompletedTask;
        return _voiceAssistant?.ToggleAsync() ?? Task.CompletedTask;
    }
    private void OnVoiceAssistantChanged(VoiceAssistantSnapshot snapshot)
    {
        if (_exiting || _voicePill == null) return;
        if (snapshot.State is VoiceAssistantState.Idle or VoiceAssistantState.Starting) _voiceHelpWindow?.Close();
        if (snapshot.State != VoiceAssistantState.Idle && (_sessionLocked || GameDoNotDisturbActive))
        { _ = _voiceAssistant!.CancelAsync(); return; }
        bool visible = snapshot.State != VoiceAssistantState.Idle;
        _voicePill.Update(snapshot);
        if (visible)
        {
            bool entering = !_voiceWasVisible;
            if (entering) { _voicePreviousKey = _activeMainIslandKey; _voicePreviousState = _state; }
            _voiceWasVisible = true;
            if (_greetingShowing || GreetingActive) DismissGreeting();
            if (entering)
            {
                HidePinBubble(immediate: true);
                _layers.Request(IslandNotification.While(VoiceIslandKey, () => VoiceIslandVisible, deferredInCard: false), requestSync: false);
                _activeMainIslandKey = VoiceIslandKey; _layers.PreferredWinnerKey = VoiceIslandKey; SetState(IslandState.Pill);
            }
        }
        else if (_voiceWasVisible)
        {
            _voiceWasVisible = false; _layers.Hide(VoiceIslandKey);
            if (_activeMainIslandKey == VoiceIslandKey)
            {
                _activeMainIslandKey = CanRetainIsland(_voicePreviousKey) ? _voicePreviousKey : "music";
                _layers.PreferredWinnerKey = _activeMainIslandKey;
            }
            SyncNotificationLayers();
            if (!GameDoNotDisturbActive && !_sessionLocked && _voicePreviousState == IslandState.Card) SetState(IslandState.Card);
            else if (!HasActiveNotification && !HasVisibleMusicPin && !_layers.AnyPinned && !KeepPlayingMusicIslandVisible) CollapseToBar();
        }
        SyncVoiceStopPosition(); SyncVoiceKeyHook();
    }
    private void ShowVoiceHelp()
    {
        if (_sessionLocked || _exiting || _voiceAssistant?.Snapshot.State != VoiceAssistantState.Error) return;
        if (_voiceHelpWindow != null) { Interop.UtilityWindowPresenter.Show(_voiceHelpWindow); return; }
        _voiceAssistant.HoldFeedback();
        var problem = _voiceAssistant.Snapshot.Problem ?? VoiceRecognitionProblem.FromMessage(_voiceAssistant.Snapshot.Hint);
        var window = _voiceHelpWindow = new VoiceAssistantHelpWindow(problem, () =>
        {
            if (_sessionLocked || _exiting) return;
            if (problem.SettingsUri == "island:speech") { ShowSettings(); _settingsWindow?.OpenSpeechSettings(); }
            else if (problem.SettingsUri.Length > 0) _voiceOpenSettings(problem.SettingsUri);
            else { ShowSettings(); _settingsWindow?.OpenPluginSettings(); }
        }, () => _ = ToggleVoiceAssistantAsync());
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_voiceHelpWindow, window)) _voiceHelpWindow = null;
            if (_voiceAssistant.Snapshot.State == VoiceAssistantState.Error) _ = _voiceAssistant.CancelAsync();
        };
        Interop.UtilityWindowPresenter.Show(window);
    }
    private bool IsPointOnVoiceStop(int screenX, int screenY)
    {
        if (_voiceStopOrb?.IsVisible != true || _voiceStopOrb.ActualWidth <= 0) return false;
        try
        {
            var center = _voiceStopOrb.PointToScreen(new Point(_voiceStopOrb.ActualWidth / 2, _voiceStopOrb.ActualHeight / 2));
            var edge = _voiceStopOrb.PointToScreen(new Point(_voiceStopOrb.ActualWidth, _voiceStopOrb.ActualHeight / 2));
            return (new Point(screenX, screenY) - center).Length <= (edge - center).Length;
        }
        catch (InvalidOperationException) { return false; }
    }
    private void SyncVoiceStopPosition()
    {
        if (_voiceStopOrb == null || _voicePill == null || _voiceStopTranslate == null) return;
        bool visible = VoiceIslandVisible && _state == IslandState.Pill && _voicePill.IsVisible && !_sessionLocked && !GameDoNotDisturbActive;
        _voiceStopOrb.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _voiceStopTranslate.X = IslandBody.ActualWidth / 2 + 10 + 18;
        _voiceStopGlyph!.Text = _voiceAssistant?.Active == true ? "\uE71A" : "\uE711";
        _voiceStopOrb.ToolTip = _voiceAssistant?.Active == true ? "取消本次语音指令（Esc）" : "关闭语音提示";
    }
    private void OnVoiceSpectrum(float[] frame)
    {
        lock (_voiceSpectrumGate) frame.AsSpan(0, Math.Min(frame.Length, _voiceSpectrumFrame.Length)).CopyTo(_voiceSpectrumFrame);
        if (Interlocked.Exchange(ref _voiceFrameQueued, 1) != 0 || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            Interlocked.Exchange(ref _voiceFrameQueued, 0);
            if (_voiceAssistant?.Snapshot.State != VoiceAssistantState.Recording) return;
            lock (_voiceSpectrumGate) _voicePill?.Wave.SetFrame(_voiceSpectrumFrame);
        }));
    }
    private void StopVoiceAssistant()
    {
        _voiceHelpWindow?.Close();
        _voiceShortcut?.Dispose(); _voiceShortcut = null; _voiceHoldTimer?.Stop();
        _voiceKeys?.Dispose(); _voiceKeys = null; _voiceAssistant?.Dispose();
    }
}
