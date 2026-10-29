using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Drawing;
using System.Windows.Controls;
using System.Windows.Threading;
using AlphaIsland.Speech;
using alpha_island.Core;
using alpha_island.Services;
using ComboBox = System.Windows.Controls.ComboBox;

namespace alpha_island.Controls;

internal enum ScreenCaptionMode { SpeechRecognition, RegionTranslation }

internal sealed partial class ScreenToolsWindow
{
    private readonly ComboBox _captionSource = new();
    private readonly Func<SpeechRecognitionOptions> _speechConfiguration;
    private readonly Func<string> _subtitleLanguage;
    private readonly Action<bool>? _subtitleStateChanged;
    private readonly Func<ISpeechRecognizer, bool, ProviderVoiceCapture> _createSpeechCapture;
    private readonly Func<string, ChatConfig, CancellationToken, string, Task<string>> _translateCaption;
    private readonly Func<Rectangle, CancellationToken, Task<string>> _readCaptionRegion;
    private readonly Func<bool> _canUseTools;
    private ProviderVoiceCapture? _captionCapture;
    private CancellationTokenSource? _captionLifetime;
    private Rectangle? _captionRegion;
    private ScreenCaptionMode _captionMode;
    private bool _subtitleRunning, _pickingSubtitle, _translatingCaption, _readingCaptionRegion;
    private string? _nextCaption;
    private int _captionTranslationGeneration, _captionReadGeneration;
    private readonly Queue<string> _captionSegments = new();
    internal bool SubtitlesRunning => _subtitleRunning;
    internal bool SpeechRecognitionRunning => _subtitleRunning && _captionMode == ScreenCaptionMode.SpeechRecognition;
    internal bool RegionTranslationRunning => _subtitleRunning && _captionMode == ScreenCaptionMode.RegionTranslation;

    private async Task SelectSubtitleRegionAsync()
    {
        if (_busy || _pickingSubtitle || _closed || !_canUseTools()) return;
        StopSubtitles(); _captionMode = ScreenCaptionMode.RegionTranslation; _pickingSubtitle = true;
        int generation = _generation;
        var lifetime = _captionLifetime = new CancellationTokenSource(); var token = lifetime.Token;
        RefreshCaptionControls();
        try
        {
            var selected = await PickAsync(token);
            if (generation != _generation || _closed) return;
            if (selected.HasValue) { _captionRegion = selected; _status.Text = L.T("已选择翻译区域，点击“开始区域翻译”即可开始。"); }
        }
        catch (OperationCanceledException) { }
        finally { if (generation == _generation) { _pickingSubtitle = false; RefreshCaptionControls(); } }
    }

    internal Task StartOrStopSpeechRecognitionAsync() => StartOrStopCaptionAsync(ScreenCaptionMode.SpeechRecognition);
    internal Task StartOrStopRegionTranslationAsync() => StartOrStopCaptionAsync(ScreenCaptionMode.RegionTranslation);

    private async Task StartOrStopCaptionAsync(ScreenCaptionMode mode)
    {
        if ((_pickingSubtitle || _subtitleRunning) && _captionMode == mode) { StopSubtitles(); return; }
        if (_busy || _closed || !_canUseTools()) return;
        bool restore = IsVisible, showError = false;
        StopSubtitles(); _captionMode = mode;
        int generation = ++_generation;
        var lifetime = _captionLifetime = new CancellationTokenSource(); var token = lifetime.Token;
        _pickingSubtitle = true; _lastText = ""; _captionSegments.Clear(); RefreshCaptionControls();
        try
        {
            if (mode == ScreenCaptionMode.RegionTranslation)
            {
                if (!_configuration().IsUsable) throw new InvalidOperationException(L.T("请先配置区域翻译使用的对话接口和模型。"));
                if (!_captionRegion.HasValue)
                {
                    Hide(); await Task.Delay(150, token);
                    var selected = _pickRegion(token);
                    if (!selected.HasValue) { _status.Text = L.T("已取消区域翻译，未开始读取屏幕。"); return; }
                    token.ThrowIfCancellationRequested();
                    if (_closed || generation != _generation) return;
                    _captionRegion = selected;
                }
            }
            else
            {
                var options = _speechConfiguration();
                if (options.Provider == "windows-local")
                    throw new InvalidOperationException(L.T("持续语音识别请选择 MiMo、兼容接口或本地语音服务；Windows 本机语音仍可用于助手麦克风指令。"));
                SpeechSettings.Validate(options);
                var capture = _captionCapture = _createSpeechCapture(SpeechRecognizerRegistry.Default.Create(options), _captionSource.SelectedIndex == 0);
                capture.SegmentRecognized += text => DispatchCaption(() =>
                {
                    if (generation != _generation || !SpeechRecognitionRunning) return;
                    _original.Text = text; ShowCaptionText(text);
                    _status.Text = L.T("语音识别中 · ") + DateTime.Now.ToString("HH:mm:ss");
                });
                capture.Failed += message => DispatchCaption(() => { if (generation == _generation) CaptionFailed(message); });
                await capture.StartAsync(token);
            }
            token.ThrowIfCancellationRequested();
            if (generation != _generation || _closed) return;
            if (!_canUseTools()) throw new OperationCanceledException(token);
            _original.Clear(); _result.Clear();
            _pickingSubtitle = false; _subtitleRunning = true; RefreshCaptionControls();
            _subtitleStateChanged?.Invoke(true); Hide();
            if (generation != _generation || !_subtitleRunning || _closed) return;
            _status.Text = mode == ScreenCaptionMode.RegionTranslation ? L.T("区域翻译中 · 关闭工具窗口后仍继续") : L.T("语音识别中 · 直接显示原文，关闭工具窗口后仍继续");
            ShowSubtitle(mode == ScreenCaptionMode.RegionTranslation ? L.T("正在读取所选区域…") : _captionSource.SelectedIndex == 0 ? L.T("正在识别系统声音，等待语音…") : L.T("正在识别麦克风，等待语音…"));
            if (mode == ScreenCaptionMode.RegionTranslation) { _timer.Start(); await ReadAndTranslateAsync(); }
        }
        catch (OperationCanceledException)
        { if (generation == _generation) { StopSubtitles(); _status.Text = L.T("已停止启动"); } }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (generation == _generation)
            {
                StopSubtitles(); _status.Text = error is InvalidOperationException ? error.Message : L.T("启动失败，请检查当前模式所需的接口配置。");
                showError = true;
            }
        }
        finally
        {
            if (generation == _generation) { _pickingSubtitle = false; RefreshCaptionControls(); }
            if ((generation == _generation || showError) && !_closed && !_subtitleRunning && !_pickingSubtitle && (restore || showError) && _canUseTools())
                Interop.UtilityWindowPresenter.Show(this);
        }
    }

    private void RefreshCaptionControls()
    {
        _liveButton.Content = _captionMode == ScreenCaptionMode.SpeechRecognition && _pickingSubtitle ? L.T("取消启动语音识别")
            : SpeechRecognitionRunning ? L.T("停止语音识别") : L.T("开始语音识别");
        _regionLiveButton.Content = _captionMode == ScreenCaptionMode.RegionTranslation && _pickingSubtitle ? L.T("取消启动区域翻译")
            : RegionTranslationRunning ? L.T("停止区域翻译") : L.T("开始区域翻译");
        _captionSource.IsEnabled = !_pickingSubtitle && !SpeechRecognitionRunning;
    }

    private void ShowCaptionText(string text)
    {
        if (_captionMode == ScreenCaptionMode.RegionTranslation) _captionSegments.Clear();
        _captionSegments.Enqueue(text);
        while (_captionSegments.Count > 3 || _captionSegments.Count > 1 && _captionSegments.Sum(s => s.Length) > 260) _captionSegments.Dequeue();
        _result.Text = string.Join(Environment.NewLine, _captionSegments); ShowSubtitle(_result.Text);
    }

    private void DispatchCaption(Action action)
    { if (!_closed && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(DispatcherPriority.Background, action); }

    private async Task ReadAndTranslateAsync()
    {
        if (!RegionTranslationRunning || !_captionRegion.HasValue || !_timer.IsEnabled || _readingCaptionRegion || _captionLifetime == null) return;
        if (!_canUseTools()) { StopSubtitles(); return; }
        int generation = _generation; var token = _captionLifetime.Token;
        _readingCaptionRegion = true; _captionReadGeneration = generation;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            string text = await _readCaptionRegion(_captionRegion.Value, timeout.Token);
            token.ThrowIfCancellationRequested();
            if (generation != _generation || !RegionTranslationRunning || string.IsNullOrWhiteSpace(text) || text == _lastText) return;
            _original.Text = text; _nextCaption = text; await TranslateCaptionQueueAsync(generation, token);
        }
        catch (OperationCanceledException) { if (!token.IsCancellationRequested && generation == _generation) CaptionFailed(L.T("屏幕文字读取超时，请重新框选区域后开始。")); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (generation == _generation && !token.IsCancellationRequested) CaptionFailed(error is InvalidOperationException ? error.Message : L.T("屏幕文字读取失败，请重新框选区域。")); }
        finally { if (_captionReadGeneration == generation) _readingCaptionRegion = false; }
    }

    private async Task TranslateCaptionQueueAsync(int generation, CancellationToken token)
    {
        if (_translatingCaption && _captionTranslationGeneration == generation) return;
        _translatingCaption = true; _captionTranslationGeneration = generation;
        try
        {
            while (_nextCaption is { } text && RegionTranslationRunning && generation == _generation)
            {
                _nextCaption = null;
                if (text == _lastText) continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                string translated = await _translateCaption(text, _configuration(), timeout.Token, _subtitleLanguage());
                token.ThrowIfCancellationRequested();
                if (!RegionTranslationRunning || generation != _generation || _closed) return;
                _lastText = text; ShowCaptionText(translated); _status.Text = L.T("区域翻译已更新 · ") + DateTime.Now.ToString("HH:mm:ss");
            }
        }
        catch (OperationCanceledException) { if (!token.IsCancellationRequested && generation == _generation) CaptionFailed(L.T("区域翻译超时，请检查翻译接口后重新开始。")); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (generation == _generation && !token.IsCancellationRequested) CaptionFailed(error is InvalidOperationException ? error.Message : L.T("区域翻译连接中断，请检查翻译接口。")); }
        finally
        {
            if (_captionTranslationGeneration == generation) _translatingCaption = false;
            if (_nextCaption != null && RegionTranslationRunning && !token.IsCancellationRequested && generation == _generation) _ = TranslateCaptionQueueAsync(generation, token);
        }
    }

    private void CaptionFailed(string message)
    { StopSubtitles(); _status.Text = message; if (!_closed && _canUseTools()) Interop.UtilityWindowPresenter.Show(this); }

    internal void StopSubtitles()
    {
        bool wasRunning = _subtitleRunning, wasStarting = _pickingSubtitle; _subtitleRunning = false; _pickingSubtitle = false; ++_generation;
        _timer.Stop(); _operation?.Cancel(); _captionLifetime?.Cancel(); _captionLifetime?.Dispose(); _captionLifetime = null;
        _captionCapture?.Dispose(); _captionCapture = null; _nextCaption = null; _captionSegments.Clear(); _readingCaptionRegion = false;
        RefreshCaptionControls();
        var subtitle = _subtitle; _subtitle = null; subtitle?.Close();
        if (wasRunning || wasStarting) _status.Text = _captionMode == ScreenCaptionMode.SpeechRecognition ? L.T("语音识别已停止") : L.T("区域翻译已停止");
        if (wasRunning) _subtitleStateChanged?.Invoke(false);
    }
}
