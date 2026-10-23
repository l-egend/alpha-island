using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AlphaIsland.PluginSdk;
using AlphaIsland.Plugins.DeviceBattery;
using alpha_island.Core;
using alpha_island.Controls;
using Grid = System.Windows.Controls.Grid;

internal static partial class Program
{
    private static void DeviceBatteryPolicyChecks()
    {
        byte[] atk = [8,0x72,2,0,1,7,1,100];
        Check(BatteryProtocols.ParseAtk(atk) == 100, "ATK 已知协议的满电读数"); atk[7] = 0;
        Check(BatteryProtocols.ParseAtk(atk) == 0, "真实零电量不会被当作未知"); atk[7] = 101;
        Check(BatteryProtocols.ParseAtk(atk) == null && BatteryProtocols.ParseAtk(atk[..5]) == null, "ATK 无效与截断响应被拒绝");
        byte[] dark = BatteryProtocols.DarkrimQuery(37); dark[8] = 3; dark[10] = 1; dark[11] = 1; dark[12] = 72;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(dark.AsSpan(13), BatteryProtocols.Crc(dark.AsSpan(1,12)));
        Check(BatteryProtocols.ParseDarkrim(dark,37) == 72, "DARKRIM 电量帧通过 CRC 与 TLV 校验");
        Check(BatteryProtocols.ParseDarkrim(dark,38) == null && BatteryProtocols.ParseDarkrim(dark[..13],37) == null, "拒绝上一轮与截断的电量响应");
        dark[12] = 73; Check(BatteryProtocols.ParseDarkrim(dark,37) == null, "CRC 错误不能变成电量");
        Check(BatteryProtocols.EstimateLogitechVoltage(3499) == null && BatteryProtocols.EstimateLogitechVoltage(4501) == null,
            "罗技非合理锂电电压不能套用估算曲线");
        Check(BatteryProtocols.EstimateLogitechVoltage(4000) > BatteryProtocols.EstimateLogitechVoltage(3800), "标记为估算的电量随电压单调变化");
        Check(XInputBatteryReader.Level(2,2) == "中" && XInputBatteryReader.Level(3,3) == "充足" && XInputBatteryReader.Level(255,3) == "",
            "手柄档位与未知状态不伪造百分比");
        var mouse = new HidInterface("test", "mouse", "MCHOSE K5 Ultra", 0x3837, 0x1018, new HidNative.Caps { UsagePage=1, Usage=2 });
        Check(BatteryScanner.KindFor([mouse, mouse with { Caps = new HidNative.Caps { UsagePage=1, Usage=6 } }]) == DeviceBatteryKind.Mouse,
            "K5 Ultra 等复合接口鼠标不会被键盘宏接口误分类");
        var logi = mouse with { Vid=0x046D,Pid=0xC539,Caps=new HidNative.Caps {UsagePage=0xFF00,Usage=2,InputBytes=20,OutputBytes=20} };
        Check(BatteryScanner.IsLogitechQueryInterface(logi), "G502 使用 HID++ 长报文查询通道");
        Check(!BatteryScanner.IsLogitechQueryInterface(logi with { Caps=new HidNative.Caps {UsagePage=0xFF00,Usage=4,InputBytes=32,OutputBytes=32} })
            && !BatteryScanner.IsLogitechQueryInterface(logi with { Caps=new HidNative.Caps {UsagePage=0xFF00,Usage=1,InputBytes=7,OutputBytes=7} }),
            "其他罗技厂商接口和短报文接口不会被误选为长报文通道");
        byte[] fly = new byte[32]; fly[0]=4; fly[1]=0xFF; fly[3]=85; fly[11]=3; fly[15]=0xEC;
        Check(FlydigiBatteryReader.Parse(fly) is { Level:"档位 2", Charging:false, Wireless:true }, "飞智 f4 专用档位不会套用线性百分比");
        fly[1]=0xFE; Check(FlydigiBatteryReader.Parse(fly)==null,"手柄输入流不会被误认成电池查询响应"); fly[1]=0xFF;
        fly[11]=5; Check(FlydigiBatteryReader.Parse(fly) is { Charging:true }, "飞智 f4 的充电图标特殊编码");
        fly[11]=0; Check(FlydigiBatteryReader.Parse(fly) is { Level:"", Charging:false }, "飞智唤醒初期的空读数不会显示零电量");
        fly[11]=0xFF; Check(FlydigiBatteryReader.Parse(fly)==null && FlydigiBatteryReader.Parse(fly[..16])==null, "飞智损坏和截断电量包被拒绝");
        fly[11]=2; fly[3]=84; Check(FlydigiBatteryReader.Parse(fly)==null, "其他飞智型号不能使用 f4 特殊电量映射");
        Check(!FlydigiBatteryReader.Supports(mouse), "飞智查询不会发送给其他品牌外设");
        // Actual K5 Ultra device-info response; only the documented payload and checksum are kept.
        byte[] mchose = Convert.FromHexString("4D010110000900003738384004000000001002003D0008E4A8");
        Check(MchoseBatteryReader.Parse(mchose) is { Percent:61,Charging:false }, "K5 Ultra 实机设备信息响应解出 61% 电量");
        var savedMchose = mchose.ToArray();
        void MchoseChecksum() { byte crc=0; foreach(byte value in mchose.AsSpan(2,mchose[3]+6))crc^=value;mchose[8+mchose[3]]=crc; }
        mchose[20]=0;mchose[19]=1;MchoseChecksum();
        Check(MchoseBatteryReader.Parse(mchose) is {Percent:0,Charging:true}, "K5 零电量与充电状态均保留，不套用估算");
        mchose[20]=101;MchoseChecksum();Check(MchoseBatteryReader.Parse(mchose)==null,"K5 非法百分比被拒绝");
        mchose=savedMchose.ToArray();mchose[20]^=1;
        Check(MchoseBatteryReader.Parse(mchose)==null && MchoseBatteryReader.Parse(savedMchose[..22])==null,"K5 校验错误和截断响应不能产生读数");
        mchose=savedMchose.ToArray();mchose[5]=1;MchoseChecksum();Check(MchoseBatteryReader.Parse(mchose)==null,"K5 其他命令回包不冒充设备信息");
        mchose=savedMchose.ToArray();mchose.AsSpan(8,16).Clear();MchoseChecksum();
        Check(MchoseBatteryReader.Parse(mchose)==null,"K5 接收器的空响应不会冒充零电量");
        var k5=mouse with { Caps=new HidNative.Caps { UsagePage=0xFF01,InputBytes=64,OutputBytes=64 } };
        Check(MchoseBatteryReader.Supports(k5) && !MchoseBatteryReader.Supports(k5 with {Caps=new HidNative.Caps {UsagePage=0xFF0B,InputBytes=61,OutputBytes=61}}),
            "K5 电池查询只选择官网协议的正确接口");
        Check(!MchoseBatteryReader.Supports(k5 with {Name="Other MCHOSE mouse"}),"同款通用接收器下的其他型号不会被误发送 K5 命令");
        var now = DateTimeOffset.UtcNow;
        var d = new PluginDeviceBattery("device-a", "测试鼠标", DeviceBatteryTransport.WirelessReceiver, DeviceBatteryKind.Mouse, 0, true, PhysicalId:"physical-a");
        var known = new DeviceBatterySample("a",d,now);
        var unknown = new DeviceBatterySample("b",d with { Percent=null },now);
        Check(DeviceBatteryCatalog.Merge([known,unknown],now) is { Count:1 } merged && merged[0].Device.Percent == 0, "同物理设备合并，保留真实零读数");
        Check(DeviceBatteryCatalog.Merge([known,known with { Device=d with { PhysicalId="physical-b" } }],now).Count == 2, "同型号的不同物理设备独立显示");
        Check(DeviceBatteryCatalog.Merge([known with { Device=d with {Connected=false} }],now).Count == 0, "断开设备从电池卡片移除");
        Check(DeviceBatteryCatalog.Merge([known with {UpdatedAt=now.AddMinutes(-4)}],now)[0].Device.Percent == null, "过期读数不继续显示为当前电量");
        Check(DeviceBatteryCatalog.Merge([known with {UpdatedAt=now.AddMinutes(-4),Expires=false}],now)[0].Device.Percent==0,
            "仅在变化时推送的旧版耳机接口读数保持有效直到断开");
        var wire = new alpha_island.Plugins.Isolation.RemoteEvent("device-batteries", DeviceBatteries:[d]);
        var round = JsonSerializer.Deserialize<alpha_island.Plugins.Isolation.RemoteEvent>(JsonSerializer.Serialize(wire));
        Check(round?.DeviceBatteries is [{Percent:0}], "跨进程完整电量快照保留零值");
    }
    private static void DeviceBatteryCardChecks(alpha_island.MainWindow window, Grid root, string testRoot)
    {
        DeviceBatteryHostChecks(window,testRoot);
        var pages = (IslandPageHost)ReadWindowMember(window,"_pages")!;
        Check(pages.IndexOf(IslandPageIds.DeviceBatteries) >= 0, "组件卡片列表存在独立电池入口");
        PrivateCall(window,"SetState",IslandState.Card,false,IslandPageIds.DeviceBatteries);
        var card = (DeviceBatteryCard)ReadWindowMember(window,"_deviceBatteryCard")!;
        var now=DateTimeOffset.UtcNow;
        var rows=Enumerable.Range(0,9).Select(i=>new DeviceBatterySample("fixture",new PluginDeviceBattery("test-"+i,
            i==0?"罗技 G502 LIGHTSPEED":i==1?"蓝牙耳机":i==2?"游戏手柄":"设备电池 "+i,
            i==1?DeviceBatteryTransport.Bluetooth:DeviceBatteryTransport.WirelessReceiver,
            i==2?DeviceBatteryKind.Gamepad:i==1?DeviceBatteryKind.Audio:DeviceBatteryKind.Mouse,
            i==2?null:i*10,true,i==1,Level:i==2?"中":""),now)).ToArray();
        card.Update(rows); root.UpdateLayout(); PumpAnimations(80);
        Check(card.IsVisible && card.ActualHeight > 100, "电池卡片实际进入卡片视口");
        Check(Descendants<TextBlock>(card).Any(t=>t.Text=="0%") && Descendants<TextBlock>(card).Any(t=>t.Text=="中"), "卡片分别呈现真实百分比和手柄档位");
        var scroll=Descendants<ScrollViewer>(card).Single(); scroll.ScrollToEnd(); root.UpdateLayout();
        Check(scroll.ScrollableHeight>0 && scroll.VerticalOffset>0, "多设备列表可滚动到底部");
        var preview = new Grid { Background=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32,32,32)) };
        var previewCard = new DeviceBatteryCard { Visibility=Visibility.Visible }; preview.Children.Add(previewCard);
        previewCard.Update(rows.Take(3).ToArray());
        SaveConversationPreview(preview,"bin/device-battery-previews/connected-devices.png",414,294);
        Check(Descendants<TextBlock>(previewCard).First(t=>t.Text=="电池").Foreground is System.Windows.Media.SolidColorBrush fg && fg.Color.R>200,
            "深色玻璃上的电池文字继承主题前景色");
        card.Update([]); root.UpdateLayout();
        Check(Descendants<TextBlock>(card).Any(t=>t.Text.Contains("尚未发现")), "没有连接设备时展示明确空状态");
        PrivateCall(window,"SetState",IslandState.Bar,false,null);
    }
    private static void DeviceBatteryHostChecks(alpha_island.MainWindow window, string testRoot)
    {
        string directory=Path.Combine(testRoot,"battery-probe"); Directory.CreateDirectory(directory);
        string source=Path.GetFullPath($"tools/PluginFixture/bin/{TestBuildConfiguration}/net8.0-windows");
        foreach(string file in Directory.GetFiles(source).Where(f=>f.EndsWith(".dll")||f.EndsWith(".deps.json"))) File.Copy(file,Path.Combine(directory,Path.GetFileName(file)));
        File.WriteAllText(Path.Combine(directory,"plugin.json"),JsonSerializer.Serialize(new {id="fixture.battery",name="Battery probe",version="1.0.0",apiVersion=4,assembly="AlphaIsland.PluginFixture.dll",entryType="AlphaIsland.PluginFixture.BatteryProbePlugin"}));
        var options=new IslandOptions(); options.Extras[alpha_island.Plugins.PluginHost.EnabledKey("fixture.battery")]=JsonSerializer.SerializeToElement(true);
        using var host=new alpha_island.Plugins.PluginHost(window.Dispatcher,options,[testRoot],_=>{},_=>{},_=>{}) { DataRoot=Path.Combine(testRoot,"battery-data") };
        try
        {
            host.Discover(); PumpUntilState(()=>host.DeviceBatteries.Count==2);
            Check(host.DeviceBatteries.Count==2 && host.DeviceBatteries.Any(s=>s.Device.Percent==0),"真实隔离插件通过新接口上报快照并过滤非法百分比");
            Check(host.InvokeAction("battery-empty"),"隔离插件的清空操作可调用"); PumpUntilState(()=>host.DeviceBatteries.Count==0);
            Check(host.DeviceBatteries.Count==0,"空快照立即撤销上一轮设备，不能残留旧电量");
            options.Extras[alpha_island.Plugins.PluginHost.EnabledKey("fixture.battery")]=JsonSerializer.SerializeToElement(false);host.Sync(options);
            AwaitPlugin(host.ShutdownCompletion.ContinueWith(_=>true));
            Check(host.DeviceBatteries.Count==0,"插件停用后移除其全部设备数据");
        }
        finally { host.Dispose(); AwaitPlugin(host.ShutdownCompletion.ContinueWith(_=>true)); }
    }
    private static async Task DeviceBatteryLiveProbe()
    {
        HidSession.Trace = text=>Console.WriteLine(text);
        var inventory=HidInventory.Enumerate();
        foreach(var iface in inventory.Where(i=>i.Vid==0x046d && i.Pid==0xc539 && i.Caps.UsagePage==0xff00))
        {
            try
            {
                using var session=new HidSession(iface);
                foreach(byte address in new byte[]{1,255})
                {
                    byte[] ping=new byte[iface.Caps.OutputBytes]; ping[0]=ping.Length>=20?(byte)0x11:(byte)0x10;ping[1]=address;ping[3]=0x1b;ping[6]=0x55;
                    Console.WriteLine($"ROUTE: usage={iface.Caps.Usage} slot={address}");
                    await session.Query(ping,r=>r.Length>=7 && r[1]==address,CancellationToken.None,500);
                }
            }
            catch(Exception e){Console.WriteLine("ROUTE ERROR: "+e.Message);}
        }
        var readings=await BatteryScanner.ReadAsync(CancellationToken.None);
        string probeDirectory = "bin/DeviceBattery" + typeof(alpha_island.MainWindow).Assembly.GetName().Version!.Build;
        Directory.CreateDirectory(probeDirectory);
        await File.WriteAllTextAsync(Path.Combine(probeDirectory,"live-devices.json"),JsonSerializer.Serialize(new {
            Interfaces=inventory.Select(i=>new {i.Name,i.Vid,i.Pid,i.Caps.UsagePage,i.Caps.Usage,i.Caps.InputBytes,i.Caps.OutputBytes,i.Caps.FeatureBytes}),
            Devices=readings.Select(d=>new {d.Name,d.Kind,d.Transport,d.Percent,d.Level,d.Estimated,d.Millivolts,d.Detail})},new JsonSerializerOptions{WriteIndented=true}));
        foreach(var d in readings) Console.WriteLine($"DEVICE: {d.Name}: {(d.Percent.HasValue?(d.Estimated?"约 ":"")+d.Percent+"%":d.Level.Length>0?d.Level:"未上报")} · {d.Detail}");
        Check(readings.All(d=>d.Percent is null or >=0 and <=100),"本机读取结果范围有效");
    }
}
