using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AlphaIsland.PluginSdk;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Services;
using Orientation = System.Windows.Controls.Orientation;
using Brushes = System.Windows.Media.Brushes;

internal static partial class Program
{
    private static void DeviceBatteryUiChecks(alpha_island.MainWindow window, Grid root)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        bool reduce = MotionPolicy.ReduceMotion, powerSave = MotionPolicy.PowerSave; int tier = MotionPolicy.RenderTier;
        var layers = (IslandLayerHost)ReadWindowMember(window, "_layers")!;
        var state = ReadWindowMember(window, "_state");
        var pages = (IslandPageHost)ReadWindowMember(window, "_pages")!;
        string previousPage = pages.ActiveId;
        var gallery = new Border { Width = 470, Height = 185, Background = Brushes.Transparent };
        var samples = new StackPanel { Margin = new Thickness(16) }; gallery.Child = samples;
        var meters = new List<DeviceBatteryIndicator>();
        foreach (var (title, percent, charging) in new (string, int?, bool)[]
        { ("电量未知", null, false), ("低电量 · 12%", 12, false), ("50%", 50, false), ("83%", 83, false), ("充电中 · 100%", 100, true) })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var meter = new DeviceBatteryIndicator { Percent = percent, IsCharging = charging, Margin = new Thickness(0, 0, 12, 0) };
            row.Children.Add(meter); row.Children.Add(new TextBlock { Text = title, FontSize = 15, Foreground = Brushes.White });
            samples.Children.Add(row); meters.Add(meter);
        }
        root.Children.Add(gallery); root.UpdateLayout(); PumpAnimations(20);
        double Fraction(DeviceBatteryIndicator meter) => (double)typeof(DeviceBatteryIndicator).GetProperty("DisplayFraction", flags)!.GetValue(meter)!;
        int BoltPixels(DeviceBatteryIndicator meter)
        {
            var bitmap = new RenderTargetBitmap(30, 16, 96, 96, PixelFormats.Pbgra32); bitmap.Render(meter);
            var pixels = new byte[30 * 16 * 4]; bitmap.CopyPixels(pixels, 120, 0);
            return Enumerable.Range(0, 30 * 16).Count(i => pixels[i * 4] > 235 && pixels[i * 4 + 1] > 235
                && pixels[i * 4 + 2] > 235 && pixels[i * 4 + 3] > 200);
        }
        int ColoredPixels(DeviceBatteryIndicator meter, bool red = false)
        {
            meter.UpdateLayout();
            var bitmap = new RenderTargetBitmap(30, 16, 96, 96, PixelFormats.Pbgra32); bitmap.Render(meter);
            var pixels = new byte[30 * 16 * 4]; bitmap.CopyPixels(pixels, 120, 0);
            return Enumerable.Range(3, 21).Count(x =>
            {
                int index = (8 * 30 + x) * 4;
                return red ? pixels[index + 2] > pixels[index + 1] + 30
                    : pixels[index + 1] > pixels[index + 2] + 30
                        || pixels[index + 1] > pixels[index] + 30 && pixels[index + 2] > pixels[index] + 30;
            });
        }
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(true, false);
            Check(Fraction(meters[1]) == 0.12 && Fraction(meters[2]) == 0.5 && Fraction(meters[3]) == 0.83 && Fraction(meters[4]) == 1,
                "电池填充比例分别对应 12%、50%、83% 和 100%，不是固定图标");
            int low = ColoredPixels(meters[1], red: true), medium = ColoredPixels(meters[2]), high = ColoredPixels(meters[3]);
            Check(low > 0 && medium > low && high > medium, "真实像素显示低电量红色及随百分比增长的填充长度");
            SaveThemePreview(gallery, "docs/screens/device-batteries.png");
            var meter = meters[3];
            foreach (int percent in new[] { 0, 1, 20, 50, 100 })
            { meter.Percent = percent; Check(Math.Abs(Fraction(meter) - percent / 100d) < 0.00001, $"精确绘制 {percent}% 电量"); }
            meter.Percent = 255; meter.IsCharging = true;
            Check(meter.Percent == null && Fraction(meter) == 0 && BoltPixels(meter) == 0 && ColoredPixels(meter) == 0,
                $"无效或未知电量显示空框与占位，充电标记不能制造虚假电量（percent={meter.Percent}, fill={Fraction(meter)}, pixels={ColoredPixels(meter)}）");
            meter.Percent = 20; meter.IsCharging = false;
            MotionPolicy.Apply(false, false); meter.Percent = 80; PumpAnimations(90);
            double intermediate = Fraction(meter);
            Check(intermediate > 0.2 && intermediate < 0.8, "电量变化经过真实中间填充状态");
            meter.Percent = 12; PumpAnimations(90);
            Check(Fraction(meter) < intermediate && Fraction(meter) > 0.12, "快速连续更新从当前可见电量平滑转向最新值");
            PumpAnimations(420); Check(Math.Abs(Fraction(meter) - 0.12) < 0.00001, "过渡结束后准确停在最后的百分比");
            meter.IsCharging = true; PumpAnimations(80);
            Check(BoltPixels(meter) > 0 && !meter.HasAnimatedProperties, "设备报告充电时显示常亮闪电，无循环动画");
            meter.Visibility = Visibility.Collapsed;
            Check(!meter.HasAnimatedProperties && meter.IsCharging, "电量控件隐藏时不保留动画且仍保留设备充电状态");
            meter.Visibility = Visibility.Visible; PumpAnimations(20);
            Check(BoltPixels(meter) > 0 && !meter.HasAnimatedProperties, "重新显示时按当前充电状态恢复常亮标记");
            MotionPolicy.Apply(true, false);
            Check(!meter.HasAnimatedProperties && BoltPixels(meter) > 0 && Fraction(meter) == 0.12, "减弱动效保留常亮充电标记及准确电量");
            MotionPolicy.Apply(false, true); meter.Percent = 65;
            Check(!meter.HasAnimatedProperties && BoltPixels(meter) > 0 && Fraction(meter) == 0.65, "节能模式采用静态准确电量并保留充电标记");
            MotionPolicy.Apply(false, false); MotionPolicy.OverrideRenderTierForDebug(0);
            Check(!meter.HasAnimatedProperties && BoltPixels(meter) > 0, "软件渲染采用静态充电标记");
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(true, false);

            var device = new BluetoothDeviceInfo("battery-probe", "测试蓝牙耳机", BluetoothDeviceCategory.Audio, 83, true);
            PrivateCall(window, "ShowBluetoothNotify", device, true); root.UpdateLayout();
            Check(window.BtBatteryIcon.Percent == 83 && window.BtBatteryText.Text == "83%", "蓝牙提示的图形与数字使用同一个实际电量");
            PrivateCall(window, "OnBluetoothBatteryUpdated", null, device with { BatteryPercent = 12 });
            Check(window.BtBatteryIcon.Percent == 12 && window.BtBatteryText.Text == "12%", "当前设备电量回报同步更新比例及数字");
            PrivateCall(window, "OnBluetoothBatteryUpdated", null, device with { Id = "another-device", BatteryPercent = 99 });
            Check(window.BtBatteryIcon.Percent == 12, "其他设备的异步回报不能覆盖当前显示");
            PrivateCall(window, "OnBluetoothBatteryUpdated", null, device with { BatteryPercent = null });
            Check(window.BtBatteryIcon.Percent == null && window.BtBatteryText.Text == "—", "电量不可读时清空旧值，避免显示过期电量");
            PrivateCall(window, "ShowBluetoothNotify", device with { IsConnected = false }, false);
            PrivateCall(window, "OnBluetoothBatteryUpdated", null, device);
            Check(window.BtBatteryIcon.Percent == null && window.BtBatteryIcon.Visibility == Visibility.Collapsed,
                "断联隐藏并清空电量，迟到的旧回报不能恢复它");
            PrivateCall(window, "ShowBluetoothNotify", device with { Id = "new-device", BatteryPercent = null }, true);
            Check(window.BtBatteryIcon.Percent == null && window.BtBatteryText.Text == "—", "更换设备不继承上一次电量");
        }
        finally
        {
            root.Children.Remove(gallery); layers.Hide("bluetooth"); PrivateCall(window, "SyncNotificationLayers");
            PrivateCall(window, "SetState", state, true, previousPage);
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduce, powerSave);
        }
    }
}
