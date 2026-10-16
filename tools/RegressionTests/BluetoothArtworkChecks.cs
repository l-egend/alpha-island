using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;
using Point = System.Windows.Point;

internal static partial class Program
{
    private static void BluetoothArtworkUiChecks(alpha_island.MainWindow window, Grid root)
    {
        var samples = new (string? Name, BluetoothAudioForm Form)[]
        {
            ("AirPods Max", BluetoothAudioForm.Headphones), ("WH-1000XM5", BluetoothAudioForm.Headphones),
            ("HUAWEI FreeClip", BluetoothAudioForm.EarClip), ("OPPO Enco Clip", BluetoothAudioForm.EarClip),
            ("FreeClip Wireless Headphones", BluetoothAudioForm.EarClip), ("Ear Clip Earbuds", BluetoothAudioForm.EarClip),
            ("WF-1000XM5 headphones", BluetoothAudioForm.InEar),
            ("我的耳夹式耳机", BluetoothAudioForm.EarClip), ("AirPods Pro", BluetoothAudioForm.InEar),
            ("HUAWEI FreeBuds Pro", BluetoothAudioForm.InEar), ("Galaxy Buds3 Pro", BluetoothAudioForm.InEar),
            ("WF-1000XM5", BluetoothAudioForm.InEar), ("入耳式 TWS", BluetoothAudioForm.InEar),
            ("JBL Flip 6", BluetoothAudioForm.Speaker), ("JBL Charge5", BluetoothAudioForm.Speaker),
            ("桌面音箱", BluetoothAudioForm.Speaker), ("Shokz OpenFit 2", BluetoothAudioForm.OpenEar),
            ("soundcore AeroFit", BluetoothAudioForm.OpenEar), ("OpenRun Pro", BluetoothAudioForm.OpenEar),
            ("骨传导耳机", BluetoothAudioForm.OpenEar), ("Open-Ear Headphones", BluetoothAudioForm.OpenEar),
            ("Bone Conduction Headphones", BluetoothAudioForm.OpenEar), ("MY HEADSET", BluetoothAudioForm.Generic),
            ("JBL", BluetoothAudioForm.Generic), ("JBL Clip5", BluetoothAudioForm.Generic),
            ("Bose QuietComfort", BluetoothAudioForm.Generic), ("clip test", BluetoothAudioForm.Generic),
            (null, BluetoothAudioForm.Generic), ("", BluetoothAudioForm.Generic)
        };
        foreach (var (name, form) in samples)
            Check(BluetoothDeviceArtwork.Resolve(name) == form, $"蓝牙音频名称 {name ?? "空名称"} 匹配 {form}，不单凭品牌猜外形");

        var distinct = new (string Name, BluetoothAudioForm Form)[]
        {
            ("未知音频", BluetoothAudioForm.Generic), ("AirPods Pro", BluetoothAudioForm.InEar),
            ("FreeClip", BluetoothAudioForm.EarClip), ("AirPods Max", BluetoothAudioForm.Headphones),
            ("JBL Flip6", BluetoothAudioForm.Speaker), ("OpenFit", BluetoothAudioForm.OpenEar)
        };
        var artwork = distinct.Select(sample => BluetoothDeviceArtwork.For(sample.Name)).ToArray();
        Check(artwork.Distinct().Count() == 6, "六种蓝牙音频外形拥有独立二维素材，资源缺失未退化为通用图标");
        foreach (var (name, form) in distinct)
        {
            var source = BluetoothDeviceArtwork.For(name);
            Check(source is DrawingImage { IsFrozen: true } drawing && drawing.Drawing.IsFrozen
                && source.Width == 32 && source.Height == 32,
                $"{form} 是 32px 冻结矢量图片，没有逐通知位图解码");
            Check(ReferenceEquals(source, BluetoothDeviceArtwork.For(name)), $"{form} 重复请求复用同一图片");
            var preview = new DrawingVisual();
            using (var drawingContext = preview.RenderOpen()) drawingContext.DrawImage(source, new Rect(0, 0, 32, 32));
            var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32); bitmap.Render(preview);
            var pixels = new byte[32 * 32 * 4]; bitmap.CopyPixels(pixels, 32 * 4, 0);
            Check(Enumerable.Range(0, 32 * 32).Count(index => pixels[index * 4 + 3] > 80) >= 30,
                $"{form} 真实 32px 渲染有可见轮廓，SVG path 语法能够解析");
        }
        Check(ReferenceEquals(BluetoothDeviceArtwork.For("AirPods Pro"), BluetoothDeviceArtwork.For("Galaxy Buds3")),
            "同一外形的不同设备也复用冻结图片，缓存仅按六种外形增长");

        bool reduce = MotionPolicy.ReduceMotion, powerSave = MotionPolicy.PowerSave;
        int tier = MotionPolicy.RenderTier;
        var state = AgentField<IslandState>(window, "_state");
        var pages = AgentField<IslandPageHost>(window, "_pages");
        string previousPage = pages.ActiveId;
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(true, false);
            foreach (var (name, form) in distinct)
            {
                var device = new BluetoothDeviceInfo("artwork-probe-" + form, name, BluetoothDeviceCategory.Audio, 83, true);
                PrivateCall(window, "ShowBluetoothNotify", device, true); root.UpdateLayout();
                Check(window.BtDeviceArt.Visibility == Visibility.Collapsed
                    && ReferenceEquals(window.BtDeviceArt.Source, BluetoothDeviceArtwork.For(name))
                    && window.BtDeviceIcon.Visibility == Visibility.Visible
                    && ReferenceEquals(window.BtDeviceIcon.Source, window.BtDeviceArt.Source),
                    $"真实蓝牙提示将 {name} 显示为 {form} 二维图片");
                Check(window.BtBatteryIcon.Percent == 83 && window.BtBatteryText.Text == "83%",
                    $"{form} 图片提示仍保持准确的实际电量");
                Check(!window.BtDeviceArt.IsHitTestVisible && !window.BtIconContainer.IsHitTestVisible,
                    "设备图片与装饰容器不会截获小岛点击");
            }
            Check(!Descendants<System.Windows.Controls.Viewport3D>(window.BtIconContainer).Any()
                && typeof(alpha_island.MainWindow).Assembly.GetType("alpha_island.Controls.DeviceModelView") == null,
                "蓝牙图标树与程序集均不再包含设备 3D 建模控件");
            var last = new BluetoothDeviceInfo("artwork-probe-open", "OpenFit", BluetoothDeviceCategory.Audio, 83, true);
            PrivateCall(window, "ShowBluetoothNotify", last, true);
            PrivateCall(window, "OnBluetoothBatteryUpdated", null, last with { BatteryPercent = 17 });
            Check(window.BtBatteryIcon.Percent == 17 && window.BtBatteryText.Text == "17%",
                "二维设备提示继续响应当前设备电量更新");
            PrivateCall(window, "ShowBluetoothNotify", last with { IsConnected = false }, false);
            PrivateCall(window, "OnBluetoothBatteryUpdated", null, last);
            Check(window.BtBatteryIcon.Percent == null && window.BtBatteryIcon.Visibility == Visibility.Collapsed
                && ReferenceEquals(window.BtDeviceArt.Source, BluetoothDeviceArtwork.For(last.Name)),
                "断联保留对应设备外形但清空旧电量，迟到回报不恢复电量");
            var keyboard = last with { Category = BluetoothDeviceCategory.Keyboard, Name = "AirPods Max" };
            PrivateCall(window, "ShowBluetoothNotify", keyboard, true);
            Check(window.BtDeviceArt.Visibility == Visibility.Collapsed && window.BtDeviceArt.Source == null
                && window.BtDeviceIcon.Visibility == Visibility.Visible && window.BtDeviceIcon.Text == "\uE765",
                "实际键盘分类优先于耳机式名称，不显示上一个音频设备图片");
        }
        finally
        {
            layers.Hide("bluetooth"); PrivateCall(window, "SyncNotificationLayers");
            PrivateCall(window, "SetState", state, true, previousPage);
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduce, powerSave);
        }
    }
}
