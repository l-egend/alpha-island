using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;
using AlphaIsland.PluginSdk;
using Grid = System.Windows.Controls.Grid;
using Color = System.Windows.Media.Color;

internal static partial class Program
{
    private static void WelcomeLogoChecks(alpha_island.MainWindow main, Grid root)
    {
        var options = AgentField<IslandOptions>(main, "_options");
        int tier = MotionPolicy.RenderTier;
        bool reduce = MotionPolicy.ReduceMotion, save = MotionPolicy.PowerSave;
        string output = Path.GetFullPath("bin/WelcomeLogoAudit"); Directory.CreateDirectory(output);
        try
        {
            var legacy = new IslandOptions(); SettingsRegistry.Write(legacy, "GreetingPreset", "rabbit");
            Check(GreetingPresetCatalog.GetId(legacy) == GreetingPresetCatalog.Logo, "旧兔子配置自动使用新的项目 Logo");
            GreetingPresetCatalog.Select(legacy, "rabbit");
            Check(SettingsRegistry.GetText(legacy, "GreetingPreset", "") == "logo", "旧默认组合保存为新的 Logo 标识");
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            options.GreetingEnabled = true; options.GreetingSeconds = 10;
            SettingsRegistry.Write(options, "GreetingSoundEnabled", false);
            GreetingPresetCatalog.Select(options, GreetingPresetCatalog.Logo);
            PrivateCall(main, "DismissGreeting"); PrivateCall(main, "ApplyGreetingMedia");
            PrivateCall(main, "PreviewStartupGreeting"); PumpAnimations(550); root.UpdateLayout();
            var icon = main.GreetingIcon;
            Check(icon.IsLoaded && icon.IsVisible && icon.ActualWidth == 32 && icon.ActualHeight == 32,
                "默认欢迎使用 32 像素项目 Logo，不改变欢迎文字的布局");
            Check(DependencyPropertyHelper.GetValueSource(icon, WelcomeLogoIcon.ProgressProperty).IsAnimated,
                "实际欢迎展示后开始左下方入场动画");
            double progress = icon.Progress; PrivateCall(main, "ApplyGreetingMedia");
            Check(icon.Progress >= progress, "重复应用设置不会从头播放 Logo");
            PumpAnimations((int)WelcomeLogoIcon.PlaybackDuration.TotalMilliseconds + 200);
            Check(icon.Progress == 1 && !DependencyPropertyHelper.GetValueSource(icon, WelcomeLogoIcon.ProgressProperty).IsAnimated,
                "Logo 完成一次往返后释放动画时钟");
            PrivateCall(main, "ApplyGreetingMedia");
            Check(icon.Progress == 1, "欢迎仍显示时保持最终位置，不重新播放");
            var completed = Content(CurrentFrame());
            Check(main.GreetingLayer.Visibility == Visibility.Visible && completed.Count > 0,
                "实际动画结束后欢迎弹窗仍显示时，左侧必须保留可见图标");

            BitmapSource CurrentFrame()
            {
                icon.UpdateLayout(); PumpAnimations(2);
                var bitmap = new RenderTargetBitmap(128, 128, 384, 384, PixelFormats.Pbgra32);
                bitmap.Render(icon);
                return bitmap;
            }
            BitmapSource Frame(double value)
            {
                AnimationClockLifetime.Begin(icon, WelcomeLogoIcon.ProgressProperty, null);
                icon.Progress = value;
                return CurrentFrame();
            }
            (int Count, double X, double Y) Content(BitmapSource frame)
            {
                int stride = frame.PixelWidth * 4, count = 0; double xSum = 0, ySum = 0;
                byte[] data = new byte[stride * frame.PixelHeight]; frame.CopyPixels(data, stride, 0);
                for (int y = 0; y < frame.PixelHeight; y++)
                    for (int x = 0; x < frame.PixelWidth; x++)
                        if (data[y * stride + x * 4 + 3] > 160) { count++; xSum += x; ySum += y; }
                return (count, xSum / Math.Max(1, count), ySum / Math.Max(1, count));
            }
            var first = Content(Frame(0)); var emerging = Content(Frame(.08)); var approaching = Content(Frame(.15));
            var hold = Content(Frame(.35)); var flight = Content(Frame(.65)); var departed = Content(Frame(.80));
            var returning = Content(Frame(.88)); var returnedNear = Content(Frame(.96)); var end = Content(Frame(1));
            Console.WriteLine($"Logo pixels: start={first}, emerging={emerging}, hold={hold}, flight={flight}, departed={departed}, returning={returning}, end={end}");
            Check(first.Count == 0 && emerging.Count > 0 && emerging.Count < hold.Count,
                "Logo 从显示区域外逐渐进入，停留时显示完整主体");
            Check(emerging.X < hold.X - 12 && emerging.Y > hold.Y + 12,
                "入场的实际图像位于中心的左下方，沿斜线进入而不是从正下方弹出");
            Check(approaching.X > emerging.X && approaching.Y < emerging.Y,
                "入场过程持续向右上方靠近中心");
            Check(completed.Count == hold.Count && completed.Count > 0,
                "欢迎弹窗仍显示时，实际播放完成后的图标完整停在原位，左侧不持续空白");
            Check(flight.X > hold.X + 8 && flight.Y < hold.Y - 8 && departed.Count == 0,
                "Logo 先向右上方完整飞出显示区域");
            Check(returning.Count > 0 && returning.X < hold.X - 12 && returning.Y > hold.Y + 12
                && returnedNear.X > returning.X && returnedNear.Y < returning.Y,
                "飞出后从左下方再次平滑飞回中心");
            Check(end.Count == hold.Count && Math.Abs(end.X - hold.X) < .01 && Math.Abs(end.Y - hold.Y) < .01,
                "返程结束恢复完整图标，位置与中心停顿时一致");
            double[] samples = [0, .08, .15, .35, .65, .80, .88, .96, 1];
            var sheet = new DrawingVisual();
            using (var drawing = sheet.RenderOpen())
            {
                drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(25, 43, 67)), null, new Rect(0, 0, samples.Length * 128, 128));
                for (int i = 0; i < samples.Length; i++) drawing.DrawImage(Frame(samples[i]), new Rect(i * 128, 0, 128, 128));
            }
            var contact = new RenderTargetBitmap(samples.Length * 128, 128, 96, 96, PixelFormats.Pbgra32); contact.Render(sheet);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(contact));
            using (var file = File.Create(Path.Combine(output, "logo-stages.png"))) png.Save(file);
            var gif = new GifBitmapEncoder();
            int frameCount = (int)Math.Round(WelcomeLogoIcon.PlaybackDuration.TotalSeconds * 20);
            for (int i = 0; i <= frameCount; i++)
            {
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(25, 43, 67)), null, new Rect(0, 0, 128, 128));
                    drawing.DrawImage(Frame(i / (double)frameCount), new Rect(0, 0, 128, 128));
                }
                var bitmap = new RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                var metadata = new BitmapMetadata("gif"); metadata.SetQuery("/grctlext/Delay", (ushort)(i == frameCount ? 100 : 5));
                gif.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
            }
            using (var file = File.Create(Path.Combine(output, "logo-motion.gif"))) gif.Save(file);
            Frame(.45); root.UpdateLayout();
            var actual = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            actual.Render(root);
            var body = main.IslandBody.TransformToAncestor(root).TransformBounds(new Rect(main.IslandBody.RenderSize));
            var crop = new CroppedBitmap(actual, new Int32Rect((int)body.X, (int)body.Y, (int)Math.Ceiling(body.Width), (int)Math.Ceiling(body.Height)));
            var snapshot = new PngBitmapEncoder(); snapshot.Frames.Add(BitmapFrame.Create(crop));
            using (var file = File.Create(Path.Combine(output, "welcome-in-island.png"))) snapshot.Save(file);

            PrivateCall(main, "DismissGreeting");
            Check(!DependencyPropertyHelper.GetValueSource(icon, WelcomeLogoIcon.ProgressProperty).IsAnimated,
                "欢迎隐藏时不保留动画时钟");
            PrivateCall(main, "PreviewStartupGreeting"); PumpAnimations(350);
            Check(icon.Progress < 1, "下一次临时测试可以重新播放一次");
            MotionPolicy.Apply(false, true);
            Check(!DependencyPropertyHelper.GetValueSource(icon, WelcomeLogoIcon.ProgressProperty).IsAnimated && icon.Progress == .45,
                "节能模式停止动画并保留静态 Logo");
            MotionPolicy.Apply(false, false); PrivateCall(main, "DismissGreeting");
            SettingsRegistry.Write(options, "ReduceEffectsEnabled", true);
            PrivateCall(main, "PreviewStartupGreeting"); PumpAnimations(350);
            Check(!icon.AnimationEnabled && !DependencyPropertyHelper.GetValueSource(icon, WelcomeLogoIcon.ProgressProperty).IsAnimated,
                "削减动效时显示静态 Logo");
        }
        finally
        {
            PrivateCall(main, "DismissGreeting");
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduce, save);
        }
    }
}
