using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static T PerformanceField<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static void DownloadIdleQueryChecks(string root)
    {
        string profile = Path.Combine(root, "IdleDownloadBrowser");
        Directory.CreateDirectory(profile);
        string history = Path.Combine(profile, "History");
        Sql(history, "create table downloads(target_path text,current_path text,total_bytes integer,received_bytes integer,state integer,start_time integer,end_time integer);");
        var monitor = new DownloadMonitorService(profile);
        var query = typeof(DownloadMonitorService).GetMethod("TryQueryChromium", BindingFlags.Instance | BindingFlags.NonPublic)!;
        List<string?[]> Read(bool force = false)
        {
            object?[] arguments = { history, force, null, null };
            Check((bool)query.Invoke(monitor, arguments)!, "下载历史查询成功");
            return (List<string?[]>)arguments[2]!;
        }
        var first = Read();
        var unchanged = Read();
        Check(ReferenceEquals(first, unchanged), "未变化的浏览器历史复用查询结果，不反复读取 SQLite");
        Sql(history, "insert into downloads values('new.zip','',100,100,1,1,2);");
        File.SetLastWriteTimeUtc(history, DateTime.UtcNow.AddSeconds(1));
        var changed = Read();
        Check(changed.Count == 1 && changed[0][0] == "new.zip" && !ReferenceEquals(first, changed),
            "浏览器历史变化后立即读取新下载，不复用旧行");
        Check(!ReferenceEquals(changed, Read(force: true)), "半成品消失时强制重读，即使文件指纹未变化");
        File.WriteAllText(Path.Combine(profile, "Preferences"), System.Text.Json.JsonSerializer.Serialize(
            new { download = new { default_directory = profile } }));
        string partial = Path.Combine(profile, "live.zip.crdownload");
        File.WriteAllBytes(partial, new byte[32]);
        monitor.Sample();
        Check(monitor.Current.Any(t => t.PathOnDisk == partial && t.ReceivedBytes == 32), "缓存历史时仍检测实时下载文件");
        File.WriteAllBytes(partial, new byte[96]);
        monitor.Sample();
        Check(monitor.Current.Any(t => t.PathOnDisk == partial && t.ReceivedBytes == 96), "数据库未变化时下载字节数仍即时更新");
    }

    private static void PerformanceAllocationChecks()
    {
        var previousLevel = IslandLog.MinLevel;
        long debugBytes = -1;
        try
        {
            Check(previousLevel == IslandLogLevel.Info, "Release 默认 Info 日志级别");
            IslandLog.MinLevel = IslandLogLevel.Info;
            int evaluations = 0;
            int ExpensiveValue() { evaluations++; return 7; }
            for (int i = 0; i < 100; i++) IslandLog.Debug("Performance", $"ignored={ExpensiveValue()}");
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) IslandLog.Debug("Performance", $"ignored={ExpensiveValue()}");
            debugBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Check(evaluations == 0 && debugBytes == 0, "过滤 10000 条 Debug 插值日志：表达式不执行，分配 0 字节");
            IslandLog.MinLevel = IslandLogLevel.Debug;
            IslandLog.Debug("Performance", $"{ExpensiveValue(),3}|{1.25:F2}|{"ok"}");
            Check(evaluations == 1 && IslandLog.Snapshot().Last().Message == "  7|1.25|ok",
                "启用 Debug 后恢复求值、对齐、格式化及日志记录");
        }
        finally { IslandLog.MinLevel = previousLevel; }

        using var audio = new AudioSpectrumService();
        Check(PerformanceField<int>(audio, "_watchdogInterval") == Timeout.Infinite,
            "未启动频谱服务不运行看门狗");
        var smoothed = PerformanceField<float[]>(audio, "_smoothed");
        Array.Fill(smoothed, .25f);
        int frameCount = 0;
        bool frameValid = true;
        AudioSpectrumService.SpectrumFrameHandler consumer = frame =>
        {
            frameCount++;
            frameValid &= frame.Length == 64 && frame[0] == .25f && frame[63] == .25f;
        };
        audio.FrameReady += consumer;
        long step = Stopwatch.Frequency / 50;
        long stamp = Stopwatch.GetTimestamp();
        for (int i = 0; i < 100; i++) audio.PublishFrameIfDue(stamp += step);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) audio.PublishFrameIfDue(stamp += step);
        long frameBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(frameBytes == 0 && frameValid && frameCount == 10100,
            "10000 个内部频谱帧保持 64 段完整数据，热路径分配 0 字节");
        audio.FrameReady -= consumer;
        var retained = new List<float[]>();
        audio.SpectrumUpdated += (_, frame) => retained.Add(frame);
        audio.PublishFrameIfDue(stamp += step);
        Array.Fill(smoothed, .75f);
        audio.PublishFrameIfDue(stamp += step);
        Check(retained.Count == 2 && !ReferenceEquals(retained[0], retained[1])
            && retained[0][0] == .25f && retained[1][0] == .75f,
            "公开频谱事件继续提供独立快照，旧帧不会被覆盖");

        using var concurrent = new AudioSpectrumService();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int deliveries = 0;
        bool unchanged = false;
        concurrent.FrameReady += frame =>
        {
            if (Interlocked.Increment(ref deliveries) == 1)
            {
                float initial = frame[0];
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException();
                unchanged = frame[0] == initial;
            }
        };
        long concurrentStamp = Stopwatch.GetTimestamp();
        var first = Task.Run(() => concurrent.PublishFrameIfDue(concurrentStamp));
        Check(entered.Wait(TimeSpan.FromSeconds(3)), "并发频谱测试进入借用帧回调");
        lock (PerformanceField<object>(concurrent, "_gate"))
            Array.Fill(PerformanceField<float[]>(concurrent, "_smoothed"), 1f);
        var second = Task.Run(() => concurrent.PublishFrameIfDue(concurrentStamp + step));
        try { Check(!second.Wait(50), "另一发布线程等待首个回调完成，不抢写缓冲"); }
        finally { release.Set(); }
        Task.WaitAll(first, second);
        Check(unchanged && deliveries == 2, "采集与看门狗并发发布不会破坏正在读取的帧");
        concurrent.Stop();
        PrivateCall(concurrent, "WatchdogTick", (object?)null);
        Check(!concurrent.IsRunning && PerformanceField<long>(concurrent, "_lastStartAttemptTicks") == 0,
            "显式停止后看门狗不会重新启动采集");
        Check(MediaWorkPacing.WatchdogInterval(31, true) == 2000
            && MediaWorkPacing.WatchdogInterval(31, false) == 400
            && MediaWorkPacing.WatchdogInterval(0, true) == 400,
            "只有健康的长期静音会降频，失去回调与恢复出声保持快速巡检");
        Console.WriteLine($"Measured allocations: filtered Debug={debugBytes} B; borrowed frames={frameBytes} B / 10000 frames");
        using var refreshSignal = new AutoResetEvent(false);
        using var volume = new VolumeService(Dispatcher.CurrentDispatcher);
        typeof(VolumeService).GetField("_refreshSignal", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(volume, refreshSignal);
        _ = volume.GetActiveOutputDevices(); _ = volume.GetAudioAppSessions();
        Check(!refreshSignal.WaitOne(0), "读取音量列表缓存不反向唤醒 COM 设备枚举");
        volume.RequestRefresh();
        Check(refreshSignal.WaitOne(0), "真实设备或会话变动仍可立即唤醒刷新");
        volume.OnDeviceAdded("fixture-endpoint");
        Check(refreshSignal.WaitOne(0) && PerformanceField<int>(volume, "_devicesDirty") == 1,
            "音频设备新增事件使缓存失效并立即刷新，不等待低频兜底");
    }

    private static void PerformanceUiChecks(alpha_island.MainWindow window)
    {
        var spectrum = (alpha_island.Controls.SpectrumVisualizer)window.FindName("PillSpectrum");
        var wave = (alpha_island.Controls.WaveVisualizer)window.FindName("CardWave");
        var bands = Enumerable.Repeat(.4f, 64).ToArray();
        for (int i = 0; i < 100; i++) { spectrum.PushFrame(bands.AsSpan()); wave.PushFrame(bands.AsSpan()); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) { spectrum.PushFrame(bands.AsSpan()); wave.PushFrame(bands.AsSpan()); }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(bytes == 0, "两个实际频谱控件接收 10000 帧，预热后分配 0 字节");
        Array.Clear(bands);
        Check(PerformanceField<float[]>(spectrum, "_incoming")[0] > 0
            && PerformanceField<float[]>(wave, "_incoming")[0] > 0,
            "借用帧被重用后，两个控件各自的输入副本保持不变");
        window.Left = window.Top = -32000;
        window.Show(); PumpAnimations(50); // 隔离模式的 Loaded 不启动真实服务。
        using var media = new SmtcService(window.Dispatcher);
        using var ticker = new IslandTicker(window.Dispatcher);
        SetAgentField(window, "_smtc", media);
        SetAgentField(window, "_ticker", ticker);
        SetAgentField(window, "_forcedState", "performance");
        SetAgentField(window, "_runtimeEnabled", true);
        var progress = AgentField<DispatcherTimer>(window, "_progressTimer");
        var hover = AgentField<DispatcherTimer>(window, "_hoverTimer");
        var stateField = typeof(alpha_island.MainWindow).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void State(string name) => stateField.SetValue(window, Enum.Parse(stateField.FieldType, name));
        void Pace() => PrivateCall(window, "ApplyPowerSavePacing");
        var probe = PerformanceField<IslandTimer>(media, "_metadataProbe");
        var info = new NowPlayingInfo { Title = "性能回归曲目", SourceApp = "cloudmusic.exe",
            State = PlaybackState.Playing, Position = TimeSpan.FromSeconds(12), Duration = TimeSpan.FromMinutes(3) };
        try
        {
            media.SetDebugNowPlaying(info);
            using var backgroundAudio = new AudioSpectrumService();
            SetAgentField(window, "_audio", backgroundAudio);
            int hiddenFrames = 0;
            backgroundAudio.FrameReady += _ => hiddenFrames++;
            State("Bar"); PrivateCall(window, "SyncVisualizers");
            byte[] pcm = new byte[960 * 2 * sizeof(float)];
            for (int i = 0; i < 960; i++)
            {
                byte[] sample = BitConverter.GetBytes((float)(.2 * Math.Sin(2 * Math.PI * 440 * i / 48000)));
                sample.CopyTo(pcm, i * 8); sample.CopyTo(pcm, i * 8 + 4);
            }
            for (int i = 0; i < 30; i++) PrivateCall(backgroundAudio, "OnDataAvailable", null, new NAudio.Wave.WaveInEventArgs(pcm, pcm.Length));
            Check(hiddenFrames == 0, "实际收起链路仍采集音频证据，但不发布隐藏频谱帧");
            Check(double.IsFinite(backgroundAudio.LastSignalDbPeak) && backgroundAudio.SilentSeconds < 1,
                "收起后的音频证据仍能识别播放，避免误判暂停");
            int backgroundAnalyses = backgroundAudio.AnalysisCount;
            Check(backgroundAnalyses < 15, "收起后完整 FFT 降为音频证据节奏，不再逐包计算");
            PrivateCall(window, "SetState", IslandState.Pill, true, null);
            PumpAnimations(80);
            PrivateCall(window, "SyncVisualizers");
            spectrum.Visibility = System.Windows.Visibility.Visible;
            int resumedAt = backgroundAudio.AnalysisCount;
            for (int i = 0; i < 30; i++) PrivateCall(backgroundAudio, "OnDataAvailable", null, new NAudio.Wave.WaveInEventArgs(pcm, pcm.Length));
            Check(hiddenFrames > 0 && backgroundAudio.AnalysisCount - resumedAt > 15,
                "展开后立即恢复完整频谱，无需重建采集会话");
            Console.WriteLine($"FFT work for identical 30 audio packets: background={backgroundAnalyses}, expanded={backgroundAudio.AnalysisCount - resumedAt}");
            spectrum.Visibility = System.Windows.Visibility.Collapsed;
            Check(!PerformanceField<bool>(spectrum, "_rendering") && !backgroundAudio.VisualizationEnabled,
                "实际频谱控件隐藏时退订逐帧绘制并降为音频证据采样");
            spectrum.Visibility = System.Windows.Visibility.Visible;
            Check(backgroundAudio.VisualizationEnabled, "控件再次可见时立即恢复完整频谱");
            var pages = AgentField<IslandPageHost>(window, "_pages");
            State("Card"); PrivateCall(window, "SyncVisualizers");
            SetAgentField(window, "_runtimeEnabled", false);
            pages.SetActiveIndex(pages.IndexOf(IslandPageIds.Volume));
            PrivateCall(window, "SyncCardPageVisuals");
            Check(!wave.IsActive && !backgroundAudio.VisualizationEnabled,
                "切到音量卡后停止透明的媒体波浪和完整频谱计算");
            SetAgentField(window, "_runtimeEnabled", true);
            pages.SetActiveIndex(pages.IndexOf(IslandPageIds.Music));
            PrivateCall(window, "ApplyNoMedia");
            Check(AgentField<AudioSpectrumService?>(window, "_audio") == null,
                "媒体会话确认退出后释放音频采集和看门狗，避免无媒体后台常驻采样");
            SetAgentField(window, "_audio", null);
            media.SetDebugNowPlaying(null);
            State("Bar"); Pace();
            Check(!progress.IsEnabled && !ticker.IsRegistered("media-evidence") && probe.Interval.TotalMilliseconds == 2000,
                "无媒体时停止进度和音频证据心跳，SMTC 兜底每 2 秒探测");
            PrivateCall(window, "StartAudioSpectrum");
            Check(AgentField<AudioSpectrumService?>(window, "_audio") == null,
                "无媒体时不创建频谱服务及固定数组");
            media.SetDebugNowPlaying(info);
            State("Pill"); Pace();
            Check(progress.IsEnabled && progress.Interval.TotalMilliseconds == 100
                && ticker.IsRegistered("media-evidence") && probe.Interval.TotalMilliseconds == 500,
                "展开播放恢复 100ms 本地进度，播放时 SMTC 兜底仍为 500ms");
            PrivateCall(window, "ProgressTimer_Tick", null, EventArgs.Empty);
            Check(!string.IsNullOrWhiteSpace(((System.Windows.Controls.TextBlock)window.FindName("PositionText")).Text),
                "进度计时无需媒体 IPC 也能更新时间显示");
            media.SetDebugNowPlaying(info with { State = PlaybackState.Paused }); Pace();
            Check(progress.Interval.TotalMilliseconds == 500 && probe.Interval.TotalMilliseconds == 1000
                && ticker.IsRegistered("media-evidence"), "暂停降频但保留音频证据，可发现播放器恢复播放");
            media.SetDebugNowPlaying(info); State("Bar"); Pace();
            Check(progress.Interval.TotalMilliseconds == 500, "收起播放时进度降至 500ms");
            MotionPolicy.Apply(true, true); Pace();
            Check(progress.Interval.TotalMilliseconds == 1000, "节能模式收起进度进一步降至 1 秒");
            MotionPolicy.Apply(true, false);
            PrivateCall(window, "SyncHoverPacing");
            Check(!hover.IsEnabled && ticker.IsRegistered("idle-hover"),
                "完全收起后暂停 120ms 悬停计时，复用低频心跳保留热区检测");
            State("Pill"); Pace();
            Check(hover.IsEnabled && !ticker.IsRegistered("idle-hover") && progress.Interval.TotalMilliseconds == 100,
                "再次展开立即恢复悬停检查和平滑进度");
            media.SetDebugNowPlaying(null); Pace();
            Check(!progress.IsEnabled && !ticker.IsRegistered("media-evidence"), "媒体退出后及时停止两个媒体计时源");
        }
        finally
        {
            SetAgentField(window, "_audio", null);
            progress.Stop(); hover.Stop();
            SetAgentField(window, "_runtimeEnabled", false);
            SetAgentField(window, "_smtc", null);
            window.Hide();
        }
    }
}
