using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Plugins;
using alpha_island.Services;
using alpha_island.ViewModels;
using AlphaIsland.PluginSdk;
using Binding = System.Windows.Data.Binding;

internal static partial class Program
{
    private static void CoreLogicChecks()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var timer = new PomodoroTimer();
        Check(timer.Remaining == TimeSpan.FromMinutes(25) && !timer.Running, "番茄钟初始空闲时长");
        timer.Configure(new(FocusMinutes: 1, ShortBreakMinutes: 1));
        timer.Start(now); timer.Tick(now.AddSeconds(20));
        Check(timer.Running && timer.Remaining == TimeSpan.FromSeconds(40), "番茄钟按实际截止时间计时");
        timer.Pause(now.AddSeconds(30));
        timer.Configure(new(FocusMinutes: 2, ShortBreakMinutes: 1));
        Check(!timer.Running && timer.Remaining == TimeSpan.FromSeconds(30), "暂停中修改配置保留进度");
        timer.Start(now.AddHours(1));
        Check(!timer.Tick(now.AddHours(1).AddSeconds(29)) && timer.Tick(now.AddHours(1).AddSeconds(30)), "恢复后完成剩余时长");
        Check(timer.IsBreak && timer.CompletedFocus == 1 && !timer.Running, "手动模式阶段结束只切换一次");
        Check(!timer.Tick(now.AddDays(2)), "暂停阶段不会因系统休眠补发完成事件");
        timer.Reset();
        Check(!timer.IsBreak && timer.CompletedFocus == 0 && timer.Remaining == TimeSpan.FromMinutes(2), "重置恢复新配置");
        timer.Configure(new(FocusMinutes: 1, ShortBreakMinutes: 1, UseLongBreak: true,
            LongBreakMinutes: 3, LongBreakEvery: 2, AutoCycle: true, CycleCount: 2));
        timer.Start(now);
        Check(timer.Tick(now.AddMinutes(1)) && timer.IsBreak && timer.Running && !timer.IsLongBreak, "自动循环进入短休息");
        timer.Tick(now.AddMinutes(2)); timer.Tick(now.AddMinutes(3));
        Check(timer.IsBreak && timer.IsLongBreak && timer.Remaining == TimeSpan.FromMinutes(3), "指定轮次进入长休息");
        timer.Tick(now.AddMinutes(6));
        Check(timer.CycleFinished && !timer.Running && timer.CompletedFocus == 2, "完成指定循环后停止");
        timer.Start(now.AddMinutes(7));
        Check(timer.Running && timer.CompletedFocus == 0 && !timer.CycleFinished, "完成后开始新一轮会重置轮次");
        var sleep = new PomodoroTimer(); sleep.Configure(new(FocusMinutes: 1, AutoCycle: true)); sleep.Start(now);
        Check(sleep.Tick(now.AddDays(1)) && sleep.CompletedFocus == 1 && sleep.Remaining == TimeSpan.FromMinutes(5), "长时间休眠仅进入一个新阶段");

        var wheel = new WheelAccumulator();
        Check(wheel.Consume(30, 1000) == 0 && wheel.Consume(30, 1010) == 0
            && wheel.Consume(60, 1020) == 1, "高精度滚轮积累到一个刻度");
        Check(wheel.Consume(-60, 1030) == 0 && wheel.Consume(60, 1040) == 0
            && wheel.Consume(60, 1050) == 1, "反向滚动丢弃前一方向余量");
        wheel.Consume(60, 1100);
        Check(wheel.Consume(60, 1701) == 0, "超过 500ms 的滚轮余量过期");
        wheel.Consume(60, 1702);
        Check(wheel.Consume(60, 1703) == 0 && wheel.Consume(int.MaxValue, 1704) == 10,
            "异常大滚轮输入安全限幅，不因整数溢出反向");

        Check(!LrcParser.Parse(null, null).HasLyrics, "空歌词可安全解析");
        var lyrics = LrcParser.Parse("[ti:test]\n[00:00]作曲：测试\n[00:03.123]三\n[00:01.2][00:02.34]重复", "[00:01.20]译文");
        Check(lyrics.Lines.Count == 3 && lyrics.IsSynced, "LRC 元数据和制作人行过滤、多时间戳展开");
        Check(lyrics.Lines.Select(line => line.Time.TotalMilliseconds).SequenceEqual(new[] { 1200d, 2340d, 3123d }), "LRC 一至三位小数精度与排序");
        Check(lyrics.HasTranslation && lyrics.Lines[0].Translation == "译文", "翻译按时间戳对齐");
        Check(lyrics.IndexAt(TimeSpan.FromMilliseconds(1199)) == -1 && lyrics.IndexAt(TimeSpan.FromMilliseconds(2340)) == 1
            && lyrics.IndexAt(TimeSpan.FromHours(1)) == 2, "歌词二分定位覆盖首行之前、边界和末尾");
        var offset = LrcParser.Parse("[offset:1500]\n[00:01]一\n[00:03]二", null);
        Check(offset.Lines[0].Time == TimeSpan.Zero && offset.Lines[1].Time == TimeSpan.FromMilliseconds(1500), "歌词正偏移提前并在零截断");
        Check(LrcParser.Parse("[offset:9223372036854775807]\n[00:01]极端", null).Lines[0].Time == TimeSpan.Zero,
            "极端歌词偏移不使解析器溢出");
        Check(LrcParser.Parse("[offset:-9223372036854775808]\n[00:01]极端", null).Lines[0].Time == TimeSpan.MaxValue,
            "极端负偏移安全饱和到最大时长");
        var words = LrcParser.ParseWordTimings("[1000,800](1000,300,0)你(1300,500,0)好");
        Check(words.Count == 1 && words[0].Ends.SequenceEqual(new[] { 1.3, 1.8 }) && words[0].Chars.SequenceEqual(new[] { 1, 1 }), "逐字歌词读取结束时刻和字数");
        var timed = LrcParser.Parse("[00:01]你好", null).WithWordTimings(words);
        Check(timed.Lines[0].WordEnds?.Length == 2, "逐字时间附着到对应行");
        var plain = LrcParser.FromPlainText("第一行\n第二行", TimeSpan.FromSeconds(10), "test");
        Check(plain.Lines[1].Time == TimeSpan.FromSeconds(5), "纯文本歌词按给定时长分配");
    }

    private static void OptimizationTickerChecks()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                long now = 1000; int ticks = 0;
                using var ticker = new IslandTicker(Dispatcher.CurrentDispatcher, null, () => now);
                using var subscription = ticker.Subscribe("probe", TimeSpan.FromSeconds(10), () => ticks++);
                Check(ticker.ScheduledInterval == TimeSpan.FromSeconds(10), "仅有十秒任务时统一心跳无需每 250ms 唤醒");
                for (int i = 0; i < 20; i++) { now += 250; subscription.SetInterval(TimeSpan.FromSeconds(10)); ticker.RunDue(); }
                Check(ticks == 0, "重复应用相同周期不触发额外采样");
                now = 11000; ticker.RunDue();
                Check(ticks == 1, "重复应用相同周期不会无限推迟任务");
                subscription.SetPaused(true); now += 60000; ticker.RunDue();
                Check(ticks == 1 && !ticker.IsRunning, "所有订阅暂停时停止底层计时器");
                subscription.SetPaused(false); ticker.RunDue();
                Check(ticks == 1 && ticker.IsRunning, "恢复后不补跑过去积压的采样");
                now += 10000; ticker.RunDue(); Check(ticks == 2, "恢复后的完整周期只采一拍");
                using var replacement = ticker.Subscribe("probe", TimeSpan.FromSeconds(1), () => ticks += 10);
                subscription.Dispose(); now += 1000; ticker.RunDue();
                Check(ticks == 12 && ticker.IsRegistered("probe"), "旧订阅句柄释放不会注销同名新订阅");
                ticker.Register("first", TimeSpan.FromSeconds(1), () => ticker.Unregister("last"));
                int removed = 0; ticker.Register("last", TimeSpan.FromSeconds(1), () => removed++);
                now += 1000; ticker.RunDue(); Check(removed == 0, "同一拍中已取消的后续回调不会执行");
                ticker.Register("fault", TimeSpan.FromSeconds(1), () => throw new InvalidOperationException("fixture"));
                int survivor = 0; ticker.Register("survivor", TimeSpan.FromSeconds(1), () => survivor++);
                now += 1000; ticker.RunDue(); Check(survivor == 1, "单个轮询异常不会中断其他订阅");
                var active = ResourceSamplingPolicy.For(true, true, false, false, false);
                var folded = ResourceSamplingPolicy.For(false, false, false, false, false);
                var game = ResourceSamplingPolicy.For(true, true, false, true, false);
                Check(active.Cpu.TotalSeconds == 1 && folded.Cpu.TotalSeconds == 5 && folded.HardwarePower.TotalSeconds == 15,
                    "卡片可见保持快速刷新，折叠后 CPU 和功耗降频");
                Check(game.PauseHardware && game.Network.TotalSeconds == 5 && game.Memory.TotalSeconds == 10,
                    "游戏免打扰暂停硬件功耗，告警和网络累计保留低频采样");
                Check(ResourceSamplingPolicy.For(false, false, false, false, true).Cpu > folded.Cpu, "节能模式进一步降低采样频率");
                int Count(TimeSpan interval)
                {
                    long clock = 0; int count = 0;
                    using var schedule = new IslandTicker(Dispatcher.CurrentDispatcher, null, () => clock);
                    using var poll = schedule.Subscribe("sample", interval, () => count++);
                    for (clock = 250; clock <= 60000; clock += 250) schedule.RunDue();
                    return count;
                }
                int activeCpu = Count(active.Cpu), foldedCpu = Count(folded.Cpu);
                Check(activeCpu == 60 && foldedCpu == 12, "同一调度器模拟一分钟：CPU 采样由 60 次减到 12 次");
                Console.WriteLine($"Sampling: visible CPU={activeCpu}/min; folded CPU={foldedCpu}/min; this measures call count, not total CPU usage.");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }

    private static async Task OptimizationLogChecks(string root)
    {
        string dir = Path.Combine(root, "async-log"); Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "error.log");
        using (var sink = new RollingFileLogSink(path, 1024, 3))
        {
            bool allQueued = true;
            for (int i = 0; i < 250; i++) allQueued &= sink.TryWrite($"{i:0000} 中文日志 " + new string('a', 50));
            Check(allQueued, "批量日志入队成功");
            Check(await sink.FlushAsync(TimeSpan.FromSeconds(3)), "日志刷盘屏障覆盖之前的所有记录");
            var files = Directory.GetFiles(dir, "error*.log");
            Check(files.Length == 3 && files.All(file => new FileInfo(file).Length <= 1024), "后台日志轮转限制单文件大小和保留数量");
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
                Check(reader.ReadToEnd().Contains("0249 中文日志"), "最新日志完整保留中文字符");
            string zipPath = Path.Combine(dir, "live-diagnostics.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
                DiagnosticsReport.AddSharedLog(archive, path);
            using (var archive = System.IO.Compression.ZipFile.OpenRead(zipPath))
            using (var reader = new StreamReader(archive.Entries.Single().Open()))
                Check(reader.ReadToEnd().Contains("0249 中文日志"), "后台日志文件打开期间诊断包仍能完整打包");
        }
        using var writing = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using (var sink = new RollingFileLogSink(Path.Combine(dir, "slow.log"), 4096, 2, 4, () =>
        { writing.Set(); if (!release.Wait(3000)) throw new TimeoutException("fixture"); }))
        {
            sink.TryWrite("first"); Check(writing.Wait(2000), "慢磁盘测试已阻塞消费者");
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++) sink.TryWrite("queued");
            Check(watch.ElapsedMilliseconds < 500 && sink.Pending <= 4 && sink.Dropped > 0,
                "磁盘阻塞时生产者不会等待，日志队列有界并记录拥塞丢弃");
            release.Set(); Check(await sink.FlushAsync(TimeSpan.FromSeconds(3)), "磁盘恢复后队列可以排空");
        }
        writing.Reset(); release.Reset();
        using (var sink = new RollingFileLogSink(Path.Combine(dir, "reserved.log"), 4096, 2, 8, () =>
        { writing.Set(); if (!release.Wait(3000)) throw new TimeoutException("fixture"); }))
        {
            sink.TryWrite("first"); Check(writing.Wait(2000), "日志保留容量测试已阻塞消费者");
            for (int i = 0; i < 100; i++) sink.TryWrite("normal");
            Check(sink.Pending == 7 && sink.TryWrite("critical", critical: true), "普通日志拥塞时警告和错误仍有保留名额");
            release.Set(); Check(await sink.FlushAsync(TimeSpan.FromSeconds(3)), "保留的错误日志能完成刷盘");
        }
        string invalid = Path.Combine(dir, "not-a-directory"); File.WriteAllText(invalid, "file");
        using (var sink = new RollingFileLogSink(Path.Combine(invalid, "error.log"), 1024, 3))
        {
            Check(sink.TryWrite("failure"), "无权限或无效日志路径不会阻塞入队");
            await sink.FlushAsync(TimeSpan.FromSeconds(2));
            Check(sink.WriteFailures > 0 && sink.Pending == 0, "文件写入失败留在诊断计数中，不拖垮进程");
        }
    }

    private static void OptimizationUiChecks(alpha_island.MainWindow window, string root)
    {
        var monitor = AgentField<SystemResourceMonitor>(window, "_resourceMonitor");
        var values = monitor.Values;
        var changed = new List<string?>(); values.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        var sample = new SystemResourceSnapshot(31, 52, 10, 20, 30, "已用 8 / 16 GB", "fixture");
        PrivateCall(window, "UpdateResourceSnapshot", sample); PumpAnimations(30);
        Check(AgentField<System.Windows.Controls.TextBlock>(window, "SysCardCpuValue").Text == "31%"
            && AgentField<System.Windows.Controls.TextBlock>(window, "SysCardPowerValue").Text == "30.0 W", "系统卡片由 ViewModel 绑定 CPU 和功耗");
        changed.Clear(); values.Update(sample);
        Check(changed.Count == 0, "重复快照不重复触发属性变更或布局");
        values.Update(sample with { CpuPercent = 45 });
        Check(changed.Contains(nameof(SystemResourcesViewModel.CpuText)) && !changed.Contains(nameof(SystemResourcesViewModel.MemoryText)), "局部资源变化只通知相关显示属性");

        var pages = AgentField<IslandPageHost>(window, "_pages");
        var state = AgentField<IslandState>(window, "_state"); int page = pages.ActiveIndex;
        try
        {
            pages.SetActiveIndex(pages.IndexOfOrDefault(IslandPageIds.System));
            SetAgentField(window, "_state", IslandState.Card); PrivateCall(window, "SyncResourceSampling");
            var visible = AgentField<ResourceSamplingProfile?>(window, "_lastResourceProfile")!.Value;
            SetAgentField(window, "_state", IslandState.Bar); PrivateCall(window, "SyncResourceSampling");
            var folded = AgentField<ResourceSamplingProfile?>(window, "_lastResourceProfile")!.Value;
            Check(visible.Cpu.TotalSeconds == 1 && folded.Cpu.TotalSeconds == 5, "真实窗口从系统卡片收回后使用折叠采样档位");
            var schedule = AgentField<IslandTicker>(window, "_ticker");
            int reads = 0;
            using var network = schedule.Subscribe("netspeed", TimeSpan.FromSeconds(5), () =>
            { reads++; PrivateCall(window, "SyncResourceSampling"); });
            pages.SetActiveIndex(pages.IndexOfOrDefault(IslandPageIds.NetSpeed));
            SetAgentField(window, "_state", IslandState.Card);
            SetAgentField(window, "_lastResourceProfile", (ResourceSamplingProfile?)(folded with { Network = TimeSpan.FromSeconds(5) }));
            network.TriggerNow();
            Check(reads == 1, "网速切回快速档位不会在当前采样回调内重复采样");
        }
        finally { SetAgentField(window, "_state", state); pages.SetActiveIndex(page); PrivateCall(window, "SyncResourceSampling"); }

        var rotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
        for (int i = 0; i < 20; i++)
            AnimationReset.Once(rotation, AxisAngleRotation3D.AngleProperty, new DoubleAnimation(-90, 0, TimeSpan.FromMilliseconds(30)), 0d);
        PumpAnimations(110);
        Check(!rotation.HasAnimatedProperties && rotation.Angle == 0, "快速重复封面翻转完成后不残留动画时钟");
        var translate = new TranslateTransform(7, 0);
        AnimationReset.Once(translate, TranslateTransform.XProperty, new DoubleAnimation(7, 20, TimeSpan.FromMilliseconds(20)), 20d);
        PumpAnimations(70);
        Check(!translate.HasAnimatedProperties && translate.X == 20, "有限位移动画清理时钟并保留所需终值");
        CpuSamplingUiChecks(window);
        using (var ticker = new IslandTicker(window.Dispatcher))
        {
            var power = new OptimizationPowerSource();
            using var probes = new SystemResourceMonitor(window.Dispatcher, ticker, createPower: () => new HardwarePowerService(power));
            probes.ApplyProfile(ResourceSamplingPolicy.For(true, true, false, true, false));
            probes.SetPowerEnabled(true); PumpAnimations(60);
            Check(power.Samples == 0 && ticker.Report().Contains("hardwarepower：已暂停"), "免打扰期间启动功耗模块也不会读取硬件");
            probes.ApplyProfile(ResourceSamplingPolicy.For(true, true, false, false, false));
            PumpUntil(() => probes.Values.Snapshot.TotalWatts == 30);
            Check(power.Samples == 1, "游戏退出后立即请求新的功耗样本");
            probes.SetPowerEnabled(false);
            Check(probes.Values.Snapshot.TotalWatts == null, "关闭硬件监测立即清空绑定状态");
        }
        PluginActionFailureChecks(window.Dispatcher, root);
    }

    private sealed class OptimizationPowerSource : AlphaIsland.Hardware.IHardwarePowerSource
    {
        internal int Samples;
        public AlphaIsland.Hardware.HardwarePowerReading Sample()
        { Interlocked.Increment(ref Samples); return new(10, 20, "fixture"); }
        public void Dispose() { }
    }

    private static void PluginActionFailureChecks(Dispatcher dispatcher, string root)
    {
        string plugins = Path.Combine(root, "action-plugins"); Directory.CreateDirectory(plugins);
        foreach (var id in new[] { "fixture.action-fails", "fixture.available-fails", "fixture.action-good" })
        {
            string folder = Path.Combine(plugins, id); Directory.CreateDirectory(folder);
            File.Copy(Path.GetFullPath("tools/PluginFixture/bin/Release/net8.0-windows/AlphaIsland.PluginFixture.dll"), Path.Combine(folder, "AlphaIsland.PluginFixture.dll"));
            File.WriteAllText(Path.Combine(folder, "plugin.json"), JsonSerializer.Serialize(new
            { id, name = id, version = "1.0.0", apiVersion = 1, assembly = "AlphaIsland.PluginFixture.dll", entryType = "AlphaIsland.PluginFixture.ProbePlugin" }));
        }
        var options = new IslandOptions();
        foreach (string folder in Directory.GetDirectories(plugins)) options.Extras[PluginHost.EnabledKey(Path.GetFileName(folder))] = JsonSerializer.SerializeToElement(true);
        using var host = new PluginHost(dispatcher, options, new[] { plugins }, _ => { }, _ => { }, _ => { });
        host.Discover(); PumpUntilState(() => host.Entries.All(entry => entry.Running || entry.Error.Length > 0));
        var actions = host.AvailableActions;
        Check(host.Entries.Single(entry => entry.Manifest?.Id == "fixture.available-fails").Error.Contains("availability failed"), "插件可用性回调失败只停用该插件");
        host.InvokeAction("fixture.action-fails"); PumpUntilState(() => host.Entries.Single(entry => entry.Manifest?.Id == "fixture.action-fails").Error.Length > 0);
        Check(host.Entries.Single(entry => entry.Manifest?.Id == "fixture.action-fails").Error.Contains("action failed"), "插件操作抛出异常由宿主捕获并清理");
        Check(host.InvokeAction("fixture.action-good") && host.Entries.Single(entry => entry.Manifest?.Id == "fixture.action-good").Running, "故障插件不影响其他插件继续操作");
        host.Dispose(); AwaitPlugin(host.ShutdownCompletion.ContinueWith(_ => true));
    }
}
