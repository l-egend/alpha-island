using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static async Task SmartStateStabilityChecks()
    {
        DateTime now = new(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);
        var next = SmartChargingState.Active;
        int reads = 0;
        var monitor = new SmartChargingMonitor(() => { Interlocked.Increment(ref reads); return next; }, () => now);
        async Task ReadSample()
        {
            monitor.ReadCached();
            if (AgentField<Task<SmartChargingState>?>(monitor, "_query") is { } query)
                await query.WaitAsync(TimeSpan.FromSeconds(3));
            monitor.ReadCached();
        }
        await ReadSample();
        Check(monitor.ReadCached() == SmartChargingState.Active, "已确认系统开启智能充电");
        now = now.AddSeconds(6); next = SmartChargingState.Unknown;
        await ReadSample();
        Console.WriteLine($"One unreadable shell sample: smart={monitor.ReadCached()}, auto island suppressed={monitor.ReadCached() == SmartChargingState.Active}");
        Check(monitor.ReadCached() == SmartChargingState.Active,
            "任务栏一次不可读不会撤销智能充电或突然弹出电量岛");
        now = now.AddSeconds(6); next = SmartChargingState.NotActive;
        await ReadSample();
        Check(monitor.ReadCached() == SmartChargingState.Active, "任务栏一帧省略智能充电字样不会立即弹出电量岛");
        now = now.AddSeconds(1); next = SmartChargingState.Active;
        await ReadSample();
        Check(monitor.ReadCached() == SmartChargingState.Active, "下一次有效读取恢复智能充电后取消关闭候选");
        now = now.AddSeconds(6); next = SmartChargingState.NotActive; await ReadSample();
        now = now.AddSeconds(1); await ReadSample();
        Check(monitor.ReadCached() == SmartChargingState.NotActive, "连续两次明确关闭后确认状态，不长期隐藏真实的普通充电");
        now = now.AddSeconds(6); next = SmartChargingState.Active; await ReadSample();
        bool stable = true;
        for (int i = 0; i < 100; i++)
        {
            now = now.AddSeconds(6); next = i % 2 == 0 ? SmartChargingState.Unknown : SmartChargingState.NotActive;
            await ReadSample(); stable &= monitor.ReadCached() == SmartChargingState.Active;
            now = now.AddSeconds(1); next = SmartChargingState.Active;
            monitor.Invalidate(); await ReadSample(); stable &= monitor.ReadCached() == SmartChargingState.Active;
        }
        Check(stable && reads >= 200, "重复一百次短暂读取失败与标签刷新，智能充电状态始终稳定");
        monitor.Invalidate();
        Check(monitor.ReadCached() == SmartChargingState.Active,
            "重复电源广播刷新时保留已确认的智能充电，不闪出电量岛");
        await Task.Delay(20); monitor.ReadCached();
        now = now.AddSeconds(31);
        Check(monitor.ReadCached() == SmartChargingState.Unknown,
            "长时间读不到系统状态仍会过期，避免永久锁存智能充电");
        now = now.AddSeconds(6); next = SmartChargingState.Active; await ReadSample();
        if (AgentField<Task<SmartChargingState>?>(monitor, "_query") is { } pending) await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Check(monitor.ReadCached(false) == SmartChargingState.NotActive, "真实拔电立即清除智能充电缓存，无需等待关闭确认");
        int offReads = reads; monitor.Invalidate();
        Check(monitor.ReadCached(false) == SmartChargingState.NotActive && reads == offReads,
            "电池供电时不再读取或恢复旧的智能充电指示");
    }

    private static Task MusicStateStabilityChecks() => OnMediaDispatcher(dispatcher =>
    {
        MediaStreamObservation? observation = UsbObservation();
        using var service = UsbPlaybackFixture(dispatcher, () => observation);
        var changes = new List<PlaybackState>();
        service.NowPlayingChanged += (_, info) => { if (info != null) changes.Add(info.State); };
        var stamp = DateTimeOffset.UtcNow;
        service.ObserveTimelineEvidence(TimeSpan.FromSeconds(10), stamp.AddMilliseconds(-500));
        service.ObserveTimelineEvidence(TimeSpan.FromSeconds(10.5), stamp);
        observation = UsbObservation(false);
        PrivateCall(service, "PublishResolvedState");
        Console.WriteLine($"Advancing native timeline + stopped stream sample: state={service.Current?.State}, transitions={string.Join(',', changes)}");
        Check(service.Current?.IsPlaying == true && changes.Count == 0,
            "自身进度仍在推进时，短暂停止音频样本不会触发音乐岛收回");
        observation = UsbObservation();
        PrivateCall(service, "PublishResolvedState");
        Check(changes.Count == 0, "音频样本恢复后无需重新弹出音乐岛");
        PrivateCall(service, "ResetNativeTimelineEvidence");
        observation = UsbObservation(false) with { StableSinceMs = Environment.TickCount64 - 250 };
        PrivateCall(service, "PublishResolvedState");
        Check(service.Current?.IsPlaying == true && changes.Count == 0, "输出设备短暂中断二百五十毫秒不让音乐岛收回");
        observation = UsbObservation(false); PrivateCall(service, "PublishResolvedState");
        Check(service.Current?.State == PlaybackState.Paused && changes.Count == 1, "真正持续停止音频流仍能及时纠正暂停漏报");
        observation = UsbObservation(); PrivateCall(service, "PublishResolvedState");
        Check(service.Current?.State == PlaybackState.Paused && changes.Count == 1, "USB 音频流重新打开不会单独推翻已确认暂停或弹岛");
        service.ReportAudioEvidence(0, .6, true, 0, true);
        Check(service.Current?.IsPlaying == true && changes.Count == 2, "持续自身声音确认恢复后只更新一次播放状态");
        observation = UsbObservation(false); PrivateCall(service, "PublishResolvedState");
        Check(service.Current?.IsPlaying == true && changes.Count == 2, "新鲜自身音频优先于矛盾的停止流样本，不反复收放");
        service.ObserveTransportState(PlaybackState.Paused, true); PrivateCall(service, "PublishResolvedState");
        Check(service.Current?.State == PlaybackState.Paused, "新的明确暂停立即生效，稳定处理不会拖延真实操作");
        return Task.CompletedTask;
    });

    private static Task UsbResumeLatencyChecks() => OnMediaDispatcher(dispatcher =>
    {
        using var service = UsbPlaybackFixture(dispatcher, () => UsbObservation());
        service.ObserveTransportState(PlaybackState.Paused, true);
        PrivateCall(service, "PublishResolvedState");
        SetAgentField(service, "_transportEventUntilMs", 0L);
        service.ReportAudioEvidence(0, .6, true, 0, true);
        Check(service.Current?.State == PlaybackState.Paused, "短暂停尾音仍不能误判为恢复播放");
        service.ReportAudioEvidence(.4, 0, true, 0, true);
        Check(service.Current?.State == PlaybackState.Paused, "已经采到自身静音，确认暂停后的安静阶段");
        service.ReportAudioEvidence(0, .6, true, 0, true);
        Console.WriteLine($"USB stream=Active, own audio active=0.6s after silence, raw state=Paused, resolved={service.Current?.State}");
        Check(service.Current?.IsPlaying == true,
            "USB 耳机恢复持续出声而播放器漏报时，状态不必等待数秒后的进度更新");
        service.ReportAudioEvidence(.4, 0, true, 0, true);
        Check(service.Current?.State == PlaybackState.Paused, "USB 再次真正安静时同步暂停，不延续旧播放锁存");
        foreach (var bad in new MediaStreamObservation?[] {
            null, UsbObservation() with { Source = "other-player.exe" }, UsbObservation() with { SessionId = "old-session" },
            UsbObservation() with { SampledAtMs = Environment.TickCount64 - 2000 },
            UsbObservation() with { SampledAtMs = Environment.TickCount64 + 1000 }, UsbObservation(false) })
        {
            using var other = UsbPlaybackFixture(dispatcher, () => bad);
            other.ObserveTransportState(PlaybackState.Paused, true); PrivateCall(other, "PublishResolvedState");
            SetAgentField(other, "_transportEventUntilMs", 0L);
            other.ReportAudioEvidence(.4, 0, true, 0, true); other.ReportAudioEvidence(0, .6, true, 0, true);
            Check(other.Current?.State == PlaybackState.Paused, "缺失、其他会话、过期或停止的流不能确认 USB 恢复播放");
        }
        return Task.CompletedTask;
    });
}