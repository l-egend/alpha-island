using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static async Task BatterySamplingLatencyChecks()
    {
        using var release = new ManualResetEventSlim();
        int reports = 0;
        using var service = new BatteryService(Dispatcher.CurrentDispatcher, () =>
        {
            Interlocked.Increment(ref reports); release.Wait();
            return new(Windows.System.Power.BatteryStatus.Charging, 5000, 40000, 50000);
        }, () => new NativeBatterySnapshot(1, 8, 80, 0, -1));
        try
        {
            var clock = Stopwatch.StartNew(); var status = service.QueryCurrentStatus();
            double elapsed = clock.Elapsed.TotalMilliseconds;
            Console.WriteLine($"Stalled battery report snapshot: {elapsed:F1} ms");
            Check(elapsed < 50 && status.IsOnAcPower == true && status.IsCharging && status.Percent == 80,
                "详细电池报告挂起时仍在50毫秒内返回实际电源与电量状态");
            for (int i = 0; i < 8; i++) service.QueryCurrentStatus();
            await Task.Delay(20);
            Check(reports == 1, "挂起的电池报告只保留一个后台请求，不因轮询堆积线程");
        }
        finally { release.Set(); }
    }
    private static async Task BatteryBroadcastCoalescingChecks()
    {
        int samples = 0, value = 0, sampled = -1;
        using var coordinator = new BatterySampleCoordinator(() =>
        {
            Interlocked.Increment(ref samples); sampled = Volatile.Read(ref value);
        });
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) { Volatile.Write(ref value, i); coordinator.Request(); coordinator.Request(150); }
        Check(clock.ElapsedMilliseconds < 100, "一千次电源广播的入队不阻塞UI线程");
        await Task.Delay(350);
        Console.WriteLine($"1000 broadcasts -> {samples} samples");
        Check(samples is >= 1 and <= 8 && sampled == 999, "电源事件突发合并为少量采样且读取最终状态");
        coordinator.Dispose(); int stopped = samples;
        coordinator.Request(); await Task.Delay(100);
        Check(samples == stopped, "采样器停止后不执行迟到电源回调");
    }
    private static void PowerFrameStallChecks(alpha_island.MainWindow window, Grid root)
    {
        bool reduce = MotionPolicy.ReduceMotion, save = MotionPolicy.PowerSave;
        int tier = MotionPolicy.RenderTier;
        try
        {
            MotionPolicy.Apply(false, false); MotionPolicy.OverrideRenderTierForDebug(2);
            var layers = AgentField<IslandLayerHost>(window, "_layers");
            AgentField<IslandOptions>(window, "_options").BatteryNotifyEnabled = true;
            PrivateCall(window, "SetState", IslandState.Bar, true);
            PrivateCall(window, "OnBatteryStatusChanged", null,
                new BatteryStatusInfo(79, true, BatteryChargeType.StandardAC, BatteryPowerEvent.PluggedIn, null, IsOnAcPower: true));
            var animator = AgentField<IslandAnimator>(window, "_animator");
            Check(animator.PreservePowerTransitionMotion && animator.IsRunning, "电源切换形变使用临时慢帧保护");
            for (int i = 0; i < 4; i++)
            {
                SetAgentField(animator, "_lastTick", (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency - .2);
                PrivateCall(animator, "OnRendering", null, EventArgs.Empty);
            }
            Check(animator.IsRunning && AgentField<double>(animator, "_elapsed") <= .21,
                "连续电源慢帧不会把展开降级成瞬变，也不消耗整段播放时间");
            window.BatteryRipple.Trigger(true);
            SetAgentField(window.BatteryRipple, "_startTimestamp", Stopwatch.GetTimestamp() - 3 * Stopwatch.Frequency);
            PrivateCall(window.BatteryRipple, "OnRendering", null, EventArgs.Empty);
            Check(AgentField<bool>(window.BatteryRipple, "_animating") && AgentField<double>(window.BatteryRipple, "_progress") < .1,
                "电源切换造成三秒停顿后波纹继续播放，不直接跳过整段动画");
        }
        finally
        {
            window.BatteryRipple.Stop();
            AgentField<IslandLayerHost>(window, "_layers").HideAll();
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduce, save);
        }
    }
}
