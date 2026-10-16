using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Controls;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

internal static partial class Program
{
    private static void SemanticIconControlChecks()
    {
        var failures = new List<string>();
        RunSemanticIconUiChecks(failures, ref _checks);
        RedesignedIconChecks();
        if (failures.Count != 0) throw new Exception("FAIL: " + string.Join("; ", failures));
        Console.WriteLine("PASS: 真实 WPF 图标内部形变、状态笔划、显示门控和有限时钟检查");
    }
    // Called on the regression runner's STA thread. The probe draws the production control,
    // with actual WPF clocks and pixels; it neither changes the desktop nor calls a service.
    private static void RunSemanticIconUiChecks(List<string> failures, ref int checks)
    {
        int completed = 0;
        void Assert(bool passed, string message) { completed++; if (!passed) failures.Add(message); }
        bool reduced = MotionPolicy.ReduceMotion, saved = MotionPolicy.PowerSave;
        int tier = MotionPolicy.RenderTier;
        var root = new Grid { Width = 96, Height = 96 };
        var panel = new Grid { Width = 64, Height = 64 };
        root.Children.Add(panel);
        var icon = NewSemanticProbe(IconShape.PlayPause, false);
        panel.Children.Add(icon);
        using var source = new HwndSource(new HwndSourceParameters("Alpha Island semantic icon probe")
        { Width = 96, Height = 96, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x80000000) });
        source.RootVisual = root; root.UpdateLayout(); PumpAnimations(30);
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            ActionIconMotion.Stop(icon);
            var original = icon.RenderTransform;
            byte[] play = SemanticIconPixels(icon);
            Assert(SemanticInkRuns(play, 32) == 1, "暂停状态显示一个连贯的播放三角形");
            icon.Active = true; ActionIconMotion.Play(icon, ActionIconAnimation.Tick); PumpAnimations(220);
            byte[] morph = SemanticIconPixels(icon);
            Assert(icon.TransitionProgress > 0 && icon.TransitionProgress < 1
                && SemanticPixelDifference(play, morph) > 8,
                "播放三角到暂停条经过真实中间轮廓，而非瞬间换图标");
            Assert(SemanticProgressHasClock(icon), "中间状态由实际 WPF 属性时钟驱动");
            Assert(ReferenceEquals(icon.RenderTransform, original) && !icon.RenderTransform.HasAnimatedProperties,
                "内部形变保持原 RenderTransform，不整体旋转或缩放图标");
            PumpAnimations(1500); byte[] pause = SemanticIconPixels(icon);
            Assert(SemanticInkRuns(pause, 32) == 2 && SemanticPixelDifference(play, pause) > 20,
                "播放状态最终显示两个分离的暂停条");
            Assert(!ActionIconMotion.GetIsAnimating(icon) && !SemanticProgressHasClock(icon)
                && icon.TransitionProgress == 1,
                "内部形变单次结束后恢复静态并移除时钟");

            // A second identical playback refresh must leave the first clock running. An actual
            // reverse around 500 ms must start at the currently drawn split triangle, not commit
            // either final icon and jump back to a new full-length animation.
            icon.Active = false; ActionIconMotion.Stop(icon);
            byte[] rapidStart = SemanticIconPixels(icon);
            // A WPF dispatcher turn can advance to the next display frame even for a 1 ms pump.
            // Allow that small natural movement, while rejecting a jump across a final state.
            int continuityLimit = Math.Max(12, (int)(SemanticPixelDifference(rapidStart, pause) * .06));
            icon.Active = true; ActionIconMotion.Play(icon, ActionIconAnimation.Tick); PumpAnimations(450);
            double beforeRepeat = icon.TransitionProgress;
            byte[] beforeRepeatPixels = SemanticIconPixels(icon);
            double repeatedState = icon.DisplayedState;
            ActionIconMotion.Play(icon, ActionIconAnimation.Tick);
            Assert(Math.Abs(icon.DisplayedState - repeatedState) < .000001 && icon.TransitionProgress >= beforeRepeat,
                "同一播放状态再次刷新不重播，不重置当前轮廓或进度");
            PumpAnimations(1);
            PumpAnimations(49); double beforeReverse = icon.TransitionProgress;
            byte[] beforeReversePixels = SemanticIconPixels(icon);
            Assert(beforeReverse > beforeRepeat && beforeReverse > .02 && beforeReverse < .95,
                "同状态刷新后的原动画继续递增，到约500ms仍处于内部形变中");
            double stateBeforeTarget = icon.DisplayedState;
            icon.Active = false;
            Assert(Math.Abs(icon.DisplayedState - stateBeforeTarget) < .000001,
                "快速反向更新目标状态时，旧钟仍绘制原插值轮廓，不提前跳成三角");
            PumpAnimations(1);
            byte[] changedTargetPixels = SemanticIconPixels(icon);
            ActionIconMotion.Play(icon, ActionIconAnimation.Tick); PumpAnimations(1);
            byte[] reverseStartPixels = SemanticIconPixels(icon);
            Assert(icon.TransitionProgress < beforeReverse * .3 && SemanticProgressHasClock(icon)
                && ActionIconMotion.GetIsAnimating(icon),
                "真正播放到暂停的反向为新目标重启内部进度，保留一个实际时钟");
            Assert(SemanticPixelDifference(changedTargetPixels, reverseStartPixels) <= continuityLimit,
                "反向动画从当前可见轮廓开始，重定向首帧像素保持连续");
            PumpAnimations(600);
            Assert(SemanticPixelDifference(SemanticIconPixels(icon), rapidStart)
                < SemanticPixelDifference(beforeReversePixels, rapidStart),
                "反向插值逐渐回到播放三角，而非继续走向过时的暂停条");
            PumpAnimations(1100); byte[] reversedFinal = SemanticIconPixels(icon);
            Assert(SemanticInkRuns(reversedFinal, 32) == 1 && SemanticPixelDifference(reversedFinal, rapidStart) <= 3,
                "快速播放再暂停的最终图形准确回到最后请求的播放三角");
            Assert(!ActionIconMotion.GetIsAnimating(icon) && !SemanticProgressHasClock(icon)
                && icon.TransitionProgress == 1 && ReferenceEquals(icon.RenderTransform, original),
                "反向结束也释放全部时钟，图标整体变换始终不变");

            // Wi-Fi activation draws its signal arcs inside a fixed icon slot.
            icon.Shape = IconShape.Wifi; icon.Active = false; ActionIconMotion.Stop(icon);
            byte[] wifiOff = SemanticIconPixels(icon);
            icon.Active = true; ActionIconMotion.Play(icon, ActionIconAnimation.Connect); PumpAnimations(140);
            byte[] wifiFirst = SemanticIconPixels(icon);
            PumpAnimations(460); byte[] wifiMiddle = SemanticIconPixels(icon);
            PumpAnimations(1000); byte[] wifiOn = SemanticIconPixels(icon);
            Assert(SemanticPixelDifference(wifiFirst, wifiMiddle) > 8
                && SemanticPixelDifference(wifiMiddle, wifiOn) > 8,
                "Wi-Fi 开启逐步描画弧线，有多个可观察内部阶段");
            Assert(SemanticPixelDifference(wifiOff, wifiOn) > 20, "Wi-Fi 开关最终形态不同");
            icon.Active = false; ActionIconMotion.Play(icon, ActionIconAnimation.Disconnect); PumpAnimations(500);
            byte[] wifiErase = SemanticIconPixels(icon); PumpAnimations(1100);
            Assert(SemanticPixelDifference(wifiErase, wifiOn) > 8
                && SemanticPixelDifference(SemanticIconPixels(icon), wifiOff) <= 3,
                "Wi-Fi 关闭擦除信号并停在正确的关闭形态");

            icon.Shape = IconShape.Bluetooth; icon.Active = false; ActionIconMotion.Stop(icon);
            byte[] bluetoothOff = SemanticIconPixels(icon);
            icon.Active = true; ActionIconMotion.Play(icon, ActionIconAnimation.Connect); PumpAnimations(260);
            byte[] rippleFirst = SemanticIconPixels(icon); PumpAnimations(390);
            byte[] rippleMiddle = SemanticIconPixels(icon); PumpAnimations(1000);
            Assert(SemanticPixelDifference(rippleFirst, rippleMiddle) > 8
                && SemanticPixelDifference(bluetoothOff, rippleMiddle) > 8,
                "蓝牙连接由符号内外的波纹演进提示，而非转动整个字形");
            Assert(!SemanticProgressHasClock(icon) && ReferenceEquals(icon.RenderTransform, original),
                "蓝牙连接波纹结束后没有持续渲染时钟或整体变换");

            icon.Shape = IconShape.Volume; icon.Active = true; icon.Level = .8; ActionIconMotion.Stop(icon);
            byte[] audible = SemanticIconPixels(icon);
            icon.Active = false; ActionIconMotion.Play(icon, ActionIconAnimation.Disconnect); PumpAnimations(65);
            byte[] muting = SemanticIconPixels(icon);
            Assert(ActionIconMotion.GetIsAnimating(icon), "静音切换保留可见过渡时间，不直接替换图标");
            PumpAnimations(350);
            byte[] muted = SemanticIconPixels(icon);
            Assert(SemanticPixelDifference(audible, muting) > 8 && SemanticPixelDifference(muting, muted) > 8
                && SemanticPixelDifference(audible, muted) > 20,
                "音量波纹变化为静音 ×，形状内容在中间帧发生变化");
            Assert(ReferenceEquals(icon.RenderTransform, original), "音量及静音动画不移动整个图标");
            Assert(!ActionIconMotion.GetIsAnimating(icon) && !SemanticProgressHasClock(icon), "静音过渡完成后释放动画时钟");
            icon.Active = true; ActionIconMotion.Play(icon, ActionIconAnimation.Connect); PumpAnimations(80);
            byte[] unmuting = SemanticIconPixels(icon); PumpAnimations(350);
            byte[] resumed = SemanticIconPixels(icon);
            Assert(SemanticPixelDifference(muted, unmuting) > 8 && SemanticPixelDifference(unmuting, resumed) > 8,
                "取消静音时 × 淡出、正常声波逐条展开，存在清楚的中间帧");

            // Opacity-only hiding is used by persistent card pages. An IsVisible=true control
            // must not spend its animation while any ancestor is effectively transparent.
            panel.Opacity = 0; root.UpdateLayout(); icon.Shape = IconShape.Wifi; icon.Active = true;
            ActionIconMotion.Play(icon, ActionIconAnimation.Connect); PumpAnimations(300);
            Assert(icon.IsVisible && !ActionIconMotion.GetIsAnimating(icon) && !SemanticProgressHasClock(icon),
                "祖先透明时不提前消耗内部动画，也不保留隐藏时钟");
            panel.Opacity = 1; root.UpdateLayout(); PumpAnimations(220);
            Assert(ActionIconMotion.GetIsAnimating(icon) && icon.TransitionProgress < 1,
                "祖先重新显示后才开始内部动画，卡片打开不会错过效果");
            panel.Opacity = 0; root.UpdateLayout(); PumpAnimations(30);
            Assert(!ActionIconMotion.GetIsAnimating(icon) && !SemanticProgressHasClock(icon),
                "祖先透明隐藏立即清除内部动画时钟");
            panel.Opacity = 1; root.UpdateLayout(); PumpAnimations(50);
            ActionIconMotion.Play(icon, ActionIconAnimation.Connect); PumpAnimations(30);
            Assert(ActionIconMotion.GetIsAnimating(icon), "隐藏测试前内部动画正在运行");
            icon.Visibility = Visibility.Collapsed; root.UpdateLayout(); PumpAnimations(30);
            Assert(!ActionIconMotion.GetIsAnimating(icon) && !SemanticProgressHasClock(icon),
                "Collapsed 图标立即停止内部动画");
            icon.Visibility = Visibility.Visible; root.UpdateLayout(); PumpAnimations(80);
            ActionIconMotion.Play(icon, ActionIconAnimation.Connect); PumpAnimations(30);
            Assert(ActionIconMotion.GetIsAnimating(icon), "卸载测试前内部动画正在运行");
            panel.Children.Remove(icon); root.UpdateLayout(); PumpAnimations(30);
            Assert(!ActionIconMotion.GetIsAnimating(icon) && !SemanticProgressHasClock(icon),
                "卸载图标移除内部时钟，临时卡片不留下活动动画");
            panel.Children.Add(icon); root.UpdateLayout(); PumpAnimations(40);

            foreach (var (reduce, save, renderTier, label) in new[]
            { (true, false, 2, "减弱动效"), (false, true, 2, "节能模式"), (false, false, 0, "软件渲染") })
            {
                MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
                ActionIconMotion.Play(icon, ActionIconAnimation.Connect); PumpAnimations(80);
                MotionPolicy.Apply(reduce, save); MotionPolicy.OverrideRenderTierForDebug(renderTier);
                Assert(!ActionIconMotion.GetIsAnimating(icon) && !SemanticProgressHasClock(icon),
                    label + "立即停止已经开始的内部动画");
                icon.Active = !icon.Active; ActionIconMotion.Play(icon, ActionIconAnimation.Connect); PumpAnimations(30);
                Assert(!ActionIconMotion.GetIsAnimating(icon) && !SemanticProgressHasClock(icon)
                    && ReferenceEquals(icon.RenderTransform, original), label + "中仍准确显示静态状态，无整体变换");
            }
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(true, false);
            icon.Shape = IconShape.Agent; icon.Active = false; icon.Text = "\uE99A";
            byte[] idleAgent = SemanticIconPixels(icon);
            icon.Text = "\uE783"; ActionIconMotion.Play(icon, ActionIconAnimation.Disconnect);
            byte[] errorAgent = SemanticIconPixels(icon);
            Assert(SemanticPixelDifference(idleAgent, errorAgent) > 12 && !SemanticProgressHasClock(icon),
                "减弱动效时 Agent 错误嘴型仍按实际状态更新，不沿用上次动画动作");
            icon.Text = "\uE73E"; ActionIconMotion.Play(icon, ActionIconAnimation.Tick);
            Assert(SemanticPixelDifference(errorAgent, SemanticIconPixels(icon)) > 12 && !SemanticProgressHasClock(icon),
                "减弱动效时等待授权图标准确显示完成笔划");
            icon.Shape = IconShape.Download; icon.Text = "\uE896";
            byte[] downloading = SemanticIconPixels(icon);
            icon.Text = "\uE769";
            byte[] pausedDownload = SemanticIconPixels(icon);
            icon.Text = "\uE73E";
            byte[] finishedDownload = SemanticIconPixels(icon);
            Assert(SemanticPixelDifference(downloading, pausedDownload) > 20
                && SemanticPixelDifference(pausedDownload, finishedDownload) > 20,
                "下载中、暂停和完成的静态图标准确区分箭头、双竖条与勾");
        }
        catch (Exception error) { completed++; failures.Add("Semantic icon probe failed: " + error); }
        finally
        {
            ActionIconMotion.SetKind(icon, ActionIconAnimation.None); ActionIconMotion.Stop(icon);
            panel.Children.Clear(); source.RootVisual = null;
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduced, saved);
            checks += completed;
        }
    }

    private static SemanticIcon NewSemanticProbe(IconShape shape, bool active) => new()
    {
        Shape = shape, Active = active, Level = .8, Width = 64, Height = 64,
        Foreground = new SolidColorBrush(Color.FromRgb(0x65, 0xDB, 0xFF)), IsHitTestVisible = false
    };

    private static bool SemanticProgressHasClock(SemanticIcon icon)
    {
        var values = icon.GetLocalValueEnumerator();
        while (values.MoveNext())
            if (DependencyPropertyHelper.GetValueSource(icon, values.Current.Property).IsAnimated) return true;
        return false;
    }
    private static byte[] SemanticIconPixels(FrameworkElement visual)
    {
        visual.UpdateLayout(); int width = (int)Math.Ceiling(visual.ActualWidth), height = (int)Math.Ceiling(visual.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0); return pixels;
    }
    private static int SemanticPixelDifference(byte[] first, byte[] second) => Enumerable.Range(0, first.Length / 4)
        .Count(index => Enumerable.Range(0, 4).Sum(channel => Math.Abs(first[index * 4 + channel] - second[index * 4 + channel])) > 16);
    private static int SemanticInkRuns(byte[] pixels, int row)
    {
        bool ink = false; int runs = 0;
        for (int column = 0; column < 64; column++)
        {
            bool next = pixels[(row * 64 + column) * 4 + 3] > 100;
            if (next && !ink) runs++; ink = next;
        }
        return runs;
    }
}
