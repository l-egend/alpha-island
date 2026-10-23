using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Controls;
using alpha_island.Services;
using Color = System.Windows.Media.Color;
using Border = System.Windows.Controls.Border;

internal static partial class Program
{
    private static void KeyboardBatteryLiveChecks(alpha_island.MainWindow window)
    {
        var read = typeof(BluetoothService).GetMethod("ReadBatteryFromPnpAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        int? native = AwaitPlugin((Task<int?>)read.Invoke(null, [null, "QingLong 4.0", null])!);
        Console.WriteLine("Windows keyboard battery: " + (native?.ToString() ?? "unknown"));
        Check(native is >= 0 and <= 100, "Windows 电量路径确实能读取当前 QingLong 4.0 键盘");
        using var service = new BluetoothService(window.Dispatcher);
        AwaitPlugin(Task.Run(() => { service.Start(); return true; }));
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(16) && !service.BatterySnapshots.Any(s => s.Device.Name == "QingLong 4.0" && s.Device.Percent.HasValue)) PumpAnimations(100);
        foreach (var snapshot in service.BatterySnapshots) Console.WriteLine($"Inventory: {snapshot.Device.Name} | {snapshot.Device.Kind} | {snapshot.Device.Percent?.ToString() ?? "unknown"}");
        foreach (var candidate in AgentField<System.Collections.Concurrent.ConcurrentDictionary<string, Windows.Devices.Bluetooth.BluetoothLEDevice>>(service, "_bleDevices").Values.Where(d => d.Name.Contains("QingLong", StringComparison.OrdinalIgnoreCase)))
        {
            string address = (string)typeof(BluetoothService).GetMethod("ExtractMacAddress", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [candidate.DeviceId])!;
            Check(address == candidate.BluetoothAddress.ToString("X12"), "真实键盘的去重地址与 Windows 远端设备地址一致");
        }
        Check(service.BatterySnapshots.Any(s => s.Device.Name == "QingLong 4.0" && s.Device.Percent == native && s.Device.Kind == AlphaIsland.PluginSdk.DeviceBatteryKind.Keyboard),
            "正式蓝牙监控向电池卡片提供与 Windows 一致的键盘电量和键盘分类");
        var previous = ReadWindowMember(window, "_bluetooth");
        try
        {
            SetAgentField(window, "_bluetooth", service);
            PrivateCall(window, "SetState", alpha_island.Core.IslandState.Card, false, alpha_island.Core.IslandPageIds.DeviceBatteries);
            PrivateCall(window, "SyncDeviceBatteryCard"); window.UpdateLayout();
            var card = (DeviceBatteryCard)ReadWindowMember(window, "_deviceBatteryCard")!;
            Check(Descendants<System.Windows.Controls.TextBlock>(card).Any(t => t.Text == "QingLong 4.0")
                && Descendants<System.Windows.Controls.TextBlock>(card).Any(t => t.Text == native + "%"),
                "实际卡片汇总、分组与界面保留真实键盘电量");
            SaveReminderPreview(card, "bin/KeyboardSpectrum129/keyboard-live.png", 414, 300);
        }
        finally { SetAgentField(window, "_bluetooth", previous); }
    }

    private static void KeyboardSpectrumChecks(alpha_island.MainWindow window, Grid root)
    {
        var failures = new List<Exception>();
        void Case(Action action) { try { action(); } catch (Exception error) { failures.Add(error); Console.WriteLine("FAIL: " + error.Message); } }
        var extract = typeof(BluetoothService).GetMethod("ExtractMacAddress", BindingFlags.Static | BindingFlags.NonPublic)!;
        string Address(string id) => (string)extract.Invoke(null, [id])!;
        Case(() => Check(Address("BluetoothLE#BluetoothLE11:22:33:44:55:66-aa:bb:cc:dd:ee:01") == "AABBCCDDEE01",
            "BLE 双地址标识使用远端键盘地址，不能使用电脑适配器地址"));
        Case(() => Check(Address("Bluetooth#Bluetooth112233445566-aabbccddee02") == "AABBCCDDEE02",
            "经典蓝牙紧凑双地址标识同样使用远端设备地址"));
        Case(() => Check(new[] { "BluetoothLE#BluetoothLE11:22:33:44:55:66-aa:bb:cc:dd:ee:01",
                "BluetoothLE#BluetoothLE11:22:33:44:55:66-aa:bb:cc:dd:ee:02" }.Select(Address).Distinct().Count() == 2,
            "同一电脑上的键盘与耳机具有不同去重键，不会互相吞掉"));
        Case(() => Check(Address("BTHLE\\DEV_AABBCCDDEE01\\7&123&0&AABBCCDDEE01") == "AABBCCDDEE01"
            && Address("aa:bb:cc:dd:ee:01") == "AABBCCDDEE01" && Address("") == "", "PnP 远端地址及原有单地址输入保持兼容"));
        using (var service = new BluetoothService(window.Dispatcher))
        {
            const string keyboard = "BluetoothLE#BluetoothLE11:22:33:44:55:66-aa:bb:cc:dd:ee:01";
            const string earbuds = "BluetoothLE#BluetoothLE11:22:33:44:55:66-aa:bb:cc:dd:ee:02";
            _ = PrivateCall(service, "HandleConnectedAsync", keyboard, "回归键盘129", BluetoothDeviceCategory.Keyboard);
            _ = PrivateCall(service, "HandleConnectedAsync", earbuds, "回归耳机129", BluetoothDeviceCategory.Audio);
            PumpAnimations(40);
            Case(() => Check(service.BatterySnapshots.Select(s => s.Device.Name).Order().SequenceEqual(new[] { "回归耳机129", "回归键盘129" }.Order()),
                "真实连接处理链同时保留同适配器的键盘和耳机快照"));
            _ = PrivateCall(service, "HandleConnectedAsync", keyboard, "回归键盘129", BluetoothDeviceCategory.Keyboard);
            PumpAnimations(20);
            Case(() => Check(service.BatterySnapshots.Count == 2, "同一外设重复连接事件仍正确去重"));
            PrivateCall(service, "HandleDisconnected", keyboard, "回归键盘129", BluetoothDeviceCategory.Keyboard);
            PumpAnimations(20);
            Case(() => Check(service.BatterySnapshots is [{ Device.Name: "回归耳机129" }], "键盘断开仅移除自身，不会删除同适配器耳机"));
        }
        byte[] Render(Color album, Color background, string name)
        {
            var spectrum = new SpectrumVisualizer { Width = 28, Height = 22, BarCount = 5, BarGap = 2.5, MinBarHeight = 2.5,
                UniformColor = true, ColorLow = album, GlowRadius = 1.6, GlowOpacity = .26 };
            SetAgentField(spectrum, "_display", new float[] { .4f, .65f, .9f, .65f, .4f });
            var border = new Border { Width = 48, Height = 42, Padding = new Thickness(10), Background = new SolidColorBrush(background), Child = spectrum };
            border.Measure(new System.Windows.Size(48, 42)); border.Arrange(new Rect(0, 0, 48, 42)); border.UpdateLayout();
            var bitmap = new RenderTargetBitmap(192, 168, 384, 384, PixelFormats.Pbgra32); bitmap.Render(border);
            string directory = Path.GetFullPath("bin/SpectrumColor130"); Directory.CreateDirectory(directory);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var output = File.Create(Path.Combine(directory, name + ".png"))) encoder.Save(output);
            if (name == "album-color")
            {
                static IEnumerable<GeometryDrawing> Geometry(Drawing drawing) => drawing is GeometryDrawing geometry ? [geometry]
                    : drawing is DrawingGroup group ? group.Children.SelectMany(Geometry) : [];
                Check(Geometry(VisualTreeHelper.GetDrawing(spectrum)!).All(d => d.Pen == null),
                    "频谱柱和柔光均没有描边，恢复干净的实心轮廓");
            }
            var bytes = new byte[192 * 168 * 4]; bitmap.CopyPixels(bytes, 192 * 4, 0); return bytes;
        }
        static double Luma(byte r, byte g, byte b) => (.2126 * r + .7152 * g + .0722 * b) / 255;
        static IEnumerable<double> PixelLuma(byte[] bytes)
        { for (int y = 36; y < 132; y++) for (int x = 36; x < 156; x++) { int at = (y * 192 + x) * 4; yield return Luma(bytes[at + 2], bytes[at + 1], bytes[at]); } }
        var darkBackground = Color.FromRgb(28, 24, 42); var lightBackground = Color.FromRgb(241, 240, 246);
        var dark = Render(Color.FromRgb(31, 28, 47), darkBackground, "dark-on-dark");
        var light = Render(Color.FromRgb(242, 240, 248), lightBackground, "light-on-light");
        Case(() => Check(PixelLuma(dark).Max() - Luma(darkBackground.R, darkBackground.G, darkBackground.B) > .12,
            "深色专辑色适度提亮，在深色背景上仍可辨识"));
        Case(() => Check(Luma(lightBackground.R, lightBackground.G, lightBackground.B) - PixelLuma(light).Min() > .12,
            "近白专辑色适度压暗，在浅色背景上仍可辨识"));
        var color = Color.FromRgb(25, 168, 107); var colored = Render(color, darkBackground, "album-color"); int center = (84 * 192 + 96) * 4;
        Case(() => Check(Math.Abs(colored[center + 2] - (color.R * 238 + darkBackground.R * 17) / 255d) < 3
            && Math.Abs(colored[center + 1] - (color.G * 238 + darkBackground.G * 17) / 255d) < 3,
            "正常亮度的专辑色保持原有颜色"));
        if (failures.Count > 0) throw new AggregateException(failures);
    }
}
