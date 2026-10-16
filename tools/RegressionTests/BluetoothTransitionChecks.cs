using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;
using Color = System.Windows.Media.Color;

internal static partial class Program
{
    private static void BluetoothTransitionChecks(alpha_island.MainWindow window, Grid root)
    {
        var progress = (DependencyProperty)typeof(SemanticIcon).GetField("ProgressProperty", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var output = Path.GetFullPath("bin/bluetooth-transition-previews"); Directory.CreateDirectory(output);
        var frames = new List<BitmapSource>();
        bool reduced = MotionPolicy.ReduceMotion, saved = MotionPolicy.PowerSave;
        int tier = MotionPolicy.RenderTier;
        var device = NewSemanticProbe(IconShape.Bluetooth, false);
        device.Source = BluetoothDeviceArtwork.For("AirPods Pro");
        var radio = NewSemanticProbe(IconShape.Bluetooth, true);
        foreach (var icon in new[] { device, radio }) { icon.Measure(new(64, 64)); icon.Arrange(new(0, 0, 64, 64)); }
        byte[] Frame(SemanticIcon icon, double value, string name)
        {
            // Retain the production source/target state but hold its real WPF progress deterministically.
            icon.BeginAnimation(progress, null); icon.SetValue(progress, value); icon.UpdateLayout();
            var bitmap = SemanticPreviewFrame(icon, 64, 64);
            frames.Add(bitmap);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(output, name + ".png"))) png.Save(file);
            var pixels = new byte[64 * 64 * 4]; bitmap.CopyPixels(pixels, 64 * 4, 0); return pixels;
        }
        void Start(SemanticIcon icon, bool active)
        {
            icon.Active = active;
            icon.BeginTransition(active ? ActionIconAnimation.Connect : ActionIconAnimation.Disconnect,
                new DoubleAnimation(0, 1, TimeSpan.FromSeconds(60)));
        }
        bool Blue(byte[] pixels) => HasColour(pixels, c => c.B > 150 && c.G > 150 && c.R < 100);
        bool Red(byte[] pixels) => HasColour(pixels, c => c.R > 180 && c.G < 130 && c.B < 150);
        int AddedInk(byte[] baseline, byte[] pixels, int x0 = 0, int y0 = 0, int x1 = 64, int y1 = 64)
        {
            int count = 0;
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
            {
                int alpha = (y * 64 + x) * 4 + 3;
                if (baseline[alpha] < 30 && pixels[alpha] > 160) count++;
            }
            return count;
        }
        double BlueCenter(byte[] pixels)
        {
            int count = 0, total = 0;
            for (int i = 0; i < pixels.Length / 4; i++)
                if (pixels[i * 4 + 3] > 180 && pixels[i * 4] > 150 && pixels[i * 4 + 1] > 150 && pixels[i * 4 + 2] < 100)
                { count++; total += i % 64; }
            return count == 0 ? double.NaN : (double)total / count;
        }
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            device.EndTransition(); Start(device, true);
            var connecting = new[] { Frame(device, 0, "device-start"), Frame(device, .3, "device-moving"),
                Frame(device, .65, "device-badge"), Frame(device, 1, "device-connected") };
            Check(connecting.All(pixels => Blue(pixels) && !Red(pixels)), "音频连接的起点、中段和右下角徽标始终为蓝色，无红色断开图标");
            Check(BlueCenter(connecting[3]) > BlueCenter(connecting[0]) + 15
                && SemanticPixelDifference(connecting[0], connecting[3]) > 50, "蓝色蓝牙符号真实移向右下角并让出设备轮廓");
            device.EndTransition(); Start(device, false);
            var disconnecting = Frame(device, .5, "device-disconnecting");
            var disconnected = Frame(device, 1, "device-disconnected");
            Check(Blue(disconnecting) && !Red(disconnecting) && Blue(disconnected) && !Red(disconnected),
                "仅断开耳机时蓝牙仍为开启的蓝色，反向过渡不借用关闭蓝牙图标");
            device.EndTransition();

            radio.EndTransition(); Start(radio, false);
            var closing = new[] { Frame(radio, 0, "radio-start"), Frame(radio, .25, "radio-top"),
                Frame(radio, .65, "radio-middle"), Frame(radio, 1, "radio-off") };
            Check(Blue(closing[0]) && !Red(closing[0]) && Red(closing[3]) && !Blue(closing[3]),
                "关闭蓝牙从原蓝色符号过渡到红色符号，最终状态颜色明确区分");
            Check(AddedInk(closing[0], closing[1], 6, 7, 15, 16) > 0
                && AddedInk(closing[0], closing[1], 49, 48, 61, 61) == 0
                && AddedInk(closing[0], closing[3], 49, 48, 61, 61) > 0,
                "关闭斜线先出现在上端，右下端仅在后段形成，方向为从上到下");
            int top = AddedInk(closing[0], closing[1]), middle = AddedInk(closing[0], closing[2]), end = AddedInk(closing[0], closing[3]);
            Check(top > 0 && middle > top + 12 && end > middle + 15,
                "关闭斜线的可见长度持续增长，而非整条线同时淡入");
            radio.EndTransition(); Start(radio, true);
            var enabled = Frame(radio, 1, "radio-on"); radio.EndTransition();
            Check(Blue(enabled) && !Red(enabled) && AddedInk(enabled, closing[3], 49, 48, 61, 61) > 0,
                "再次开启蓝牙会擦除关闭斜线并回到蓝色状态");
            Check(!SemanticProgressHasClock(device) && !SemanticProgressHasClock(radio), "图标停止后不保留动画时钟");

            MotionPolicy.Apply(true, false);
            var info = new BluetoothDeviceInfo("transition-probe", "AirPods Pro", BluetoothDeviceCategory.Audio, 83, true);
            PrivateCall(window, "ShowBluetoothNotify", info, true); root.UpdateLayout();
            Check(window.BtDeviceIcon.Shape == IconShape.Bluetooth && window.BtDeviceIcon.Active
                && ReferenceEquals(window.BtDeviceIcon.Source, device.Source), "真实音频通知沿用相同的设备过渡控件");
            PrivateCall(window, "ShowBluetoothNotify", info with { IsConnected = false }, false); root.UpdateLayout();
            var actualDisconnected = SemanticIconPixels(window.BtDeviceIcon);
            Check(!window.BtDeviceIcon.Active && Blue(actualDisconnected) && !Red(actualDisconnected),
                "减弱动效下真实设备断联通知也保持蓝色已开启符号");

            var sheet = new Canvas { Width = 640, Height = 292, Background = new SolidColorBrush(Color.FromRgb(13, 20, 33)) };
            AddSemanticPreviewText(sheet, "音频设备连接：蓝色符号移到右下角", 18, 12, 604, 16, true);
            AddSemanticPreviewText(sheet, "关闭蓝牙：蓝色转红色，斜线从上往下画出", 18, 148, 604, 16, true);
            string[] labels = { "起点", "过渡前段", "过渡后段", "完成" };
            for (int row = 0; row < 2; row++) for (int column = 0; column < 4; column++)
            {
                var image = new System.Windows.Controls.Image { Source = frames[(row == 0 ? 0 : 6) + column], Width = 64, Height = 64 };
                Canvas.SetLeft(image, 66 + 150 * column); Canvas.SetTop(image, 44 + 136 * row); sheet.Children.Add(image);
                AddSemanticPreviewText(sheet, labels[column], 24 + 150 * column, 114 + 136 * row, 148, 12, false, true, TextAlignment.Center);
            }
            sheet.Measure(new(640, 292)); sheet.Arrange(new(0, 0, 640, 292)); sheet.UpdateLayout();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(SemanticPreviewFrame(sheet, 640, 292)));
            using var stream = File.Create(Path.Combine(output, "sequence.png")); encoder.Save(stream);
        }
        finally
        {
            device.EndTransition(); radio.EndTransition();
            AgentField<IslandLayerHost>(window, "_layers").Hide("bluetooth"); PrivateCall(window, "SyncNotificationLayers");
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduced, saved);
        }
    }
}
