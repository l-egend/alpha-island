using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static Task UsbMediaStateChecks() => OnMediaDispatcher(async dispatcher =>
    {
        await UsbExplicitPauseChecks(dispatcher);
        await UsbStalePlayingChecks(dispatcher);
        UsbStreamIdentityChecks(dispatcher);
        UsbQuietPlaybackChecks(dispatcher);
    });

    private const string UsbSource = "cloudmusic.exe", UsbSession = "usb-media-session";
    private static MediaStreamObservation UsbObservation(bool running = true)
        => new(UsbSource, UsbSession, running, Environment.TickCount64 - 1000, Environment.TickCount64);

    private static SmtcService UsbPlaybackFixture(Dispatcher dispatcher, Func<MediaStreamObservation?> read)
    {
        var service = new SmtcService(dispatcher, read) { AudioProbeEnabled = true };
        service.ObserveTransportState(PlaybackState.Playing, false);
        service.UpdateTrack(new NowPlayingInfo
        {
            SourceApp = UsbSource, SessionId = UsbSession, Title = "USB playback fixture",
            ContentKind = MediaContentKind.Music, State = PlaybackState.Playing,
            Position = TimeSpan.FromSeconds(10), Duration = TimeSpan.FromMinutes(3)
        }, "usb-playback-track", null);
        return service;
    }

    private static async Task UsbExplicitPauseChecks(Dispatcher dispatcher)
    {
        using var service = UsbPlaybackFixture(dispatcher, () => UsbObservation());
        Check(service.Current?.IsPlaying == true, "USB 音频流仍运行时，播放状态正常显示");
        service.ObserveTransportState(PlaybackState.Paused, true);
        PrivateCall(service, "PublishResolvedState");
        Console.WriteLine($"reported={service.ReportedState}, USB stream=Active, resolved={service.Current?.State}");
        Check(service.Current?.State == PlaybackState.Paused,
            "播放器明确暂停后，USB 耳机保留的活动音频流不会让岛继续显示播放");
        var pausedPosition = service.EstimatedPosition;
        await Task.Delay(100);
        PrivateCall(service, "PublishResolvedState");
        Check(service.Current?.State == PlaybackState.Paused && service.EstimatedPosition == pausedPosition,
            "USB 音频流未关闭时，暂停状态稳定且歌词进度保持冻结");
        service.ReportAudioEvidence(0, .6, true, 0, true);
        Check(service.Current?.State == PlaybackState.Paused, "USB 暂停尾音不能单独推翻明确暂停");
        SetAgentField(service, "_transportEventUntilMs", 0L);
        service.ObserveTransportState(PlaybackState.Paused, false);
        PrivateCall(service, "PublishResolvedState");
        Check(service.Current?.State == PlaybackState.Paused, "事件保护窗口结束后，长期开启的 USB 流仍不能伪造播放");
        service.ObserveTransportState(PlaybackState.Playing, true);
        PrivateCall(service, "PublishResolvedState");
        Check(service.Current?.IsPlaying == true, "新的明确恢复播放立即同步");
    }

    private static async Task UsbStalePlayingChecks(Dispatcher dispatcher)
    {
        using var service = UsbPlaybackFixture(dispatcher, () => UsbObservation());
        service.ReportAudioEvidence(2, 0, true, 0, true);
        Check(service.Current?.State == PlaybackState.Paused,
            "USB 音频流保持活动且 SMTC 仍上报 Playing 时，自身连续静音可以确认暂停");
        var transitions = new List<PlaybackState>();
        service.NowPlayingChanged += (_, info) => { if (info != null) transitions.Add(info.State); };
        var pausedPosition = service.EstimatedPosition;
        await Task.Delay(100);
        for (int i = 0; i < 8; i++)
        {
            service.ObserveTransportState(PlaybackState.Playing, false);
            PrivateCall(service, "PublishResolvedState");
            service.ReportAudioEvidence(2, 0, true, 0, true);
        }
        Check(service.Current?.State == PlaybackState.Paused && transitions.Count == 0 && service.EstimatedPosition == pausedPosition,
            "重复旧 Playing 上报不闪回播放，暂停后的歌词进度保持冻结");
        service.ReportAudioEvidence(0, .6, true, 0, true);
        Check(service.Current?.IsPlaying == true, "USB 播放器重新持续出声时，解除旧暂停纠正");
        service.ReportAudioEvidence(.2, 0, true, 0, true);
        Check(service.Current?.IsPlaying == true, "USB 短暂声音间隙不撤销已确认播放");
    }

    private static void UsbStreamIdentityChecks(Dispatcher dispatcher)
    {
        MediaStreamObservation? observation = UsbObservation(false) with { Source = "other-player.exe" };
        using var service = UsbPlaybackFixture(dispatcher, () => observation);
        PrivateCall(service, "PublishResolvedState");
        Check(service.Current?.IsPlaying == true, "其他播放器的 USB 流不能改变当前播放状态");
        observation = UsbObservation(false) with { SessionId = "old-session" };
        PrivateCall(service, "PublishResolvedState");
        Check(service.Current?.IsPlaying == true, "旧媒体会话的 USB 流不能改变当前播放状态");
        observation = UsbObservation(false) with { StableSinceMs = Environment.TickCount64 - 2000, SampledAtMs = Environment.TickCount64 - 2000 };
        PrivateCall(service, "PublishResolvedState");
        Check(service.Current?.IsPlaying == true, "设备切换后过期的停止样本不能覆盖当前播放");
        observation = UsbObservation(false) with { StableSinceMs = Environment.TickCount64 };
        PrivateCall(service, "PublishResolvedState");
        Check(service.Current?.IsPlaying == true, "尚未稳定的 USB 设备变化不立即翻转播放状态");
        observation = UsbObservation(false);
        PrivateCall(service, "PublishResolvedState");
        Check(service.Current?.State == PlaybackState.Paused, "当前播放器的稳定停止音频流仍可纠正暂停漏报");
        observation = null;
        service.ReportAudioEvidence(2, 0, true, 0, true);
        Check(service.Current?.State == PlaybackState.Paused, "USB 设备暂时不可读时继续采用已确认的自身声音证据");
    }

    private static void UsbQuietPlaybackChecks(Dispatcher dispatcher)
    {
        using var service = UsbPlaybackFixture(dispatcher, () => UsbObservation());
        var stamp = DateTimeOffset.UtcNow;
        service.ObserveTimelineEvidence(TimeSpan.FromSeconds(10), stamp.AddMilliseconds(-500));
        service.ObserveTimelineEvidence(TimeSpan.FromSeconds(10.5), stamp);
        service.ReportAudioEvidence(2, 0, true, 2, true);
        Check(service.Current?.IsPlaying == true,
            "USB 静音或安静片段中，播放器自身时间线持续推进时保持播放");
        service.UpdateTrack(service.Current! with { ContentKind = MediaContentKind.Video }, "usb-video-track", null);
        service.ObserveTransportState(PlaybackState.Paused, false);
        PrivateCall(service, "PublishResolvedState");
        Check(service.Current?.State == PlaybackState.Paused, "USB 流不替视频播放器决定播放状态");
    }
}
