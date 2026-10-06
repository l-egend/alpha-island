using AlphaIsland.Hardware;
using alpha_island.Services;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;

internal static partial class Program
{
    private static async Task HardwarePowerArchitectureChecks(bool elevated)
    {
        var now = DateTime.UtcNow;
        var sample = new HardwarePowerReading(25, 15, "") { SampledUtc = now, Source = "test" };
        Check(HardwarePowerProtocol.Decode(HardwarePowerProtocol.Encode(sample), now).TotalWatts == 40, "独立读取协议保留实际 CPU 和独显瓦数");
        Check(HardwarePowerProtocol.Decode(HardwarePowerProtocol.Encode(new(null, 15, "CPU 权限不足")), now).TotalWatts == null,
            "跨进程传输保留缺失读数与原因");
        Check(new HardwarePowerReading(0, 15, "").TotalWatts == null && new HardwarePowerReading(double.NaN, 15, "").TotalWatts == null,
            "读取模型本身拒绝初始化假零和非法数值");
        void Rejected(byte[] bytes, string name)
        {
            bool rejected = false;
            try { HardwarePowerProtocol.Decode(bytes, now); } catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException) { rejected = true; }
            Check(rejected, name);
        }
        Rejected(HardwarePowerProtocol.Encode(sample with { SampledUtc = now.AddSeconds(-11) }), "拒绝过期的跨进程功耗快照");
        Rejected(HardwarePowerProtocol.Encode(sample with { SampledUtc = now.AddSeconds(5) }), "拒绝未来时间的功耗快照");
        Rejected(HardwarePowerProtocol.Encode(sample with { CpuWatts = 0 }), "拒绝跨进程的 CPU 假零");
        Rejected(HardwarePowerProtocol.Encode(sample with { GpuWatts = -1 }), "拒绝跨进程的负功耗");
        Rejected(Encoding.UTF8.GetBytes("{\"Version\":2,\"Reading\":null}"), "协议版本变化不会被静默接受");
        Rejected(new byte[HardwarePowerProtocol.MaximumBytes + 1], "消息大小有明确上限");
        using var stream = new MemoryStream();
        await HardwarePowerProtocol.WriteAsync(stream, sample, CancellationToken.None); stream.Position = 0;
        Check((await HardwarePowerProtocol.ReadAsync(stream, CancellationToken.None)).GpuWatts == 15, "有边界的消息帧可完整往返");
        using var malformed = new MemoryStream(new byte[4]);
        bool invalidFrame = false;
        try { await HardwarePowerProtocol.ReadAsync(malformed, CancellationToken.None); } catch (InvalidDataException) { invalidFrame = true; }
        Check(invalidFrame, "非法帧长度在分配内存前拒绝");
        using var truncated = new MemoryStream(new byte[] { 30, 0, 0, 0, 1 });
        bool shortFrame = false;
        try { await HardwarePowerProtocol.ReadAsync(truncated, CancellationToken.None); } catch (EndOfStreamException) { shortFrame = true; }
        Check(shortFrame, "连接截断不产生部分功耗读数");
        var fake = new FixturePowerSource(sample);
        var service = new HardwarePowerService(fake);
        service.Sample(); Check(service.Current.TotalWatts == 40, "采样协调器通过可替换数据源工作");
        service.Dispose(); service.Dispose(); service.Sample();
        Check(fake.Samples == 1 && fake.Disposals == 1, "停止采样后无迟到读取，数据源只释放一次");
        Check(File.Exists(Path.Combine(AppContext.BaseDirectory, "hardware", "AlphaIsland.HardwareWorker.exe")), "构建产物包含独立读取程序");
        var before = Process.GetProcessesByName("AlphaIsland.HardwareWorker").Select(p => p.Id).ToHashSet();
        using var client = new HardwarePowerWorkerClient();
        string status = await client.StartAsync(elevated);
        System.Console.WriteLine("Worker startup: " + status);
        var first = client.Current;
        Check(first != null && first.Source.Contains("独立硬件"), "真实读取进程通过同用户管道和 PID 校验连接");
        await Task.Delay(2400);
        var next = client.Current;
        Check(next != null && next.SampledUtc > first!.SampledUtc, "独立进程持续发送新硬件快照");
        System.Console.WriteLine("Independent hardware live: " + next!.Description);
        if (elevated)
        {
            Check(next.CpuWatts > 0 && next.GpuWatts.HasValue && next.TotalWatts.HasValue, "本机管理员读取获得 CPU＋独显完整功耗");
            File.WriteAllText("bin/power-live.json", Encoding.UTF8.GetString(HardwarePowerProtocol.Encode(next)));
        }
        var workerIds = Process.GetProcessesByName("AlphaIsland.HardwareWorker").Select(p => p.Id).Where(id => !before.Contains(id)).ToArray();
        client.Stop(); Check(client.Current == null, "关闭读取后立即清除快照");
        await Task.Delay(4500);
        Check(workerIds.Length > 0 && workerIds.All(id => { try { using var p = Process.GetProcessById(id); return p.HasExited; } catch (ArgumentException) { return true; } }),
            "关闭管道后读取进程自行退出，无需管理员终止进程");
    }
    private sealed class FixturePowerSource(HardwarePowerReading value) : IHardwarePowerSource
    {
        internal int Samples, Disposals;
        public HardwarePowerReading Sample() { Samples++; return value; }
        public void Dispose() { Disposals++; }
    }
}
