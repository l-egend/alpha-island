using AlphaIsland.Hardware;
using alpha_island.Services;
using System.Buffers.Binary;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

internal static partial class Program
{
    private static async Task HardwarePowerArchitectureChecks()
    {
        var now = DateTime.UtcNow;
        var sample = new HardwarePowerReading(25, 15, "") { SampledUtc = now, Source = "test" };
        Check(new HardwarePowerReading(0, 15, "").TotalWatts == null && new HardwarePowerReading(double.NaN, 15, "").TotalWatts == null,
            "读取模型拒绝初始化假零和非法数值");
        Check(sample.FreshAt(now.AddSeconds(11)).TotalWatts == null && sample.FreshAt(now.AddSeconds(-5)).TotalWatts == null,
            "过期和未来功耗快照不继续显示");
        Check(HardwarePowerPolicy.EnergyWatts(1, 1, 2, 100_000_002) == null && HardwarePowerPolicy.EnergyWatts(1, 1, 1, 2) == null,
            "休眠造成的长间隔和零能量差不产生错误功耗");
        byte[] Metadata(ushort version, (string Name, uint Unit)[] channels)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            void FixedName(string name) { var bytes = new byte[32]; Encoding.Unicode.GetBytes(name).CopyTo(bytes, 0); writer.Write(bytes); }
            if (version == 1) writer.Write(channels[0].Unit);
            FixedName("Microsoft"); FixedName("PPM"); writer.Write((ushort)0);
            if (version == 1)
            {
                var bytes = Encoding.Unicode.GetBytes(channels[0].Name + '\0'); writer.Write((ushort)bytes.Length); writer.Write(bytes);
            }
            else
            {
                writer.Write((ushort)channels.Length);
                foreach (var channel in channels)
                {
                    var bytes = Encoding.Unicode.GetBytes(channel.Name + '\0'); writer.Write(channel.Unit); writer.Write((ushort)bytes.Length); writer.Write(bytes);
                }
            }
            return stream.ToArray();
        }
        var v1 = EmiMetadata.Parse(1, Metadata(1, [("RAPL_Package0_PKG", 0)]));
        Check(v1.Oem == "Microsoft" && v1.Model == "PPM" && v1.Channels.Single().Name == "RAPL_Package0_PKG", "解析 Windows EMI V1 可变长度元数据");
        var v2bytes = Metadata(2, [("RAPL_Package0_PKG", 0), ("RAPL_Package0_PP1", 0), ("odd", 1)]);
        var v2 = EmiMetadata.Parse(2, v2bytes);
        Check(v2.Channels.Length == 3 && v2.Channels[1].Name == "RAPL_Package0_PP1" && v2.Channels[2].Unit == 1,
            "EMI V2 多通道不按结构体对齐而误读后续名称");
        void Rejected(ushort version, byte[] bytes, string name)
        {
            bool rejected = false;
            try { EmiMetadata.Parse(version, bytes); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, name);
        }
        Rejected(3, v2bytes, "拒绝未知 EMI 版本");
        Rejected(2, v2bytes[..^1], "拒绝截断 EMI 通道名称");
        var oddName = v2bytes.ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(oddName.AsSpan(72), 3);
        Rejected(2, oddName, "拒绝奇数 UTF-16 名称长度");
        var manyChannels = v2bytes.ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(manyChannels.AsSpan(66), 257);
        Rejected(2, manyChannels, "限制 EMI 通道数量防止异常分配");
        Rejected(1, new byte[71], "拒绝不足 EMI V1 固定头长度");
        var fake = new FixturePowerSource(sample);
        var service = new HardwarePowerService(fake);
        service.Sample(); Check(service.Current.TotalWatts == 40, "采样协调器继续支持可替换项目数据源");
        service.Dispose(); service.Dispose(); service.Sample();
        Check(fake.Samples == 1 && fake.Disposals == 1, "停止采样后无迟到读取，数据源只释放一次");
        var cpu = new FixtureComponentSource(new(25, "", "CPU test"));
        var gpu = new FixtureComponentSource(new(15, "", "GPU test"));
        var composed = new WindowsHardwarePowerSource(cpu, gpu);
        Check(composed.Sample().TotalWatts == 40, "CPU 与显卡厂商接口可分别扩展");
        gpu.Value = new(null, "显卡已断开", "GPU test");
        var missing = composed.Sample();
        Check(missing.CpuWatts == 25 && missing.GpuWatts == null && missing.TotalWatts == null && missing.Reason == "显卡已断开",
            "新采样丢失组件时立即清除旧值，保留分项与原因");
        composed.Dispose(); composed.Dispose();
        Check(cpu.Disposals == 1 && gpu.Disposals == 1, "关闭读取释放两个原生后端且只释放一次");
        using var identity = WindowsIdentity.GetCurrent();
        Check(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator), "实机验证在普通 Windows 权限下进行");
        using var live = new HardwarePowerService();
        await Task.Run(live.Sample);
        Check(live.Current.CpuWatts == null && live.Current.Reason.Contains("基线"), "首次 CPU 原生采样建立基线，不显示假零");
        await Task.Delay(2100); await Task.Run(live.Sample);
        var first = live.Current;
        Console.WriteLine("Native hardware live: " + first.Description);
        Check(first.CpuWatts > 0 && first.GpuWatts.HasValue && first.TotalWatts > 0, "本机普通权限取得 CPU＋独显完整功耗");
        await Task.Delay(2100); await Task.Run(live.Sample);
        var next = live.Current;
        Check(next.IsFresh(DateTime.UtcNow) && next.SampledUtc > first.SampledUtc && next.TotalWatts > 0, "原生采样持续更新 CPU＋GPU 读数");
        File.WriteAllText("bin/emi-power-live.json", JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name?.StartsWith("LibreHardwareMonitor", StringComparison.Ordinal) == true),
            "实机采样无需加载 LibreHardwareMonitor");
        Check(!Directory.Exists(Path.Combine(AppContext.BaseDirectory, "hardware")), "新构建不携带硬件读取辅助进程");
    }
    private sealed class FixturePowerSource(HardwarePowerReading value) : IHardwarePowerSource
    {
        internal int Samples, Disposals;
        public HardwarePowerReading Sample() { Samples++; return value; }
        public void Dispose() { Disposals++; }
    }
    private sealed class FixtureComponentSource(ComponentPowerReading value) : IComponentPowerSource
    {
        internal ComponentPowerReading Value = value;
        internal int Disposals;
        public ComponentPowerReading Sample() => Value;
        public void Dispose() { Disposals++; }
    }
}
