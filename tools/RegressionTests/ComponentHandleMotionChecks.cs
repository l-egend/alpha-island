using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;

internal static partial class Program
{
    private static void ComponentHandleMotionChecks(alpha_island.MainWindow window, Grid root)
    {
        var handle = window.ComponentHandle;
        bool reduced = MotionPolicy.ReduceMotion, saving = MotionPolicy.PowerSave;
        int tier = MotionPolicy.RenderTier;
        var offset = (DependencyProperty)typeof(IslandContourHandle).GetField("HintOffsetProperty", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var samples = new List<BitmapSource>();
        bool HasClock() => DependencyPropertyHelper.GetValueSource(handle, offset).IsAnimated;
        double Capture()
        {
            root.UpdateLayout();
            int width = (int)Math.Ceiling(handle.ActualWidth), height = (int)Math.Ceiling(handle.ActualHeight);
            // The handle is positioned outside its parent's bounds. Render the real root and
            // crop its screen-relative location instead of clipping away its Canvas offset.
            var full = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32); full.Render(root);
            var origin = handle.TransformToAncestor(root).Transform(new Point());
            var bitmap = new CroppedBitmap(full, new Int32Rect((int)Math.Round(origin.X), (int)Math.Round(origin.Y), width, height)); bitmap.Freeze(); samples.Add(bitmap);
            var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
            double total = 0; int count = 0;
            for (int i = 0; i < pixels.Length / 4; i++) if (pixels[i * 4 + 3] > 80 && Math.Max(pixels[i * 4], pixels[i * 4 + 1]) > 120) { total += i % width; count++; }
            return count == 0 ? double.NaN : total / count;
        }
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(true, false);
            PrivateCall(window, "SetState", IslandState.Pill, true); root.UpdateLayout(); PumpAnimations(50);
            handle.StopHint(); double start = Capture();
            MotionPolicy.Apply(false, false); handle.PlayHint(); PumpAnimations(420); double right = Capture();
            Console.WriteLine($"Handle cue: loaded={handle.IsLoaded}, visible={handle.IsVisible}, height={handle.ActualHeight:0.0}, state={AgentField<IslandState>(window, "_state")}, animating={handle.IsHintAnimating}, offset={handle.HintOffset:0.00}, centers={start:0.00}/{right:0.00}, instant={MotionPolicy.InstantMorph}, saving={MotionPolicy.PowerSave}");
            Check(handle.IsHintAnimating && right > start + 2.5 && right < start + 4.8, "右侧切换线在真实动画中缓慢小幅右移，像素渲染位移在约4像素范围");
            var point = new Point(24, handle.ActualHeight / 2); var screen = handle.PointToScreen(point);
            Check(handle.ContainsHandlePoint(point)
                && VisualTreeHelper.HitTest(root, handle.TranslatePoint(point, root))?.VisualHit == handle
                && (bool)PrivateCall(window, "IsPointOnComponentHandle", (int)Math.Round(screen.X), (int)Math.Round(screen.Y))!,
                "提示线移动期间收紧后的点击范围保持固定，WPF 与原生鼠标判定一致");
            PumpAnimations(1050); double returned = Capture();
            Check(Math.Abs(returned - start) < .6 && Math.Abs(handle.HintOffset) < .001 && !handle.IsHintAnimating && !HasClock(),
                "提示线自动回到原位并释放动画时钟，没有常驻刷新");
            handle.PlayHint(); PumpAnimations(100); handle.Engaged = true;
            Check(!handle.IsHintAnimating && !HasClock() && handle.HintOffset == 0,
                "点击或长按时提示动画立即结束，切换菜单的锚点不漂移");
            handle.Engaged = false; MotionPolicy.Apply(true, false); handle.PlayHint();
            Check(!handle.IsHintAnimating && !HasClock(), "减弱动效时切换条保持静止且范围仍可点击");
            MotionPolicy.Apply(false, true); handle.PlayHint();
            Check(!handle.IsHintAnimating && !HasClock(), "节能模式下不播放切换条提示动画");
            MotionPolicy.Apply(false, false); PumpAnimations(1450); handle.PlayHint(); PumpAnimations(100);
            MotionPolicy.Apply(true, false);
            Check(!handle.IsHintAnimating && !HasClock() && handle.HintOffset == 0,
                "动效策略变化会立即清除已经运行的提示动画");
            MotionPolicy.Apply(false, false); PumpAnimations(1450); handle.PlayHint(); PumpAnimations(100);
            PrivateCall(window, "SetState", IslandState.Bar, true);
            Check(handle.Visibility == Visibility.Collapsed && !handle.IsHintAnimating && !HasClock(),
                "收起岛后切换条与提示动画一起停止");

            var sheet = new Canvas { Width = 348, Height = 128, Background = new SolidColorBrush(Color.FromRgb(13, 20, 33)) };
            string[] labels = { "原位", "向右滑动", "回到原位" };
            for (int i = 0; i < 3; i++)
            {
                var image = new System.Windows.Controls.Image { Source = samples[i], Width = samples[i].PixelWidth, Height = samples[i].PixelHeight };
                Canvas.SetLeft(image, 25 + 112 * i); Canvas.SetTop(image, 15); sheet.Children.Add(image);
                AddSemanticPreviewText(sheet, labels[i], 5 + 112 * i, 87, 112, 12, false, true, TextAlignment.Center);
            }
            sheet.Measure(new(348, 128)); sheet.Arrange(new(0, 0, 348, 128)); sheet.UpdateLayout();
            string output = Path.GetFullPath("bin/component-handle-previews"); Directory.CreateDirectory(output);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(SemanticPreviewFrame(sheet, 348, 128)));
            using var stream = File.Create(Path.Combine(output, "slide-sequence.png")); encoder.Save(stream);
        }
        finally
        {
            handle.Engaged = false; handle.StopHint();
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduced, saving);
        }
    }
}
