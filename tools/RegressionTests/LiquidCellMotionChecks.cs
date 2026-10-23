using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;

internal static partial class Program
{
    private static void LiquidCellMotionChecks(alpha_island.MainWindow window, Grid root)
    {
        bool reduce = MotionPolicy.ReduceMotion, power = MotionPolicy.PowerSave; int tier = MotionPolicy.RenderTier;
        var options = AgentField<IslandOptions>(window, "_options");
        var motions = AgentField<Dictionary<Border, (long Start, double Duration, LiquidMotionKind Kind)>>(window, "_liquidOrbMotions");
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            LiquidCellContourChecks();
            PrivateCall(window, "SetState", IslandState.Pill, false);
            AgentField<IslandAnimator>(window, "_animator").SetImmediate(new(344, 48, 12, new CornerRadius(24)));
            root.UpdateLayout();
            var orb = (Border)PrivateCall(window, "EnsureSatelliteOrb", "bluetooth")!;
            SatelliteOrbAnimator.Show(orb); root.UpdateLayout();
            var cold = (Border)PrivateCall(window, "EnsureSatelliteOrb", "downloads")!;
            SatelliteOrbAnimator.Show(cold); SatelliteOrbAnimator.SlideOut(cold, -150, -242);
            PrivateCall(window, "RenderLiquidBackdrop");
            Check(motions.ContainsKey(cold), "新小岛尚未完成布局时仍保留出芽租约，不提前取消形变");
            SatelliteOrbAnimator.Hide(cold, true); PrivateCall(window, "RenderLiquidBackdrop");
            AgentField<SatelliteRail>(window, "_satellites").Register("orb-bluetooth", SatelliteSide.Left, () => 180, () => 36, () => true);
            var translate = ((TransformGroup)orb.RenderTransform).Children.OfType<TranslateTransform>().Single();
            var contours = AgentField<Dictionary<Border, LiquidIslandShape>>(window, "_liquidContours");
            var main = contours[window.IslandBody];
            var originalFill = orb.Background; bool overlapMaterial = false;
            var frames = new List<BitmapSource>();
            var timings = new List<double>(); int deformed = 0, bridges = 0, interiorPaint = 0;
            var watch = Stopwatch.StartNew(); long lastCapture = -100;
            EventHandler render = (_, _) =>
            {
                root.UpdateLayout();
                var frameWatch = Stopwatch.StartNew(); PrivateCall(window, "RenderLiquidBackdrop"); timings.Add(frameWatch.Elapsed.TotalMilliseconds);
                if (main.HasContact && contours[orb].HasContact) deformed++;
                overlapMaterial |= orb.Background is DrawingBrush;
                var membrane = AgentField<LiquidBridgeAdorner>(window, "_liquidBridgeLayer");
                if (membrane.Surface != null && orb.IsVisible && orb.Opacity > .95)
                    foreach (double dx in new[] { -5d, 0d, 5d }) foreach (double dy in new[] { -5d, 0d, 5d })
                    {
                        var point = orb.TranslatePoint(new Point(orb.ActualWidth / 2 + dx, orb.ActualHeight / 2 + dy), window.IslandScene);
                        if (membrane.Surface.FillContains(point)) interiorPaint++;
                    }
                if (AgentField<LiquidBridgeAdorner>(window, "_liquidBridgeLayer").Bridges != null) bridges++;
                if (watch.ElapsedMilliseconds - lastCapture < 30) return;
                lastCapture = watch.ElapsedMilliseconds;
                var scene = new RenderTargetBitmap(1200, 340, 96, 96, PixelFormats.Pbgra32); scene.Render(root);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(69, 109, 164), Color.FromRgb(104, 75, 141), 18), null, new Rect(0, 0, 650, 124));
                    dc.PushTransform(new TranslateTransform(-285, 0));
                    dc.DrawImage(scene, new Rect(0, 0, 1200, 340)); dc.Pop();
                }
                var bitmap = new RenderTargetBitmap(650, 124, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); frames.Add(bitmap);
            };
            CompositionTarget.Rendering += render;
            try
            {
                PrivateCall(window, "AnimateOrbSlideOut", "bluetooth", orb);
                var initial = motions[orb];
                PrivateCall(window, "AnimateOrbTranslate", translate, -198d);
                Check(motions[orb] == initial && motions[orb].Kind == LiquidMotionKind.Split, "轨道刷新保留新小岛的完整出芽时间线");
                PumpAnimations(620);
                Check(deformed >= 3 && bridges > 0, "真实 WPF 入场帧同时变形主岛、小岛及连接颈");
                Check(interiorPaint == 0, "延伸膜不绘制到图标内部，不会遮挡卫星内容");
                Check(overlapMaterial && ReferenceEquals(originalFill, orb.Background), "连接时相交材质只绘制一次，分离后恢复原主题画刷");
                Check(orb.Child?.Opacity > .99 && !orb.Child.HasAnimatedProperties, "出芽内容显现完成后保持可读并清除时钟");
                Check(!main.HasContact && !contours[orb].HasContact && motions.Count == 0, "出芽停稳后清空两侧应变和帧租约");
                Check(Math.Abs(translate.X + 198) < .01 && !translate.HasAnimatedProperties, "出芽最终落位并摘除有限动画时钟");
                int beforeMerge = deformed;
                SatelliteOrbAnimator.Return(orb, -159); PumpAnimations(510);
                Check(deformed > beforeMerge + 2 && orb.Visibility == Visibility.Collapsed, "吸收过程也牵动主岛轮廓，完成后收起小岛");
                Check(!main.HasContact && AgentField<LiquidBridgeAdorner>(window, "_liquidBridgeLayer").Surface == null, "吸收结束不残留薄膜、轮廓或细线");
            }
            finally { CompositionTarget.Rendering -= render; }
            string folder = Path.GetFullPath("bin/liquid-cell-previews"); Directory.CreateDirectory(folder);
            foreach (var stale in Directory.EnumerateFiles(folder, "frame-*.png")) File.Delete(stale);
            for (int i = 0; i < frames.Count; i++)
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frames[i]));
                using var file = File.Create(Path.Combine(folder, $"frame-{i:000}.png")); encoder.Save(file);
            }
            Console.WriteLine($"Actual WPF preview: {folder}, {frames.Count} frames; contour frame median={timings.Order().ElementAt(timings.Count / 2):F2} ms, max={timings.Max():F2} ms");
            SatelliteOrbAnimator.Show(orb); SatelliteOrbAnimator.SlideOut(orb, -164, -198); PumpAnimations(80);
            SatelliteOrbAnimator.Return(orb, -159); PumpAnimations(40);
            SatelliteOrbAnimator.Show(orb); SatelliteOrbAnimator.SlideOut(orb, translate.X, -198); PumpAnimations(520);
            Check(orb.Visibility == Visibility.Visible && orb.IsHitTestVisible && orb.Opacity > .99, "合并中再次出现不会被旧回调吞掉或丢失点击");
            SatelliteOrbAnimator.Hide(orb, true); PrivateCall(window, "RenderLiquidBackdrop");
            SatelliteOrbAnimator.Show(orb); SatelliteOrbAnimator.SlideOut(orb, -164, -198); PumpAnimations(80);
            MotionPolicy.Apply(true, false);
            PrivateCall(window, "RenderLiquidBackdrop");
            Check(Math.Abs(translate.X + 198) < .01 && !translate.HasAnimatedProperties && motions.Count == 0 && !main.HasContact, "途中切换减弱动效会完成位移并清理膜形变和动画时钟");
            SatelliteOrbAnimator.Show(orb); SatelliteOrbAnimator.SlideOut(orb, -164, -198); PrivateCall(window, "RenderLiquidBackdrop");
            Check(!translate.HasAnimatedProperties && !main.HasContact && !contours[orb].HasContact && motions.Count == 0, "减弱动效直接落位，不创建形变或常驻帧循环");
            SatelliteOrbAnimator.Hide(orb, true);
            MotionPolicy.Apply(false, true);
            SatelliteOrbAnimator.Show(orb); SatelliteOrbAnimator.SlideOut(orb, -164, -198); PrivateCall(window, "RenderLiquidBackdrop");
            Check(!translate.HasAnimatedProperties && !main.HasContact && motions.Count == 0, "省电模式同样跳过薄膜与逐帧形变");
            SatelliteOrbAnimator.Hide(orb, true);
        }
        finally { MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduce, power); }
    }

    private static void LiquidCellContourChecks()
    {
        foreach (double distance in new[] { 34d, 48d, 140d })
        {
            var timeline = SatelliteOrbAnimator.CreateSplitMove(0, -distance);
            var clock = (System.Windows.Media.Animation.AnimationClock)timeline.CreateClock(true); clock.Controller!.Begin();
            double At(double phase)
            {
                clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(LiquidCellMotion.SplitDuration.TotalMilliseconds * phase), System.Windows.Media.Animation.TimeSeekOrigin.BeginTime);
                return timeline.GetCurrentValue(0, -distance, clock);
            }
            const double epsilon = .002;
            double before = (At(.29) - At(.29 - epsilon)) / epsilon;
            double after = (At(.29 + epsilon) - At(.29)) / epsilon;
            Check(Math.Abs(before) > 1 && Math.Abs(before - after) / Math.Abs(before) < .12, "出芽关键点两侧的实际 WPF 位移速度连续，行程 " + distance);
            clock.Controller.Remove();
        }
        var left = new Rect(20, 20, 36, 36); var right = new Rect(56, 14, 344, 48);
        var sample = LiquidCellMotion.Evaluate(left, right, .35, LiquidMotionKind.Split);
        Check(sample.Left.Active && sample.Right.Active && sample.Neck != null, "出芽是双侧膜应变，含宽颈而非独立细线");
        var a = new LiquidIslandShape(); a.Update(new(36, 36, 0, new CornerRadius(18)));
        var b = new LiquidIslandShape(); b.Update(new(344, 48, 0, new CornerRadius(24)));
        var stable = b.Geometry;
        a.SetContact(right: sample.Left.InLocal(left, 36, 36)); b.SetContact(sample.Right.InLocal(right, 344, 48));
        Check(a.Geometry.Bounds.Right > 39 && b.Geometry.Bounds.Left < -3, "两岛接触尖端都超出静止边界，存在可见鼓包");
        Check(b.Geometry.FillContains(new Point(180, 1)) && b.Geometry.FillContains(new Point(342, 24)), "主岛形变局限在接触肩部，正文和远侧边缘保持稳定");
        Check(a.Geometry.GetArea() > 36 * 36 * .6 && b.Geometry.GetArea() > 344 * 48 * .85, "膜拉伸保持实体面积，不挤扁为线条");
        for (int i = 0; i <= 100; i++)
        {
            double p = i / 100d;
            var x = LiquidCellMotion.Evaluate(new Rect(20 - 26 * p, 20, 36, 36), right, p, LiquidMotionKind.Split);
            a.SetContact(right: x.Left.InLocal(left, 36, 36)); b.SetContact(x.Right.InLocal(right, 344, 48));
            Check(double.IsFinite(a.Geometry.GetArea()) && a.Geometry.GetArea() > 0 && double.IsFinite(b.Geometry.GetArea()) && b.Geometry.GetArea() > 0, "分离全过程路径有限且闭合 " + i);
        }
        Check(!a.IsDeformed && !b.IsDeformed && ReferenceEquals(stable, b.Geometry), "首尾回到同一静止轮廓对象，裁剪引用不失效");
        Check(LiquidCellMotion.Evaluate(left, right, double.NaN, LiquidMotionKind.Split) == default && LiquidCellMotion.Evaluate(left, new Rect(160, 14, 344, 48), .3, LiquidMotionKind.Split) == default, "无效进度及远处小岛不产生连线或形变");
        Check(LiquidCellMotion.Evaluate(left, new Rect(64, 14, 344, 48), .72, LiquidMotionKind.Split).Left.Pull < 0, "断颈后的轮廓存在有阻尼的反向回弹，不只是位移缓动");
        var tall = new Rect(56, 14, 420, 300);
        var card = LiquidCellMotion.Evaluate(left, tall, .4, LiquidMotionKind.Merge);
        Check(card.Right.Active && Math.Abs(card.Right.CenterY - 38) < .01, "展开卡片按小岛所在高度吸收，连接不会飞向卡片中心");
    }
}