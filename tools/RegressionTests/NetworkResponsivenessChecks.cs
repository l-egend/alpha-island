using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void NetworkResponsivenessChecks(alpha_island.MainWindow window)
    {
        var previousNetwork = AgentField<NetworkSpeedService?>(window, "_netSpeed");
        var previousReader = AgentField<Action<NetworkSpeedService>>(window, "_readNetworkCounters");
        var previousNavigation = AgentField<AgentAppNavigationService>(window, "_agentNavigation");
        var options = AgentField<IslandOptions>(window, "_options");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        const string owner = "external:codex/slow-network-ui";
        using var service = new NetworkSpeedService();
        int reads = 0, workerThread = 0, uiThread = Environment.CurrentManagedThreadId;
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            SetAgentField(window, "_netSpeed", service);
            SetAgentField(window, "_agentNavigation", new AgentAppNavigationService(new AgentWindowFixture()));
            SettingsRegistry.Write(options, "AgentStatusEnabled", true);
            PrivateCall(window, "StartAgentStatus");
            SetAgentField(window, "_readNetworkCounters", new Action<NetworkSpeedService>(sample =>
            {
                Interlocked.Increment(ref reads); workerThread = Environment.CurrentManagedThreadId; started.Set();
                release.Wait(350);
                sample.ApplyByteDeltas(8192, 1024, 1);
            }));
            var watch = Stopwatch.StartNew(); PrivateCall(window, "SampleNetworkSpeed"); watch.Stop();
            Console.WriteLine($"TIMING: slow-network-ui-call={watch.Elapsed.TotalMilliseconds:0.0}ms");
            Check(watch.ElapsedMilliseconds < 100, "慢网卡读取不阻塞实际网速心跳的界面调用");
            Check(started.Wait(1000) && workerThread != uiThread, "网卡驱动在后台读取");
            for (int i = 0; i < 80; i++) PrivateCall(window, "SampleNetworkSpeed");
            Check(reads == 1, "慢读取期间重复心跳合并，不堆积线程或请求");
            bool heartbeat = false;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => heartbeat = true));
            var report = Task.Run(() => AgentStatus.Set(AgentRunState.Thinking, "Codex 响应检查", owner));
            while ((!heartbeat || !report.IsCompleted || !AgentField<bool>(window, "_agentLayerUp")) && watch.ElapsedMilliseconds < 150)
            { watch.Start(); PumpAnimations(5); }
            Check(heartbeat && AgentField<bool>(window, "_agentLayerUp") && layers.IsActive("agentstatus"),
                "驱动仍慢读时输入心跳和 Codex 开始状态照常显示");
            AgentStatus.Reset(owner: owner); PumpAnimations(25);
            Check(!layers.IsActive("agentstatus"), "驱动慢读时 Codex 结束状态也能及时收回");
            release.Set();
            var complete = Stopwatch.StartNew();
            while ((AgentField<Task?>(window, "_networkReadTask") != null) && complete.ElapsedMilliseconds < 1500) PumpAnimations(5);
            Check(AgentField<Task?>(window, "_networkReadTask") == null && service.Current.DownBytesPerSecond == 8192,
                "后台读取完成后正常发布网速和清理待处理标志");

            int lateReads = 0;
            using var lateStarted = new ManualResetEventSlim(); using var lateRelease = new ManualResetEventSlim();
            SetAgentField(window, "_readNetworkCounters", new Action<NetworkSpeedService>(sample =>
            {
                lateStarted.Set(); lateRelease.Wait(1500); sample.ApplyByteDeltas(100, 50, 1); Interlocked.Increment(ref lateReads);
            }));
            PrivateCall(window, "SampleNetworkSpeed"); Check(lateStarted.Wait(1000), "停用检查的慢读取已开始");
            watch.Restart(); PrivateCall(window, "StopNetworkSpeedService"); watch.Stop();
            Check(watch.ElapsedMilliseconds < 100 && AgentField<NetworkSpeedService?>(window, "_netSpeed") == null,
                "停用服务不等待网卡驱动，不阻塞游戏切换或退出");
            lateRelease.Set();
            complete.Restart(); while (lateReads == 0 && complete.ElapsedMilliseconds < 1500) PumpAnimations(5);
            PumpAnimations(30);
            Check(AgentField<NetworkSpeedService?>(window, "_netSpeed") == null && window.BarSpeedDownText.Text.Length == 0,
                "停用后的旧采样结果不会重新显示网速或复活服务");

            using var retryService = new NetworkSpeedService(); int retryReads = 0;
            SetAgentField(window, "_netSpeed", retryService);
            SetAgentField(window, "_readNetworkCounters", new Action<NetworkSpeedService>(sample =>
            {
                if (Interlocked.Increment(ref retryReads) == 1) throw new InvalidOperationException("故障注入");
                sample.ApplyByteDeltas(2048, 512, 1);
            }));
            PrivateCall(window, "SampleNetworkSpeed"); complete.Restart();
            while (AgentField<Task?>(window, "_networkReadTask") != null && complete.ElapsedMilliseconds < 1500) PumpAnimations(5);
            Check(AgentField<Task?>(window, "_networkReadTask") == null && AgentField<long>(window, "_networkReadFailures") == 1,
                "读取异常释放单次采样门禁并记录诊断，不把服务卡在处理中");
            PrivateCall(window, "SampleNetworkSpeed"); complete.Restart();
            while (AgentField<Task?>(window, "_networkReadTask") != null && complete.ElapsedMilliseconds < 1500) PumpAnimations(5);
            Check(retryReads == 2 && retryService.Current.DownBytesPerSecond == 2048,
                "驱动下一次读取恢复时继续发布，累计流量不重复计算");
        }
        finally
        {
            release.Set(); AgentStatus.Reset(owner: owner); PumpAnimations(20);
            PrivateCall(window, "StopNetworkSpeedService");
            SetAgentField(window, "_netSpeed", previousNetwork); SetAgentField(window, "_readNetworkCounters", previousReader);
            SetAgentField(window, "_agentNavigation", previousNavigation);
        }
    }
}
