using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AlphaIsland.PluginSdk;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using Color = System.Windows.Media.Color;

internal static partial class Program
{
    private static DeviceBatterySample[] HeadsetSamples(DateTimeOffset now)
    {
        const string name = "OPPO Enco Clip2";
        var native = new PluginDeviceBattery("windows-headset", name, DeviceBatteryTransport.Bluetooth,
            DeviceBatteryKind.Audio, 90, true, PhysicalId: "headset-a");
        DeviceBatterySample Part(string part, string title, int percent, bool charging = false) => new("oppo.pods",
            native with { Id = name + "/" + part, Name = name + " · " + title, Percent = percent,
                Charging = charging, PhysicalId = "" }, now, Expires: false);
        return [new("bluetooth", native, now), Part("left", "左耳", 100), Part("right", "右耳", 80, true), Part("case", "耳机盒", 85)];
    }

    private static void DeviceBatteryEnhancementChecks(alpha_island.MainWindow window, Grid root)
    {
        DeviceBatteryPriorityAndLayoutChecks(root);
        DeviceBatteryReminderChecks(window);
        var now = DateTimeOffset.UtcNow;
        var card = new DeviceBatteryCard { Visibility = Visibility.Visible };
        var settings = new alpha_island.Settings.SettingsWindow(false);
        try
        {
            Check(settings.FindName(SettingsRegistry.Find("BluetoothLowBatteryEnabled")!.ControlName) is System.Windows.Controls.CheckBox
                && settings.FindName(SettingsRegistry.Find("DeviceBatteryFullEnabled")!.ControlName) is System.Windows.Controls.CheckBox,
                "通知设置内实际呈现低电量和充满提醒开关");
        }
        finally { settings.ForceClose=true; settings.Close(); }
        var container = new Border { Width = 414, Height = 294, Child = card };
        root.Children.Add(container);
        try
        {
            card.Update(HeadsetSamples(now)); root.UpdateLayout();
            Check(AgentField<StackPanel>(card, "_rows").Children.Count == 1,
                "Windows 耳机总电量和插件左右耳、耳机盒合为一个设备行");
            var texts = Descendants<TextBlock>(card).Select(text => text.Text).ToArray();
            Check(texts.Any(text => text.Contains("左耳")) && texts.Any(text => text.Contains("右耳")) && texts.Any(text => text.Contains("耳机盒")),
                "合并后仍保留左右耳和耳机盒的独立电量");
            var cardMeters = Descendants<DeviceBatteryIndicator>(card).ToArray();
            Check(cardMeters.Length > 0 && cardMeters.All(meter => meter.ActualWidth == 30 && meter.ActualHeight == 16),
                "主电量和充电组件的电池图标均恢复为原来的 30×16 大小");
            var samples = HeadsetSamples(now);
            card.Update(samples.Append(samples[0] with { Device = samples[0].Device with { Id="windows-headset-2", PhysicalId="headset-b" } }).ToArray());
            Check(AgentField<StackPanel>(card,"_rows").Children.Count >= 2, "同型号不同物理耳机不会仅凭名称被合成一个设备");
            var logi = new AlphaIsland.Plugins.DeviceBattery.HidInterface("test", "receiver", "USB Receiver", 0x046D, 0xC539, new());
            var receiver = (PluginDeviceBattery)typeof(AlphaIsland.Plugins.DeviceBattery.BatteryScanner)
                .GetMethod("Receiver", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, [logi, "外设休眠"])!;
            Check(receiver.Kind == DeviceBatteryKind.Mouse, "G502 LIGHTSPEED 接收器在休眠未上报时也使用鼠标图标");
            bool reduce = MotionPolicy.ReduceMotion, powerSave = MotionPolicy.PowerSave; int tier = MotionPolicy.RenderTier;
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            var meter = new DeviceBatteryIndicator { Percent=80, IsCharging=true };
            var meterHost = new Border { Width=30, Height=16, Child=meter };
            root.Children.Add(meterHost); root.UpdateLayout(); PumpAnimations(40);
            try
            {
                byte[] Render()
                {
                    var bitmap = new RenderTargetBitmap(30,16,96,96,PixelFormats.Pbgra32); bitmap.Render(meter);
                    var pixels = new byte[30*16*4]; bitmap.CopyPixels(pixels,30*4,0); return pixels;
                }
                var pixels = Render();
                var whiteRows = Enumerable.Range(0,16).Where(y => Enumerable.Range(0,30).Any(x =>
                { int i=(y*30+x)*4; return pixels[i]>235 && pixels[i+1]>235 && pixels[i+2]>235 && pixels[i+3]>200; })).ToArray();
                Check(whiteRows.Length>0 && whiteRows[0]>0 && whiteRows[^1]<15, "常亮充电闪电正常显示在原尺寸的电池轮廓内");
                PumpAnimations(900);
                Check(pixels.SequenceEqual(Render()) && !meter.HasAnimatedProperties, "启用完整动效时充电图标仍保持相同像素，无闪烁或呼吸动画");
            }
            finally
            {
                root.Children.Remove(meterHost);
                MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduce, powerSave);
            }
            var previewCard = new DeviceBatteryCard { Visibility=Visibility.Visible };
            previewCard.Update(HeadsetSamples(now).Append(new("fixture",receiver,now)).Append(new("fixture",
                new("k5","MCHOSE K5 Ultra",DeviceBatteryTransport.WirelessReceiver,DeviceBatteryKind.Mouse,78,true),now)).ToArray());
            SaveConversationPreview(new Border { Background=new SolidColorBrush(Color.FromRgb(24,26,31)),Child=previewCard },
                "bin/device-battery-previews125/combined-devices.png",414,294);
        }
        finally { root.Children.Remove(container); }
    }

    private static void DeviceBatteryReminderChecks(alpha_island.MainWindow window)
    {
        var now = DateTimeOffset.UtcNow;
        var seed = new DeviceBatterySample("fixture", new PluginDeviceBattery("mouse", "测试鼠标", DeviceBatteryTransport.WirelessReceiver,
            DeviceBatteryKind.Mouse, 100, true, PhysicalId:"mouse"), now);
        int tick = 0;
        IReadOnlyList<DeviceBatteryGroup> Values(int? percent, bool charging = false, bool estimated = false) => DeviceBatteryCatalog.Group(
            [seed with { Device=seed.Device with {Percent=percent,Charging=charging,Estimated=estimated}, UpdatedAt=now.AddSeconds(tick++) }],now);
        var policy = new DeviceBatteryReminderPolicy();
        Check(policy.Observe(Values(100),now).Count==0 && policy.Observe(Values(100,true),now).Count==0,
            "满电设备接入或首次上报正在充电的 100% 都不触发满电提示");
        Check(policy.Observe(Values(99,true),now).Count==0 && policy.Observe(Values(100,false),now) is [{Full:true}],
            "已观察到 99% 充电后达到 100%，即使充电标志自动关闭仍只提示一次");
        Check(policy.Observe(Values(100),now).Count==0, "连续满电读数不重复提醒");
        policy.Observe(Values(99,true),now);
        Check(policy.Observe(Values(100,true),now).Count==0,"充满后 99% 与 100% 的涓流浮动不会反复触发满电提示");
        policy.Observe([],now); Check(policy.Observe(Values(100,true),now).Count==0,"断开后以满电状态重连不会补发满电提示");
        policy.Observe(Values(90,true),now); policy.Observe(Values(95),now);
        Check(policy.Observe(Values(100),now).Count==0,"未满电就停止充电后出现的 100% 不冒充完成充电");
        policy.Observe(Values(90,true),now); policy.Observe(Values(null,true),now);
        Check(policy.Observe(Values(100),now).Count==0,"未知读数中断充电观察，不用不完整历史推测满电");
        policy.Observe(Values(90,true,true),now);
        Check(policy.Observe(Values(100,true,true),now).Count==0,"电压估算的 100% 不作为精确满电提示");
        policy = new();
        Check(policy.Observe(Values(20),now) is [{Full:false}],"首次达到 20% 提示低电量");
        Check(policy.Observe(Values(19),now).Count==0 && policy.Observe(Values(10),now) is [{Full:false}],"相同档位不重报，达到 10% 再提醒一次");
        Check(policy.Observe(Values(0),now).Count==0 && policy.Observe(Values(5,true),now) is [{Charging:true}],
            "连续低电量不重报，开始充电时只显示充电提示");
        policy.Observe([],now); Check(policy.Observe(Values(10),now).Count==0,"短暂断开重连不会重置低电量去重");
        policy.Observe(Values(30,true),now); Check(policy.Observe(Values(20),now) is [{Full:false}],"充到 25% 以上后新的放电周期可再次提醒");
        var audio = HeadsetSamples(now);
        var low = audio.Select(s=>s with {Device=s.Device with {Percent=20,Charging=false}}).ToArray();
        policy = new();
        Check(policy.Observe(DeviceBatteryCatalog.Group([low[0]],now),now).Count==1,"Windows 单独上报的低电量可提醒");
        Check(policy.Observe(DeviceBatteryCatalog.Group(low,now),now).Count==0,"随后到达的耳机插件读数不会重复发送同一设备的低电量提醒");
        policy = new(); policy.Observe(DeviceBatteryCatalog.Group(low.Skip(1),now),now);
        Check(policy.Observe(DeviceBatteryCatalog.Group(low,now),now).Count==0,"插件先到、Windows 后到时同样保留低电量去重状态");
        var exact = audio.Select(s=>s.Source=="bluetooth"?s:s with {Device=s.Device with {PhysicalId="headset-a"}}).ToArray();
        Check(DeviceBatteryCatalog.Group(exact,now) is [{ }] exactGroup && exactGroup[0].Components.Count==3,
            "有明确物理标识时合并三个电池组件，不把左右耳先去重丢失");
        var genericPlugin = audio[0] with {Source="custom.audio",Device=audio[0].Device with {Id="headset-summary",PhysicalId="",Charging=true,Percent=85}};
        Check(DeviceBatteryCatalog.Group([audio[0],genericPlugin],now) is [{ }] generic && generic[0].Display.Device is {Percent:85,Charging:true},
            "只提供总电量的第三方耳机插件也与唯一同名 Windows 设备合并");
        policy = new(); var chargeParts = audio.Select(s=>s with {Device=s.Device with {Percent=99,Charging=true}}).ToArray();
        policy.Observe(DeviceBatteryCatalog.Group(chargeParts,now),now);
        var chargedParts = chargeParts.Select(s=>s with {Device=s.Device with {Percent=100,Charging=false}}).ToArray();
        Check(policy.Observe(DeviceBatteryCatalog.Group(chargedParts,now),now) is [{Full:true}] full && full[0].Detail.Contains("耳机盒"),
            "同一轮左右耳和耳机盒充满合成一条提醒，保留组件名称");
        policy = new(); policy.Observe(DeviceBatteryCatalog.Group([seed with {Device=seed.Device with {Percent=99,Charging=true}}],now),now);
        Check(policy.Observe(DeviceBatteryCatalog.Group([seed],now.AddMinutes(4)),now.AddMinutes(4)).Count==0,"过期的 100% 读数不触发满电提醒");
        var options = AgentField<IslandOptions>(window,"_options");
        var inbox = AgentField<DesktopNotificationInbox>(window,"_desktopInbox");
        SetAgentField(window,"_sessionLocked",true);
        try
        {
            PrivateCall(window,"ObserveDeviceBatteryGroups",Values(99,true),now);
            PrivateCall(window,"ObserveDeviceBatteryGroups",Values(100),now);
            Check(inbox.History.Count(n=>n.Kind==DesktopEventKind.DeviceBatteryFull)==1 && inbox.PendingCount==0,
                "充满提醒进入统一通知中心，锁屏时不强行弹出");
            SettingsRegistry.SetExtra(options,"DeviceBatteryFullEnabled",false);
            PrivateCall(window,"ObserveDeviceBatteryGroups",Values(90,true),now);
            PrivateCall(window,"ObserveDeviceBatteryGroups",Values(100),now);
            Check(inbox.History.Count(n=>n.Kind==DesktopEventKind.DeviceBatteryFull)==1,"满电提醒开关关闭后不再生成通知");
            PrivateCall(window,"ObserveDeviceBatteryGroups",Values(20),now);
            PrivateCall(window,"ObserveDeviceBatteryGroups",Values(19),now);
            Check(inbox.History.Count(n=>n.Kind==DesktopEventKind.BluetoothLowBattery)==1,"实际通知入口对所有设备统一去重低电量提醒");
        }
        finally { SetAgentField(window,"_sessionLocked",false); }
    }
}
