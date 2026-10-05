using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WpfImage = System.Windows.Controls.Image;
using WpfPath = System.Windows.Shapes.Path;

internal static partial class Program
{
    // A disposable, offscreen capability probe. These placeholder visuals are not a product design.
    private static void ActionIconFeasibilityChecks()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Synthetic two-frame, full-canvas GIF: colored blocks, transparency, 120 ms/frame.
                // WPF's encoder did not retain the requested delay, so use a standard GIF fixture.
                // This fixture is test data, not an animation design or a shipped asset.
                const string gif = "R0lGODlhEAAQAIEAAAAAAAD//wAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQFDAAAACwAAAAAEAAQAAAINAABCBxIsGCAgggTKlwYoKHDhwgfSjxocKLDiBYbYsy40WLHiR8lhoRYkePCkygJUkwJICAAIfkEBQwAAAAsAAAAABAAEACBAAAAk3DbAAAAAAAACDQAAQgcSLBggIIIEypcGKChw4cIH0o8aHCiw4gWG2LMuNFix4kfJYaEWJHjwpMoCVJMCSAgADs=";
                using var stream = new MemoryStream(Convert.FromBase64String(gif));
                var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frames = decoder.Frames.ToArray();
                foreach (var frame in frames) frame.Freeze();
                Check(frames.Length == 2, "WPF 原生解码器能读取多帧 GIF");
                Check(frames.All(frame => frame.PixelWidth == 16 && frame.PixelHeight == 16), "验证素材使用完整画布帧");
                Check(frames.All(frame => frame.IsFrozen), "GIF 帧可冻结并共享缓存，无需每拍重新解码");
                Check((ushort)((BitmapMetadata)frames[0].Metadata).GetQuery("/grctlext/Delay") == 12,
                    "GIF 帧延时元数据可以读取");
                Check(!Pixels(frames[0]).SequenceEqual(Pixels(frames[1])), "解码得到两个不同的可绘制帧");
                Check(Pixels(frames[0])[3] == 0, "测试 GIF 的透明背景可保留");

                var root = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                var plainImage = new WpfImage { Source = frames[0], Width = 32, Height = 32 };
                var rotation = new RotateTransform();
                var vector = new WpfPath
                {
                    Width = 32, Height = 32, Stretch = Stretch.Uniform,
                    Data = Geometry.Parse("M 5,4 L 27,16 L 11,28 Z"), Fill = System.Windows.Media.Brushes.Cyan,
                    RenderTransform = rotation, RenderTransformOrigin = new System.Windows.Point(.5, .5), IsHitTestVisible = false,
                };
                using var player = new ProbeGifIcon(frames);
                root.Children.Add(plainImage); root.Children.Add(player.View); root.Children.Add(vector);
                using var source = new HwndSource(new HwndSourceParameters("Action icon capability probe")
                { Width = 120, Height = 40, PositionX = -32000, PositionY = -32000,
                    WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
                source.RootVisual = root; root.UpdateLayout();
                var firstPlain = Pixels(Render(plainImage));
                Check(firstPlain.Any(pixel => pixel != 0), "离屏绘制能取得图标的非空像素");
                PumpAnimations(180);
                Check(ReferenceEquals(plainImage.Source, frames[0]) && firstPlain.SequenceEqual(Pixels(Render(plainImage))),
                    "普通 WPF Image 不会自行切换 GIF 帧，需要播放适配器");

                player.Update(actionActive: true, visible: true);
                var firstAnimated = Pixels(Render(player.View));
                Until(() => player.FramesAdvanced > 0);
                Check(player.FramesAdvanced > 0 && !firstAnimated.SequenceEqual(Pixels(Render(player.View))),
                    "隔离 GIF 播放适配器能定时换帧并改变实际像素");
                Check(player.View.ActualWidth == 32 && player.View.ActualHeight == 32,
                    "GIF 播放保持左侧图标占位 32×32，不改变布局");
                Check(!player.View.IsHitTestVisible, "动态图标可禁用命中，不截获岛的点击与拖动");

                var originalSize = vector.RenderSize;
                var vectorPixels = Pixels(Render(vector));
                rotation.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(800)) { RepeatBehavior = RepeatBehavior.Forever });
                PumpAnimations(180);
                Check(rotation.HasAnimatedProperties && rotation.Angle > 0,
                    "WPF 原生程序动画可以驱动图标 RenderTransform");
                Check(!vectorPixels.SequenceEqual(Pixels(Render(vector))), "程序动画改变实际图标像素");
                Check(vector.RenderSize == originalSize, "RenderTransform 动画不改变图标布局尺寸");
                rotation.BeginAnimation(RotateTransform.AngleProperty, null); rotation.Angle = 0;
                Check(!rotation.HasAnimatedProperties, "动作结束可移除程序动画时钟并恢复静态图标");

                player.Update(actionActive: false, visible: true);
                int stoppedAt = player.FramesAdvanced;
                PumpAnimations(180);
                Check(!player.IsRunning && player.FramesAdvanced == stoppedAt,
                    "任务结束后停止 GIF，即使图标仍被置顶显示");
                player.Update(actionActive: true, visible: false);
                PumpAnimations(180);
                Check(!player.IsRunning && player.FramesAdvanced == stoppedAt,
                    "动作仍在进行但图标隐藏时不播放 GIF");
                player.Update(actionActive: true, visible: true, reduceMotion: true);
                Check(!player.IsRunning && ReferenceEquals(player.Image.Source, frames[0]),
                    "减弱动效时可以使用静态首帧");
                player.Update(actionActive: true, visible: true, softwareRendering: true);
                Check(!player.IsRunning, "软件渲染时可以关闭循环动画");
                player.Update(actionActive: true, visible: true);
                Until(() => player.FramesAdvanced > stoppedAt);
                Check(player.IsRunning && player.FramesAdvanced > stoppedAt, "恢复动作与可见性后可重新播放");

                foreach (int size in new[] { 24, 36 })
                {
                    player.View.Width = size; player.View.Height = size; root.UpdateLayout();
                    Check(player.View.ActualWidth == size && player.View.ActualHeight == size && Render(player.View).PixelWidth == size,
                        $"同一 GIF 帧缓存可以用于 {size}×{size} 的卫星或卡片图标槽");
                }
                player.Dispose();
                Check(!player.IsRunning, "图标卸载或释放时可以停止计时器");
            }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error != null) throw new Exception("Action icon feasibility probe failed", error);

        static RenderTargetBitmap Render(FrameworkElement view)
        {
            view.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight),
                96, 96, PixelFormats.Pbgra32);
            // A child has a layout offset inside the StackPanel; brush it at the bitmap origin.
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
                dc.DrawRectangle(new VisualBrush(view), null, new Rect(0, 0, view.ActualWidth, view.ActualHeight));
            bitmap.Render(drawing); return bitmap;
        }
        static byte[] Pixels(BitmapSource source)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
            converted.CopyPixels(pixels, converted.PixelWidth * 4, 0); return pixels;
        }
        static void Until(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(3)) PumpAnimations(10);
            if (!condition()) throw new Exception("Animation probe condition timed out");
        }
    }

    // Only full-frame GIFs are covered here. Arbitrary artwork needs disposal/partial-frame compositing.
    private sealed class ProbeGifIcon : IDisposable
    {
        private readonly BitmapFrame[] _frames;
        private readonly DispatcherTimer _timer;
        private int _index;
        public WpfImage Image { get; } = new() { Stretch = Stretch.Uniform, IsHitTestVisible = false };
        public Border View { get; }
        public int FramesAdvanced { get; private set; }
        public bool IsRunning => _timer.IsEnabled;
        public ProbeGifIcon(BitmapFrame[] frames)
        {
            _frames = frames; Image.Source = frames[0];
            View = new Border { Width = 32, Height = 32, Child = Image, IsHitTestVisible = false, ClipToBounds = true };
            _timer = new DispatcherTimer(DispatcherPriority.Render);
            _timer.Tick += Tick;
            View.Unloaded += (_, _) => _timer.Stop();
        }
        private void Tick(object? sender, EventArgs e)
        {
            _index = (_index + 1) % _frames.Length;
            Image.Source = _frames[_index]; FramesAdvanced++; SetFrameDelay();
        }
        private void SetFrameDelay()
        {
            var metadata = (BitmapMetadata)_frames[_index].Metadata;
            int delay = Convert.ToInt32(metadata.GetQuery("/grctlext/Delay"));
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, delay * 10));
        }
        public void Update(bool actionActive, bool visible, bool reduceMotion = false, bool softwareRendering = false)
        {
            if (actionActive && visible && !reduceMotion && !softwareRendering)
            { SetFrameDelay(); _timer.Start(); }
            else
            { _timer.Stop(); _index = 0; Image.Source = _frames[0]; }
        }
        public void Dispose() { _timer.Stop(); _timer.Tick -= Tick; }
    }
}
