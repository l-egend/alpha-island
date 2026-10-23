using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AlphaIsland.Speech;
using alpha_island.Core;
using alpha_island.Services;
using ComboBox = System.Windows.Controls.ComboBox;

namespace alpha_island.Controls;

internal sealed partial class ScreenToolsWindow
{
    private readonly ComboBox _captionSource = new();
    private readonly Func<SpeechRecognitionOptions> _speechConfiguration;
    private readonly Func<string> _subtitleLanguage;
    private readonly Action<bool>? _subtitleStateChanged;
    private readonly Func<ISpeechRecognizer, bool, ProviderVoiceCapture> _createSpeechCapture;
    private readonly Func<string, ChatConfig, CancellationToken, string, Task<string>> _translateCaption;
    private readonly Func<bool> _canUseTools;
    private ProviderVoiceCapture? _captionCapture;
    private CancellationTokenSource? _captionLifetime;
    private bool _subtitleRunning, _pickingSubtitle, _translatingCaption;
    private string? _nextCaption;
    private int _captionTranslationGeneration;
    private readonly Queue<string> _translatedSegments = new();
    internal bool SubtitlesRunning => _subtitleRunning;

    private async Task SelectSubtitleRegionAsync()
    {
        if (_busy || _pickingSubtitle || _closed) return;
        StopSubtitles(); _pickingSubtitle = true;
        _captionLifetime?.Dispose(); _captionLifetime = new();
        try { var selected = await PickAsync(_captionLifetime.Token); if (selected.HasValue) { _region = selected; _status.Text = "已选择字幕区域，点击“开始字幕”即可开始。"; } }
        catch (OperationCanceledException) { }
        finally { _pickingSubtitle = false; }
    }
    internal async Task StartOrStopSubtitlesAsync()
    {
        if (_subtitleRunning || _pickingSubtitle) { StopSubtitles(); return; }
        if (_busy || _closed || !_canUseTools()) return;
        int generation = ++_generation;
        _captionLifetime?.Dispose(); var lifetime = _captionLifetime = new CancellationTokenSource();
        _pickingSubtitle = true;
        try
        {
            if (!_region.HasValue)
            {
                Hide(); await Task.Delay(150, lifetime.Token);
                var selected = _pickRegion(lifetime.Token);
                if (!selected.HasValue || _closed || generation != _generation)
                { _status.Text = "已取消字幕，未开始采集音频。"; return; }
                _region = selected;
            }
            lifetime.Token.ThrowIfCancellationRequested();
            if (!_configuration().IsUsable) throw new InvalidOperationException("请先配置翻译使用的对话接口和模型。");
            if (_captionSource.SelectedIndex != 2)
            {
                var options = _speechConfiguration();
                if (options.Provider == "windows-local") throw new InvalidOperationException("Windows 本机语音只用于麦克风指令；声音字幕请选择 MiMo、兼容接口或本地语音服务。也可使用屏幕文字来源。");
                SpeechSettings.Validate(options);
                var capture = _captionCapture = _createSpeechCapture(SpeechRecognizerRegistry.Default.Create(options), _captionSource.SelectedIndex == 0);
                capture.SegmentRecognized += text => DispatchCaption(() =>
                { if (generation == _generation && _subtitleRunning) { _original.Text = text; _nextCaption = text; _ = TranslateCaptionQueueAsync(generation, lifetime.Token); } });
                capture.Failed += message => DispatchCaption(() =>
                { if (generation == _generation) { StopSubtitles(); _status.Text = message; if (!_closed) Interop.UtilityWindowPresenter.Show(this); } });
                await capture.StartAsync(lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested();
                if (generation != _generation) return;
            }
            _lastText = ""; _translatedSegments.Clear(); _subtitleRunning = true; _liveButton.Content = "停止字幕";
            _subtitleStateChanged?.Invoke(true); Hide();
            _status.Text = _captionSource.SelectedIndex == 2 ? "正在识别屏幕字幕 · 可右键翻译岛停止" : "正在听取声音并分段识别 · 翻译岛出现后可右键停止";
            if (_captionSource.SelectedIndex == 2) { _timer.Start(); await ReadAndTranslateAsync(); }
        }
        catch (OperationCanceledException) { if (generation == _generation) { StopSubtitles(); _status.Text = "字幕已取消"; } }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (generation == _generation) { StopSubtitles(); _status.Text = error is InvalidOperationException ? error.Message : "字幕启动失败，请检查语音和翻译配置。"; } }
        finally
        {
            _pickingSubtitle = false;
            if (!_closed && !_subtitleRunning && _canUseTools()) Interop.UtilityWindowPresenter.Show(this);
        }
    }
    private void DispatchCaption(Action action)
    { if (!_closed && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(DispatcherPriority.Background, action); }
    private async Task TranslateCaptionQueueAsync(int generation, CancellationToken token)
    {
        if (_translatingCaption && _captionTranslationGeneration == generation) return;
        _translatingCaption = true; _captionTranslationGeneration = generation;
        try
        {
            while (_nextCaption is { } text && _subtitleRunning && generation == _generation)
            {
                _nextCaption = null;
                if (text == _lastText) continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                string translated = await _translateCaption(text, _configuration(), timeout.Token, _subtitleLanguage());
                token.ThrowIfCancellationRequested();
                if (!_subtitleRunning || generation != _generation || _closed) return;
                _lastText = text; _result.Text = translated;
                _translatedSegments.Enqueue(translated);
                while (_translatedSegments.Count > 3 || _translatedSegments.Count > 1 && _translatedSegments.Sum(s => s.Length) > 260) _translatedSegments.Dequeue();
                ShowSubtitle(string.Join(Environment.NewLine, _translatedSegments));
                _status.Text = "字幕已更新 · " + DateTime.Now.ToString("HH:mm:ss");
            }
        }
        catch (OperationCanceledException) { if (!token.IsCancellationRequested && generation == _generation) CaptionFailed("字幕翻译超时，请检查对话接口后重新开始。"); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (generation == _generation && !token.IsCancellationRequested) CaptionFailed(error is InvalidOperationException ? error.Message : "字幕翻译连接中断，请检查对话接口。"); }
        finally
        {
            if (_captionTranslationGeneration == generation) _translatingCaption = false;
            if (_nextCaption != null && _subtitleRunning && !token.IsCancellationRequested && generation == _generation) _ = TranslateCaptionQueueAsync(generation, token);
        }
    }
    private void CaptionFailed(string message)
    { StopSubtitles(); _status.Text = message; if (!_closed && _canUseTools()) Interop.UtilityWindowPresenter.Show(this); }
    internal void StopSubtitles()
    {
        bool wasRunning = _subtitleRunning; _subtitleRunning = false; ++_generation;
        _timer.Stop(); _operation?.Cancel(); _captionLifetime?.Cancel();
        _captionCapture?.Dispose(); _captionCapture = null; _nextCaption = null; _translatedSegments.Clear();
        _liveButton.Content = "开始字幕";
        var subtitle = _subtitle; _subtitle = null; subtitle?.Close();
        if (wasRunning) _subtitleStateChanged?.Invoke(false);
    }
}
