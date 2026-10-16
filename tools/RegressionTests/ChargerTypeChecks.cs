using System.Buffers.Binary;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;

internal static partial class Program
{
    private static async Task ChargerTypeChecks()
    {
        foreach (var (raw, expected) in new (uint, BatteryChargeType)[]
        {
            (0x10001, BatteryChargeType.StandardAC), (0x10002, BatteryChargeType.PD),
            (0x30002, BatteryChargeType.PD), (0x50001, BatteryChargeType.StandardAC),
            (0, BatteryChargeType.Unknown), (1, BatteryChargeType.Unknown), (2, BatteryChargeType.Unknown),
            (0x10000, BatteryChargeType.Unknown), (0x10003, BatteryChargeType.Unknown),
            (0x10101, BatteryChargeType.Unknown), (0x80010001, BatteryChargeType.Unknown),
            (0xFFFFFFFE, BatteryChargeType.Unknown), (0xFFFFFFFF, BatteryChargeType.Unknown)
        })
        {
            var bytes = new byte[16]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, raw);
            Check(AsusChargerTypeReader.Decode(true, 16, bytes) == expected, $"ASUS充电模式0x{raw:X8}解析为{expected}");
        }
        var valid = new byte[16]; BinaryPrimitives.WriteUInt32LittleEndian(valid, 0x10002);
        Check(AsusChargerTypeReader.Decode(false, 16, valid) == BatteryChargeType.Unknown
            && AsusChargerTypeReader.Decode(true, 3, valid) == BatteryChargeType.Unknown
            && AsusChargerTypeReader.Decode(true, 16, valid.AsSpan(0, 3)) == BatteryChargeType.Unknown,
            "调用失败或短输出不把缓冲区残留当成PD");
        var request = AsusChargerTypeReader.StatusRequest();
        Check(request.Length == 16 && BinaryPrimitives.ReadUInt32LittleEndian(request) == 0x53545344
            && BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(4)) == 8
            && BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(8)) == 0x0012006C
            && BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(12)) == 0,
            "接口请求只包含固定的DSTS状态查询，没有功耗或充电设置参数");
        Check(AsusChargerTypeReader.IsAsusManufacturer("ASUSTeK COMPUTER INC.")
            && !AsusChargerTypeReader.IsAsusManufacturer("LENOVO") && !AsusChargerTypeReader.IsAsusManufacturer(null),
            "非ASUS设备不尝试厂商硬件接口");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int reads = 0;
        var monitor = new ChargerTypeMonitor(() =>
        {
            Interlocked.Increment(ref reads); entered.Set(); release.Wait(); return BatteryChargeType.PD;
        });
        try
        {
            var clock = Stopwatch.StartNew();
            Check(monitor.ReadCached(true) == BatteryChargeType.Unknown && clock.ElapsedMilliseconds < 50,
                "厂商固件查询挂起不阻塞即时电池状态");
            Check(entered.Wait(2000), "后台厂商读取已开始");
            for (int i = 0; i < 50; i++) monitor.ReadCached(true);
            Check(reads == 1, "慢厂商接口只保留一个未完成请求");
            Check(monitor.ReadCached(false) == BatteryChargeType.Unknown, "断电立即停止采用旧的适配器类型");
            release.Set();
            await Task.Delay(40);
            Check(monitor.ReadCached(false) == BatteryChargeType.Unknown, "断电前迟到的PD查询不会覆盖电池供电状态");
            monitor.ReadCached(true);
            for (int i = 0; i < 50 && monitor.ReadCached(true) == BatteryChargeType.Unknown; i++) await Task.Delay(10);
            Check(monitor.ReadCached(true) == BatteryChargeType.PD && reads == 2,
                "重新接入电源后获取当前厂商类型，不复用前一轮查询");
        }
        finally { release.Set(); }
    }

    private static void ChargerTypeUiChecks(alpha_island.MainWindow window, Grid root)
    {
        using var service = new BatteryService(window.Dispatcher);
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        int updates = 0;
        service.BatteryStatusChanged += (_, info) => { updates++; PrivateCall(window, "OnBatteryStatusChanged", null, info); };
        void Sample(BatteryStatusInfo info)
        { PrivateCall(service, "ProcessStatusUpdate", info); PumpAnimations(70); root.UpdateLayout(); }
        var ac = new BatteryStatusInfo(79, false, BatteryChargeType.StandardAC, BatteryPowerEvent.None, null, IsOnAcPower: true);
        try
        {
            layers.HideAll(); Sample(ac);
            Check(window.SysCardBatteryDetail.Text.Contains("普通 AC") && window.ChargingPrimary.ToolTip.ToString()!.Contains("普通 AC"),
                "已确认的普通适配器在系统页和电量提示中显示");
            Sample(ac with { ChargeType = BatteryChargeType.PD });
            Check(updates == 2 && window.SysCardBatteryDetail.Text.Contains("USB-C PD")
                && window.ChargingPrimary.ToolTip.ToString()!.Contains("USB-C PD") && !layers.IsActive("battery"),
                "电量和AC状态不变时厂商类型变化也同步，避免重新播放插电动画");
            Sample(ac with { ChargeType = BatteryChargeType.Unknown });
            Check(window.SysCardBatteryDetail.Text.Contains("类型未知") && !window.SysCardBatteryDetail.Text.Contains("普通 AC"),
                "厂商不支持时显示未知，不把外部电源猜成普通AC");
            PrivateCall(window, "OnBatteryStatusChanged", null, ac with { EventType = BatteryPowerEvent.PluggedIn, ChargeType = BatteryChargeType.Unknown });
            Sample(ac with { ChargeType = BatteryChargeType.PD });
            Check(window.BatteryStatusText.Text.Contains("USB-C PD"), "厂商结果稍后返回时同步展开提醒文字，不重启动画");
        }
        finally { window.BatteryRipple.Stop(); layers.HideAll(); }
    }

    private static async Task ChargerTypeLiveCheck()
    {
        var watch = Stopwatch.StartNew(); var type = await Task.Run(AsusChargerTypeReader.Read);
        Console.WriteLine($"Actual ASUS charger: {type}, reader {watch.Elapsed.TotalMilliseconds:F2} ms");
        Check(type is BatteryChargeType.StandardAC or BatteryChargeType.PD, "当前电脑普通权限取得明确的厂商充电类型");
        using var service = new BatteryService(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var snapshot = service.QueryCurrentStatus();
        for (int i = 0; i < 30 && snapshot.ChargeType == BatteryChargeType.Unknown; i++)
        { await Task.Delay(50); snapshot = service.QueryCurrentStatus(); }
        Check(snapshot.IsOnAcPower == true && snapshot.ChargeType == type, "真实电池服务采用与ASUS接口一致的类型");
        Console.WriteLine($"All {_checks} live charger type checks passed.");
    }
}
