using System.Globalization;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;
using Windows.Media.Control;
using NAudio.CoreAudioApi;

internal static partial class Program
{
    private static void V42NotificationAssetLiveCheck()
    {
        const string app = "Michaeptuch.BatteryFlyout_99j6wafs1sj9m!App";
        byte[]? image = NotificationPayloadReader.ReadImage("ms-appx:///Assets/Notifications/BatteryCharging100.png", app);
        Check(image is { Length: > 0 }, "实机 Battery Flyout 通知的 ms-appx 正文图标可读取");
    }

    // Diagnostic only. No media metadata, notification content or credentials are recorded.
    private static Task V42MediaTrace(bool exercisePause) => OnMediaDispatcher(async dispatcher =>
    {
        using var service = new SmtcService(dispatcher) { AudioProbeEnabled = true };
        using var volume = new VolumeService(dispatcher);
        volume.Start();
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var session = manager.GetSessions().FirstOrDefault(s => string.Equals(s.SourceAppUserModelId, "cloudmusic.exe", StringComparison.OrdinalIgnoreCase));
        if (session == null) throw new InvalidOperationException("网易云媒体会话未启动");
        bool originallyPlaying = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var playerIds = System.Diagnostics.Process.GetProcessesByName("cloudmusic").Select(p => { using (p) return (uint)p.Id; }).ToHashSet();
        using var audioCancellation = new CancellationTokenSource();
        string audioState = "pending";
        var audioProbe = Task.Run(async () =>
        {
            using var enumerator = new MMDeviceEnumerator();
            using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var controls = endpoint.AudioSessionManager.Sessions;
            while (!audioCancellation.IsCancellationRequested)
            {
                var states = new List<string>();
                for (int j = 0; j < controls.Count; j++)
                {
                    var control = controls[j];
                    if (playerIds.Contains(control.GetProcessID)) states.Add(control.State + ":" + control.AudioMeterInformation.MasterPeakValue.ToString("F4", CultureInfo.InvariantCulture));
                }
                Volatile.Write(ref audioState, string.Join(",", states));
                await Task.Delay(100);
            }
        });
        service.NowPlayingChanged += (_, info) => Console.WriteLine($"SERVICE t={clock.ElapsedMilliseconds} state={info?.State} reported={service.ReportedState} inferred={info?.PlaybackStateInferred} position={info?.Position.TotalSeconds:F2}");
        session.PlaybackInfoChanged += (_, _) => Console.WriteLine($"NATIVE_EVENT t={clock.ElapsedMilliseconds} state={session.GetPlaybackInfo().PlaybackStatus}");
        await service.InitializeAsync();
        await Task.Delay(650);
        originallyPlaying = service.Current?.IsPlaying == true;
        if (exercisePause && !originallyPlaying)
        {
            Check(await session.TryPlayAsync(), "测试开始前建立实际播放状态");
            await Task.Delay(800);
            Check(service.Current?.IsPlaying == true, "暂停计时从已经播放的状态开始");
        }
        long pauseAt = 0, playAt = 0, firstPause = 0, firstPlay = 0;
        bool stablePause = true, stablePlay = true, frozenPosition = true;
        TimeSpan? pausedPosition = null;
        long silentSince = 0, activeSince = 0;
        try
        {
            for (int i = 0; i < 80; i++)
            {
                if (exercisePause && i == 20) { pauseAt = clock.ElapsedMilliseconds; Check(await session.TryPauseAsync(), "网易云接受真实暂停命令"); }
                if (exercisePause && i == 50) { playAt = clock.ElapsedMilliseconds; Check(await session.TryPlayAsync(), "网易云接受真实播放命令"); }
                service.CheckFastPlaybackState();
                if (i == 0) Console.WriteLine("Audio output: " + volume.CurrentDeviceName);
                float peak = service.ReadPlayerSessionPeak(volume.GetPlayerSessionPeak(session.SourceAppUserModelId));
                long now = Environment.TickCount64;
                if (peak >= 0)
                {
                    if (peak < .0002f) { if (silentSince == 0) silentSince = now; activeSince = 0; }
                    else { if (activeSince == 0) activeSince = now; silentSince = 0; }
                }
                service.ReportAudioEvidence(silentSince == 0 ? 0 : (now - silentSince) / 1000d,
                    activeSince == 0 ? 0 : (now - activeSince) / 1000d, peak >= 0, 0, false);
                var native = session.GetPlaybackInfo(); var timeline = session.GetTimelineProperties();
                if (i % 4 == 0) Console.WriteLine($"SAMPLE t={clock.ElapsedMilliseconds} native={native.PlaybackStatus} resolved={service.Current?.State} peak={peak:F4} stream={Volatile.Read(ref audioState)} position={timeline.Position.TotalSeconds:F2} stampAge={(DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalSeconds:F2} estimated={service.EstimatedPosition.TotalSeconds:F2} shuffle={native.IsShuffleActive} repeat={native.AutoRepeatMode}");
                if (exercisePause && i >= 20 && i < 50)
                {
                    if (service.Current?.State == PlaybackState.Paused && firstPause == 0) firstPause = clock.ElapsedMilliseconds;
                    if (clock.ElapsedMilliseconds - pauseAt > 900)
                    {
                        stablePause &= service.Current?.State == PlaybackState.Paused;
                        pausedPosition ??= service.EstimatedPosition;
                        frozenPosition &= Math.Abs((service.EstimatedPosition - pausedPosition.Value).TotalMilliseconds) < 50;
                    }
                }
                if (exercisePause && i >= 50)
                {
                    if (service.Current?.State == PlaybackState.Playing && firstPlay == 0) firstPlay = clock.ElapsedMilliseconds;
                    if (clock.ElapsedMilliseconds - playAt > 900) stablePlay &= service.Current?.State == PlaybackState.Playing;
                }
                await Task.Delay(100);
            }
            if (exercisePause)
            {
                Check(firstPause > 0 && firstPause - pauseAt < 900 && stablePause, "真实暂停在 900ms 内同步，并在旧 Playing 持续上报时保持稳定");
                Check(firstPlay > 0 && firstPlay - playAt < 900 && stablePlay, "真实播放在 900ms 内同步，并在电平缓存缺失时保持稳定");
                Check(frozenPosition, "实际暂停后歌词所用进度保持冻结，重复旧时间线不会继续推进或倒退");
                Console.WriteLine($"SYNC pauseMs={firstPause-pauseAt} playMs={firstPlay-playAt}");
            }
        }
        finally
        {
            audioCancellation.Cancel(); await audioProbe;
            if (exercisePause)
            {
                if (originallyPlaying) await session.TryPlayAsync(); else await session.TryPauseAsync();
            }
        }
    });
}
