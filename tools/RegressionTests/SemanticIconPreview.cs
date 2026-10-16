using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Controls;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;

internal static partial class Program
{
    // root is the project directory, not the isolated regression configuration directory.
    // All frames come from the production SemanticIcon and its actual WPF animations.
    private static void SaveSemanticIconPreview(string root)
    {
        const int width = 696, height = 346;
        bool reduced = MotionPolicy.ReduceMotion, saved = MotionPolicy.PowerSave;
        int tier = MotionPolicy.RenderTier;
        var canvas = new Canvas { Width = width, Height = height, Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x14, 0x21)) };
        var descriptions = new (IconShape Shape, string Title, string Detail, ActionIconAnimation Cue)[]
        {
            (IconShape.PlayPause, "播放 / 暂停", "三角形与两条竖线内部形变", ActionIconAnimation.Tick),
            (IconShape.Wifi, "Wi-Fi 开关", "信号弧线逐段描画与擦除", ActionIconAnimation.Connect),
            (IconShape.Bluetooth, "蓝牙连接", "连接波纹在固定图标周围变化", ActionIconAnimation.Connect),
            (IconShape.Volume, "音量 / 静音", "声波切换为静音划线", ActionIconAnimation.Pulse),
            (IconShape.Battery, "电量状态", "电量与充电状态在图标内变化", ActionIconAnimation.Pulse),
            (IconShape.Agent, "Agent 状态", "图标内部运行状态提示", ActionIconAnimation.Pulse)
        };
        AddSemanticPreviewText(canvas, "图标内部状态动画", 18, 12, width - 36, 22, true);
        AddSemanticPreviewText(canvas, "实际程序控件渲染 · 开启与关闭各播放一次 · 图标位置与大小固定", 18, 46, width - 36, 12, false, true);
        var icons = new List<SemanticIcon>();
        var iconRects = new List<Int32Rect>();
        for (int index = 0; index < descriptions.Length; index++)
        {
            int x = 12 + 228 * (index % 3), y = 78 + 136 * (index / 3);
            var content = new Canvas { Width = 216, Height = 126 };
            var card = new Border { Width = 216, Height = 126, CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Color.FromRgb(0x18, 0x23, 0x36)), Child = content };
            Canvas.SetLeft(card, x); Canvas.SetTop(card, y); canvas.Children.Add(card);
            var icon = NewSemanticProbe(descriptions[index].Shape, false);
            icon.Foreground = new SolidColorBrush(index == 5 ? Color.FromRgb(0xBD, 0x9B, 0xFF) : Color.FromRgb(0x65, 0xDB, 0xFF));
            Canvas.SetLeft(icon, 76); Canvas.SetTop(icon, 8); content.Children.Add(icon); icons.Add(icon);
            iconRects.Add(new Int32Rect(x + 76, y + 8, 64, 64));
            AddSemanticPreviewText(content, descriptions[index].Title, 0, 78, 216, 13, true, false, TextAlignment.Center);
            AddSemanticPreviewText(content, descriptions[index].Detail, 4, 101, 208, 10, false, true, TextAlignment.Center);
        }
        using var source = new HwndSource(new HwndSourceParameters("Alpha Island semantic preview")
        { Width = width, Height = height, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x80000000) });
        source.RootVisual = canvas; canvas.UpdateLayout(); PumpAnimations(30);
        var frames = new List<BitmapSource>();
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            foreach (var icon in icons) ActionIconMotion.Stop(icon);
            frames.Add(SemanticPreviewFrame(canvas, width, height));
            foreach (bool active in new[] { true, false })
            {
                for (int index = 0; index < icons.Count; index++)
                {
                    icons[index].Active = active;
                    icons[index].Level = active ? .82 : .25;
                    ActionIconMotion.Play(icons[index], active ? descriptions[index].Cue : ActionIconAnimation.Disconnect);
                }
                for (int index = 0; index < 34; index++)
                {
                    PumpAnimations(50); canvas.UpdateLayout(); frames.Add(SemanticPreviewFrame(canvas, width, height));
                }
            }
            string directory = Path.Combine(root, "bin", "icon-previews"); Directory.CreateDirectory(directory);
            var gif = new GifBitmapEncoder();
            foreach (var frame in frames)
            {
                var metadata = new BitmapMetadata("gif"); metadata.SetQuery("/grctlext/Delay", (ushort)5);
                metadata.SetQuery("/grctlext/Disposal", (byte)1);
                gif.Frames.Add(BitmapFrame.Create(frame, null, metadata, null));
            }
            using (var encoded = new MemoryStream())
            {
                gif.Save(encoded); byte[] bytes = encoded.ToArray();
                int paletteBytes = (bytes[10] & 0x80) != 0 ? 3 * (1 << ((bytes[10] & 7) + 1)) : 0;
                int position = 13 + paletteBytes;
                // Only the review artifact loops, including its static pauses. Production clocks are finite.
                byte[] loop = { 0x21, 0xFF, 0x0B, (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A', (byte)'P', (byte)'E', (byte)'2', (byte)'.', (byte)'0', 3, 1, 0, 0, 0 };
                using var file = File.Create(Path.Combine(directory, "semantic-icon-motion.gif"));
                file.Write(bytes, 0, position); file.Write(loop); file.Write(bytes, position, bytes.Length - position);
            }
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(frames[29]));
            using (var file = File.Create(Path.Combine(directory, "semantic-icon-motion.png"))) png.Save(file);
            SaveSemanticIconSequence(frames, iconRects, descriptions.Select(item => item.Title).ToArray(),
                Path.Combine(directory, "semantic-icon-sequence.png"));
            using var checkStream = File.OpenRead(Path.Combine(directory, "semantic-icon-motion.gif"));
            var decoder = new GifBitmapDecoder(checkStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != frames.Count || decoder.Frames.Any(frame => frame.PixelWidth != width || frame.PixelHeight != height))
                throw new InvalidDataException("Semantic GIF does not retain the real rendered frames.");
        }
        finally
        {
            foreach (var icon in icons) { ActionIconMotion.SetKind(icon, ActionIconAnimation.None); ActionIconMotion.Stop(icon); }
            source.RootVisual = null;
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduced, saved);
        }
    }

    private static void AddSemanticPreviewText(Canvas canvas, string value, double x, double y, double width,
        double size, bool bold, bool muted = false, TextAlignment alignment = TextAlignment.Left)
    {
        var text = new TextBlock { Text = value, Width = width, FontSize = size, FontFamily = new FontFamily("Microsoft YaHei UI"),
            Foreground = muted ? new SolidColorBrush(Color.FromRgb(0x97, 0xAA, 0xBF)) : Brushes.White,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextAlignment = alignment };
        Canvas.SetLeft(text, x); Canvas.SetTop(text, y); canvas.Children.Add(text);
    }

    private static BitmapSource SemanticPreviewFrame(FrameworkElement element, int width, int height)
    {
        var frame = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); frame.Render(element); frame.Freeze(); return frame;
    }

    private static void SaveSemanticIconSequence(IReadOnlyList<BitmapSource> frames, IReadOnlyList<Int32Rect> iconRects,
        IReadOnlyList<string> names, string path)
    {
        const int width = 696, height = 536;
        var canvas = new Canvas { Width = width, Height = height, Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x14, 0x21)) };
        AddSemanticPreviewText(canvas, "图标内部变化 · 实际状态序列", 18, 12, width - 36, 20, true);
        string[] stages = { "起始", "描画中", "形变中", "最终" }; int[] snapshots = { 0, 8, 16, 29 };
        for (int column = 0; column < 4; column++)
            AddSemanticPreviewText(canvas, stages[column], 150 + 128 * column, 50, 128, 12, false, true, TextAlignment.Center);
        for (int row = 0; row < names.Count; row++)
        {
            AddSemanticPreviewText(canvas, names[row], 18, 96 + 72 * row, 130, 13, true);
            for (int column = 0; column < 4; column++)
            {
                var image = new System.Windows.Controls.Image { Source = new CroppedBitmap(frames[snapshots[column]], iconRects[row]), Width = 64, Height = 64 };
                Canvas.SetLeft(image, 182 + 128 * column); Canvas.SetTop(image, 74 + 72 * row); canvas.Children.Add(image);
            }
        }
        canvas.Measure(new System.Windows.Size(width, height)); canvas.Arrange(new Rect(0, 0, width, height)); canvas.UpdateLayout();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(SemanticPreviewFrame(canvas, width, height)));
        using var file = File.Create(path); encoder.Save(file);
    }
}
