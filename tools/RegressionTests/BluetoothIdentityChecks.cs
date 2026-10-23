using System.Reflection;
using alpha_island.Services;
using alpha_island.Core;
using AlphaIsland.PluginSdk;
using System.Windows.Controls;

internal static partial class Program
{
    private static void BluetoothIdentityChecks()
    {
        var extract = typeof(BluetoothService).GetMethod("ExtractMacAddress", BindingFlags.Static | BindingFlags.NonPublic)!;
        string Read(string value) => (string)extract.Invoke(null, [value])!;
        const string peer = "Bluetooth#Bluetooth112233445566-AABBCCDDEE06";
        const string endpoint = "{1}.BTHENUM\\{0000110b-0000-1000-8000-00805f9b34fb}_VID&000102B0_PID&0000\\7&3B0487D7&0&AABBCCDDEE06_C00000000";
        var now = DateTimeOffset.UtcNow;
        DeviceBatterySample Native(string id) => new("bluetooth", new("mac:" + Read(id), "OPPO Enco Clip2",
            DeviceBatteryTransport.Bluetooth, DeviceBatteryKind.Audio, 90, true, PhysicalId: "mac:" + Read(id)), now);
        var groups = DeviceBatteryCatalog.Group(new[] { Native(peer), Native(endpoint) }.Concat(HeadsetSamples(now).Skip(1)), now);
        Check(groups.Count == 1 && groups[0].Components.Count == 3,
            "Windows 蓝牙、音频端点和耳机插件同时上报后，实际电池卡片仍只有一副耳机并保留分耳读数");
        Check(Read(endpoint) == "AABBCCDDEE06", "蓝牙音频 PnP 属性取远端地址，不取服务 UUID 的十六进制尾段");
        Check(Read("{00112233-4455-6677-8899-AABBCCDDEEFF}") == "",
            "音频端点的容器 GUID 尾段不会被误识别为第二个蓝牙设备地址");
        Check(Read("SWD\\MMDEVAPI\\{0.0.0.00000000}.{00112233-4455-6677-8899-AABBCCDDEEFF}") == "",
            "CoreAudio 端点 ID 不会制造虚假的 mac 电量条目");
        Check(Read("Bluetooth#Bluetooth11:22:33:44:55:66-aa:bb:cc:dd:ee:01") == "AABBCCDDEE01",
            "经典蓝牙仍取远端地址而不是电脑适配器地址");
        Check(Read("BluetoothLE#BluetoothLE112233445566-AABBCCDDEE02") == "AABBCCDDEE02",
            "BLE 设备标识中的真实远端地址保持可读");
        Check(Read("BTHENUM\\DEV_AABBCCDDEE03\\7&instance") == "AABBCCDDEE03"
            && Read("aa:bb:cc:dd:ee:04") == "AABBCCDDEE04" && Read("AABBCCDDEE05") == "AABBCCDDEE05",
            "PnP 地址、明确 MAC 和纯地址值仍可关联同一设备");
        Check(Read("audio property 00805F9B34FB") == "" && Read("{0000110b-0000-1000-8000-00805f9b34fb}") == "",
            "任意属性中的十六进制数字和服务 UUID 不再创建设备身份");
    }
    private static void EarbudBatteryIdentityLiveChecks(alpha_island.MainWindow window, Grid root)
    {
        using var service = new BluetoothService(window.Dispatcher);
        service.Start();
        var until = DateTime.UtcNow.AddSeconds(9);
        while (DateTime.UtcNow < until) PumpAnimations(150);
        var actual = service.DeviceBatterySamples.Where(sample => sample.Device.Connected
            && sample.Device.Name.Equals("OPPO Enco Clip2", StringComparison.OrdinalIgnoreCase)).ToArray();
        Check(actual.Length == 1, "实机 Windows 蓝牙与 CoreAudio 同时枚举后，OPPO Enco Clip2 仅产生一个有效设备身份");
        var groups = DeviceBatteryCatalog.Group(actual.Concat(HeadsetSamples(DateTimeOffset.UtcNow).Skip(1)), DateTimeOffset.UtcNow);
        var card = new alpha_island.Controls.DeviceBatteryCard(); card.UpdateGroups(groups);
        Check(groups.Count == 1 && AgentField<StackPanel>(card, "_rows").Children.Count == 1,
            "实机 Windows 身份与耳机插件分耳快照进入真实卡片后只显示一行");
        Console.WriteLine("Live native headset records=" + actual.Length + "; merged device rows=" + groups.Count + "; no device addresses logged.");
    }
}
