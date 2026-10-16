using alpha_island.Services;

internal static partial class Program
{
    private static Task BluetoothMediaLatencyChecks() => OnMediaDispatcher(dispatcher =>
    {
        MediaStreamObservation? observation = UsbObservation(false) with { Peak = 0 };
        using var service = UsbPlaybackFixture(dispatcher, () => observation);
        service.ObserveTransportState(PlaybackState.Paused, true); PrivateCall(service, "PublishResolvedState");
        SetAgentField(service, "_transportEventUntilMs", 0L);
        service.ReportAudioEvidence(.4, 0, true, 0, true);
        observation = UsbObservation() with { Peak = .08f };
        float peak = service.ReadPlayerSessionPeak(0); // Mixer enumeration still holds the previous Bluetooth session's zero.
        service.ReportAudioEvidence(0, peak > .0002f ? .6 : 0, peak >= 0, 0, false);
        Console.WriteLine($"Bluetooth session reopened: own peak=.08, old mixer peak=0, selected peak={peak}, state={service.Current?.State}");
        Check(service.Current?.IsPlaying == true,
            "蓝牙暂停后重建会话已经持续出声时，不等待混音器刷新旧的零电平");
        observation = UsbObservation(false) with { Peak = 0 };
        service.ReportAudioEvidence(.4, 0, true, 0, true);
        Check(service.Current?.State == PlaybackState.Paused, "蓝牙再次暂停仍正常收回并冻结歌词进度");
        Check(service.ReadPlayerSessionPeak(.8f) == 0, "新蓝牙会话的真实静音优先于旧混音器的有声缓存");
        foreach (var bad in new MediaStreamObservation?[] {
            null, UsbObservation() with { Source = "other.exe", Peak = .9f },
            UsbObservation() with { SessionId = "previous-track", Peak = .9f },
            UsbObservation() with { SampledAtMs = Environment.TickCount64 - 600, Peak = .9f },
            UsbObservation() with { Peak = float.NaN }, UsbObservation() with { Peak = 1.1f } })
        {
            observation = bad;
            Check(service.ReadPlayerSessionPeak(.05f) == .05f,
                "缺失、其他会话、过期或无效的蓝牙峰值不替换当前有效音频证据");
        }
        return Task.CompletedTask;
    });
}