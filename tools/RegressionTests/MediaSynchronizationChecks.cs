using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using alpha_island.Services;

internal static partial class Program
{
    private static async Task InspectMedia()
    {
        var manager = await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(3));
        using var volume = new VolumeService(Dispatcher.CurrentDispatcher);
        volume.Start();
        foreach (var session in manager.GetSessions())
        {
            string source = session.SourceAppUserModelId;
            var playback = session.GetPlaybackInfo();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var props = await session.TryGetMediaPropertiesAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            var propertiesMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            long bytes = 0;
            if (props.Thumbnail != null)
            {
                using var stream = await props.Thumbnail.OpenReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                using var input = stream.AsStreamForRead();
                using var memory = new System.IO.MemoryStream();
                await input.CopyToAsync(memory).WaitAsync(TimeSpan.FromSeconds(3)); bytes = memory.Length;
            }
            System.Console.WriteLine($"Source={source}, Reported={playback?.PlaybackStatus}, Play={playback?.Controls.IsPlayEnabled}, Pause={playback?.Controls.IsPauseEnabled}, Toggle={playback?.Controls.IsPlayPauseToggleEnabled}, PropertiesMs={propertiesMs:F1}, CoverMs={sw.Elapsed.TotalMilliseconds:F1}, CoverBytes={bytes}");
            for (int i = 0; i < 4; i++)
            {
                await Task.Delay(250);
                System.Console.WriteLine($"Audio sample {i + 1}: peak={volume.GetPlayerSessionPeak(source):F4}");
            }
            var sampled = (AudioPeakSnapshot)typeof(VolumeService).GetField("_sessionPeaks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(volume)!;
            System.Console.WriteLine($"Audio device={volume.CurrentDeviceName}, sessions={volume.GetAudioAppSessions().Count}, sampled processes={string.Join(",", sampled.Peaks.Keys)}");
        }
        await OnMediaDispatcher(async dispatcher =>
        {
            using var service = new SmtcService(dispatcher);
            var cover = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            service.NowPlayingChanged += (_, info) =>
            {
                System.Console.WriteLine($"Live service: source={info?.SourceApp}, state={info?.State}, hasMedia={info?.HasMedia}, artwork={info?.Artwork != null}, elapsedMs={clock.Elapsed.TotalMilliseconds:F1}");
                if (info?.Artwork != null) cover.TrySetResult();
            };
            await service.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if (service.Current?.HasMedia == true && service.Current.Artwork == null)
                await Task.WhenAny(cover.Task, Task.Delay(1000));
        });
    }

    private static Task MediaSynchronizationChecks() => OnMediaDispatcher(MediaSynchronizationCore);

    private static async Task OnMediaDispatcher(Func<Dispatcher, Task> work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await work(dispatcher); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task;
        thread.Join();
    }

    private static async Task MediaSynchronizationCore(Dispatcher dispatcher)
    {
        using var service = new SmtcService(dispatcher) { AudioProbeEnabled = true };
        var first = new NowPlayingInfo { Title = "第一首", SourceApp = "cloudmusic.exe", SessionId = "player-1",
            ContentKind = alpha_island.Core.MediaContentKind.Music, State = PlaybackState.Playing,
            Position = TimeSpan.FromSeconds(10), Duration = TimeSpan.FromMinutes(3), CanToggle = true };
        void Report(PlaybackState state)
        {
            PrivateCall(service, "ObserveReportedState", state);
            PrivateCall(service, "PublishResolvedState");
        }
        void Track(NowPlayingInfo info, string key, Func<CancellationToken, Task<BitmapSource?>>? cover = null)
        {
            PrivateCall(service, "ObserveReportedState", info.State);
            service.UpdateTrack(info, key, cover);
        }
        void ExpireCommand() => typeof(SmtcService).GetField("_commandUntilMs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(service, 0L);
        TaskCompletionSource<BitmapSource?> Cover() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        BitmapSource art = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
        art.Freeze();
        var slow = Cover(); var second = Cover();
        Track(first, "first", _ => slow.Task);
        Check(service.Current?.Title == first.Title && service.Current.Artwork == null, "封面未完成时立即发布曲名与播放状态");
        Report(PlaybackState.Paused);
        Check(service.Current?.State == PlaybackState.Paused, "慢封面期间暂停事件直接更新状态");
        Track(first with { Title = "第二首", State = PlaybackState.Paused }, "second", _ => second.Task);
        Check(service.Current?.Title == "第二首" && service.Current.State == PlaybackState.Paused, "慢封面不阻塞下一首曲目发布");
        slow.SetResult(art); await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        Check(service.Current?.Title == "第二首" && service.Current.Artwork == null, "迟到旧封面不能覆盖新曲目");
        var artworkPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.NowPlayingChanged += (_, info) => { if (info?.Artwork == art) artworkPublished.TrySetResult(); };
        second.SetResult(art); await artworkPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(service.Current?.Artwork == art && service.Current.State == PlaybackState.Paused, "补齐封面保留最新暂停状态");
        service.ReportAudioEvidence(0, 0, false, 0, true);
        Check(service.Current?.State == PlaybackState.Paused, "端点有声不会误升级当前播放器");
        service.ReportAudioEvidence(0, .2, true, 0, true);
        Check(service.Current?.State == PlaybackState.Paused, "短暂残余声音不足以纠正暂停");
        service.ReportAudioEvidence(0, .5, true, 0, true);
        Check(service.Current?.IsPlaying == true && service.IsAudioActiveLatched && service.ReportedState == PlaybackState.Paused, "自身会话持续有声纠正迟到暂停上报");
        Report(PlaybackState.Paused);
        Check(service.Current?.IsPlaying == true, "重复旧暂停上报不会清除有声纠正");
        service.ReportAudioEvidence(2, 0, true, 0, true);
        Check(service.Current?.State == PlaybackState.Paused, "重新无声后撤销播放纠正");
        Report(PlaybackState.Playing); service.ReportAudioEvidence(2, 0, true, 0, true);
        Check(service.Current?.State == PlaybackState.Paused && service.IsAudioVetoLatched, "持续无声仍可纠正虚假播放上报");
        long epoch = service.AudioEvidenceEpoch;
        await service.ExecutePlaybackCommandAsync("player-1", PlaybackState.Playing, () => Task.FromResult(false));
        Check(service.Current?.State == PlaybackState.Paused && epoch == service.AudioEvidenceEpoch, "命令失败时不伪造播放成功");
        await service.ExecutePlaybackCommandAsync("player-1", PlaybackState.Playing, () => Task.FromResult(true));
        Check(service.Current?.IsPlaying == true && service.AudioEvidenceEpoch > epoch, "播放成功立即更新并重置旧音频证据");
        service.ReportAudioEvidence(99, 0, true, 99, true); Report(PlaybackState.Paused);
        Check(service.Current?.IsPlaying == true, "播放确认窗口丢弃旧静音及迟到暂停");
        service.ReportAudioEvidence(0, .5, true, 0, true); ExpireCommand(); Report(PlaybackState.Paused);
        Check(service.Current?.IsPlaying == true, "确认窗口结束后由自身会话有声证明播放");
        await service.ExecutePlaybackCommandAsync("player-1", PlaybackState.Paused, () => Task.FromResult(true));
        service.ReportAudioEvidence(0, 99, true, 0, true); Report(PlaybackState.Playing);
        Check(service.Current?.State == PlaybackState.Paused, "暂停成功不被旧声音或迟到播放撤销");
        ExpireCommand(); Report(PlaybackState.Playing);
        Check(service.Current?.IsPlaying == true, "命令未获确认且无声音证据时超时恢复上报");
        Report(PlaybackState.Paused);
        var lateCommand = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var olderCommand = service.ExecutePlaybackCommandAsync("player-1", PlaybackState.Playing, () => lateCommand.Task);
        await service.ExecutePlaybackCommandAsync("player-1", PlaybackState.Paused, () => Task.FromResult(true));
        lateCommand.SetResult(true); await olderCommand;
        Check(service.Current?.State == PlaybackState.Paused, "乱序旧播放命令不能覆盖较新暂停命令");
        PrivateCall(service, "Detach");
        Track(first with { SessionId = "browser", SourceApp = "msedge.exe", ContentKind = alpha_island.Core.MediaContentKind.Video, State = PlaybackState.Paused }, "browser");
        service.ReportAudioEvidence(0, 99, true, 0, true);
        Check(service.Current?.State == PlaybackState.Paused, "浏览器视频不受音频纠正影响");
        await service.ExecutePlaybackCommandAsync("player-1", PlaybackState.Playing, () => Task.FromResult(true));
        Check(service.Current?.SessionId == "browser" && service.Current.State == PlaybackState.Paused, "旧会话控制结果不能修改新来源");
        foreach (var closed in new[] { PlaybackState.Closed, PlaybackState.Stopped, PlaybackState.Changing })
        {
            PrivateCall(service, "Detach"); Track(first with { State = closed }, "closed-" + closed);
            service.ReportAudioEvidence(0, 99, true, 0, true);
            Check(service.Current?.State == closed, "声音不能将明确的 " + closed + " 改为播放");
        }
        var disposedCover = Cover(); PrivateCall(service, "Detach"); Track(first, "disposing", _ => disposedCover.Task);
        int events = 0; service.NowPlayingChanged += (_, _) => events++;
        service.Dispose(); disposedCover.SetResult(art); await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        Check(events == 0, "停用后迟到封面不会唤起媒体");
        var snapshot = new AudioPeakSnapshot(new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            { ["cloudmusic.exe"] = .4f, ["invalid.exe"] = float.NaN }, 1000);
        Check(snapshot.Read("cloudmusic.exe", 1100) == .4f, "读取新鲜会话电平");
        Check(snapshot.Read("C:\\Music\\CloudMusic", 1100) == .4f, "带路径和大小写不同的来源仍匹配电平");
        Check(snapshot.Read("cloudmusic.exe", 1501) == -1, "过期电平不构成连续声音或静音");
        Check(snapshot.Read("cloudmusic.exe", 900) == -1, "时钟异常的电平证据被拒绝");
        Check(snapshot.Read("unknown", 1100) == -1 && snapshot.Read("invalid", 1100) == -1, "不存在或非有限电平按未知处理");
    }
}
