using System.IO;
using alpha_island.Core;
using System.Windows.Threading;
using NAudio.Wave;

namespace alpha_island.Services;

/// <summary>A single bounded playback, with no device, stream or timer retained after stopping.</summary>
internal sealed class GreetingAudioPlayer(Dispatcher dispatcher, Func<IWavePlayer>? outputFactory = null, Func<Stream>? defaultSoundFactory = null) : IDisposable
{
    private IWavePlayer? _output;
    private WaveStream? _stream;
    private EventHandler<StoppedEventArgs>? _stopped;
    private DispatcherTimer? _deadline;
    internal bool IsPlaying => _output?.PlaybackState == NAudio.Wave.PlaybackState.Playing;

    internal static Stream OpenDefaultSound() => typeof(GreetingAudioPlayer).Assembly.GetManifestResourceStream("AlphaIsland.StartupGentle.wav")
        ?? throw new FileNotFoundException("内置欢迎音效不可用。");

    internal void Play(string path, int volume)
    {
        dispatcher.VerifyAccess(); Stop();
        if (volume <= 0) return;
        try
        {
            _stream = GreetingPresetCatalog.IsBuiltInSound(path) ? new WaveFileReader(GreetingPresetCatalog.OpenBuiltInSound(path))
                : string.IsNullOrWhiteSpace(path) ? new WaveFileReader(defaultSoundFactory?.Invoke() ?? OpenDefaultSound()) : new AudioFileReader(path);
            if (_stream.TotalTime <= TimeSpan.Zero || _stream.TotalTime.TotalSeconds > GreetingMediaStore.MaxSoundSeconds)
                throw new InvalidDataException("欢迎音效长度不能超过 10 秒。");
            var output = outputFactory?.Invoke() ?? new WaveOutEvent(); _output = output;
            _stopped = (_, _) => { if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(() => { if (ReferenceEquals(_output, output)) Stop(); }); };
            output.PlaybackStopped += _stopped;
            output.Init(_stream); SetVolume(volume);
            _deadline = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(11) };
            _deadline.Tick += OnDeadline; _deadline.Start(); output.Play();
        }
        catch { Stop(); throw; }
    }

    internal void SetVolume(int volume)
    {
        dispatcher.VerifyAccess();
        if (_output != null) _output.Volume = Math.Clamp(volume, 0, 100) / 100f;
    }

    private void OnDeadline(object? sender, EventArgs args) => Stop();
    internal void Stop()
    {
        dispatcher.VerifyAccess();
        if (_deadline != null) { _deadline.Stop(); _deadline.Tick -= OnDeadline; _deadline = null; }
        var output = _output; _output = null;
        if (output != null)
        {
            if (_stopped != null) output.PlaybackStopped -= _stopped;
            try { output.Stop(); } catch (Exception ex) { IslandLog.Warn("Greeting", "停止音效失败：" + ex.Message); }
            try { output.Dispose(); } catch (Exception ex) { IslandLog.Warn("Greeting", "释放音效失败：" + ex.Message); }
        }
        _stopped = null; _stream?.Dispose(); _stream = null;
    }
    public void Dispose() => Stop();
}
