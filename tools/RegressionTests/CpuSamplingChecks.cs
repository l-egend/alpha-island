using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private sealed class CpuCounterFixture : ICpuPerformanceCounter
    {
        internal readonly Queue<CpuCounterReading> Readings = new();
        internal ManualResetEventSlim? Started, Release;
        internal int Reads, Disposals;
        public CpuCounterReading Read()
        {
            Interlocked.Increment(ref Reads); Started?.Set();
            if (Release != null && !Release.Wait(3000)) throw new TimeoutException("CPU fixture was not released");
            return Readings.Count > 0 ? Readings.Dequeue() : new(true, 87);
        }
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    private static void CpuSamplingPolicyChecks()
    {
        Check(CpuUsageService.UsesUtilityMetric(19045, 0) && CpuUsageService.UsesUtilityMetric(22631, 6199),
            "Windows 10 与 Windows 11 23H2 选择任务管理器的 Utility 口径");
        Check(CpuUsageService.UsesUtilityMetric(26100, 3623) && !CpuUsageService.UsesUtilityMetric(26100, 3624)
            && !CpuUsageService.UsesUtilityMetric(26200, 0), "24H2 统计更新前后及新版本使用对应口径");
        Check(CpuUsageService.NormalizePercent(175) == 100 && CpuUsageService.NormalizePercent(0) == 0,
            "睿频超过基准容量时总体 CPU 显示上限为 100%");
        Check(CpuUsageService.NormalizePercent(double.NaN) == null && CpuUsageService.NormalizePercent(double.PositiveInfinity) == null
            && CpuUsageService.NormalizePercent(-1) == null, "坏样本不能显示为正常占用值");
        var counter = new CpuCounterFixture();
        foreach (var reading in new[] { new CpuCounterReading(true, null), new(true, 12.5), new(true, 150), new(true, double.NaN), new(false, null) })
            counter.Readings.Enqueue(reading);
        long ticks = 0;
        using var service = new CpuUsageService(true, () => counter, () => { ticks++; return (ticks * 40, ticks * 70, ticks * 30); });
        service.Sample(); Check(service.CurrentPercent == null, "第一轮仅建立 CPU 采样基线，不显示假零值");
        service.Sample(); Check(service.CurrentPercent == 12.5 && service.Measurement == CpuMeasurement.ProcessorUtility,
            "Utility 有效时优先使用它而非旧算法的 60%");
        service.Sample(); Check(service.CurrentPercent == 100, "有效 Utility 样本按总体 CPU 上限显示");
        service.Sample(); Check(service.CurrentPercent == null, "无效数值清除旧读数");
        service.Sample(); Check(service.CurrentPercent == 60 && service.Measurement == CpuMeasurement.SystemTimes && counter.Disposals == 1,
            "性能计数器不可用时释放查询并使用持续维护的系统计时基线");
        int reads = counter.Reads;
        service.Sample(); Check(service.CurrentPercent == 60 && counter.Reads == reads, "不可用计数器进入重试冷却，不每秒重新创建");
        service.Dispose(); service.Dispose(); service.Sample();
        Check(service.CurrentPercent == null && counter.Disposals == 1, "重复关闭和迟到采样不会恢复旧 CPU 值或重复释放");
        using var modern = new CpuUsageService(false, () => new CpuCounterFixture(), () => null);
        modern.Sample(); Check(modern.CurrentPercent == 87 && modern.Measurement == CpuMeasurement.ProcessorTime,
            "新版口径及计时 API 失败不会覆盖有效性能计数器值");
        using var missing = new CpuPerformanceCounter(@"\AlphaIslandMissingCpuObject(_Total)\MissingCounter");
        Check(!missing.Read().Available, "真实不存在的计数器路径安全返回不可用");
    }

    private static async Task CpuLiveSamplingChecks()
    {
        using var service = new CpuUsageService();
        service.Sample(); Check(service.CurrentPercent == null, "真实 CPU 首轮采样等待下一次计数器值");
        bool utility = service.Measurement == CpuMeasurement.ProcessorUtility;
        using var reference = new CpuPerformanceCounter(utility);
        reference.Read();
        var differences = new List<double>();
        for (int i = 0; i < 4; i++)
        {
            await Task.Delay(CpuUsageService.SampleInterval);
            service.Sample(); var expected = reference.Read();
            Check(service.CurrentPercent is >= 0 and <= 100, "真实 CPU 样本有效且在总体百分比范围内 " + i);
            if (expected.Percent is double value && service.CurrentPercent is double actual)
                differences.Add(Math.Abs(actual - value));
            Console.WriteLine($"CPU live: selected={service.Measurement}, island={service.CurrentPercent:F2}%, reference={expected.Percent:F2}%");
        }
        Check(differences.Count == 4 && differences.Average() < 5, "岛读数与同口径 Windows 参考计数器的平均差小于 5 个百分点");
    }

    private static void CpuSamplingUiChecks(alpha_island.MainWindow window)
    {
        PrivateCall(window, "StartCpuService");
        var ticker = AgentField<IslandTicker>(window, "_ticker");
        Check(ticker.IsRegistered("cpu") && CpuUsageService.SampleInterval == TimeSpan.FromSeconds(1), "CPU 监测注册每秒采样");
        PrivateCall(window, "StopCpuService");
        PumpUntil(() => !AgentField<bool>(window, "_cpuSampling"));
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var counter = new CpuCounterFixture { Started = started, Release = release };
        var service = new CpuUsageService(true, () => counter, () => (0, 100, 0));
        SetAgentField(window, "_cpu", service);
        try
        {
            PrivateCall(window, "SampleCpu");
            PumpUntil(() => started.IsSet);
            PrivateCall(window, "SampleCpu");
            Check(counter.Reads == 1 && AgentField<bool>(window, "_cpuSampling"), "CPU 采集在后台执行且慢采样不会重叠");
            var watch = Stopwatch.StartNew(); PrivateCall(window, "StopCpuService");
            Check(watch.ElapsedMilliseconds < 500 && !ticker.IsRegistered("cpu") && AgentField<CpuUsageService?>(window, "_cpu") == null,
                "关闭监测立刻清除界面，不等待后台性能采集");
            release.Set();
            PumpUntil(() => !AgentField<bool>(window, "_cpuSampling") && counter.Disposals == 1);
            Check(AgentField<System.Windows.Controls.TextBlock>(window, "_cpuCardText").Text.Contains("未开启 / 等待采样"),
                "关闭后迟到的后台 CPU 结果不会回填界面");
        }
        finally { release.Set(); service.Dispose(); SetAgentField(window, "_cpu", null); }
    }
    private static void PumpUntil(Func<bool> completed)
    {
        var elapsed = Stopwatch.StartNew();
        while (!completed() && elapsed.ElapsedMilliseconds < 2000) PumpAnimations(10);
        if (!completed()) throw new TimeoutException("CPU regression wait timed out");
    }
}
