using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using WpfPath = System.Windows.Shapes.Path;
using Point = System.Windows.Point;

internal static partial class Program
{
    private static void ShortIconMotionChecks(alpha_island.MainWindow window, Grid root)
    {
        bool reduced = MotionPolicy.ReduceMotion, saved = MotionPolicy.PowerSave;
        int tier = MotionPolicy.RenderTier;
        var gallery = new Canvas { Width = 100, Height = 60, Background = System.Windows.Media.Brushes.Transparent,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var original = new RotateTransform(5, 16, 16);
        var icon = new alpha_island.Controls.SemanticIcon { Width = 32, Height = 32, Shape = alpha_island.Controls.IconShape.Bluetooth,
            Foreground = System.Windows.Media.Brushes.Cyan,
            RenderTransform = original, RenderTransformOrigin = new Point(.17, .29), IsHitTestVisible = false };
        Canvas.SetLeft(icon, 30); Canvas.SetTop(icon, 14); gallery.Children.Add(icon);
        root.Children.Add(gallery); root.UpdateLayout(); PumpAnimations(20);
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            var size = icon.RenderSize; var origin = icon.RenderTransformOrigin;
            var baseline = MotionIconPixels(gallery);
            var baselineHit = VisualTreeHelper.HitTest(gallery, new Point(46, 30))?.VisualHit;
            ActionIconMotion.SetKind(icon, ActionIconAnimation.Connect);
            PumpAnimations(130); root.UpdateLayout();
            double firstProgress = icon.TransitionProgress;
            Check(ActionIconMotion.GetIsAnimating(icon) && icon.HasAnimatedProperties && firstProgress > 0 && firstProgress < 1,
                "真实可见图标开始有限连接笔划过渡，内部进度实际发生变化");
            Check(!baseline.SequenceEqual(MotionIconPixels(gallery)), "短图标动画改变离屏 WPF 实际像素");
            Check(icon.RenderSize == size && icon.RenderTransformOrigin == origin && !icon.IsHitTestVisible
                && ReferenceEquals(baselineHit, VisualTreeHelper.HitTest(gallery, new Point(46, 30))?.VisualHit),
                "短动画保留占位、原变换原点和点击命中规则");
            ActionIconMotion.Play(icon); icon.Visibility = Visibility.Visible; root.UpdateLayout(); PumpAnimations(30);
            Check(icon.TransitionProgress >= firstProgress && ReferenceEquals(icon.RenderTransform, original), "正在播放时重复同步和持续可见不创建第二组动画时钟");
            PumpAnimations(1450);
            Check(!ActionIconMotion.GetIsAnimating(icon) && ReferenceEquals(icon.RenderTransform, original)
                && !icon.HasAnimatedProperties,
                "短动画完成后移除全部自有时钟，并恢复原静态变换");
            PumpAnimations(80);
            Check(!ActionIconMotion.GetIsAnimating(icon) && baseline.SequenceEqual(MotionIconPixels(gallery)),
                "图标持续可见时保持静态，不自行循环或重新播放");

            foreach (var cue in new[] { ActionIconAnimation.Pulse, ActionIconAnimation.Disconnect, ActionIconAnimation.Spin, ActionIconAnimation.Tick })
            {
                ActionIconMotion.Play(icon, cue); PumpAnimations(90);
                var channels = icon;
                Check(ActionIconMotion.GetIsAnimating(icon) && channels.HasAnimatedProperties,
                    cue + " 动画能用于真实动作变化");
                ActionIconMotion.Stop(icon);
                Check(!ActionIconMotion.GetIsAnimating(icon) && ReferenceEquals(icon.RenderTransform, original)
                    && !channels.HasAnimatedProperties, cue + " 显式停止立即移除时钟并恢复基线");
            }

            ActionIconMotion.Play(icon); PumpAnimations(50); var hiddenChannels = icon;
            icon.Visibility = Visibility.Collapsed;
            Check(!ActionIconMotion.GetIsAnimating(icon) && ReferenceEquals(icon.RenderTransform, original)
                && !hiddenChannels.HasAnimatedProperties, "图标隐藏立即停止全部动画时钟");
            icon.Visibility = Visibility.Visible; root.UpdateLayout(); PumpAnimations(30);
            Check(ActionIconMotion.GetIsAnimating(icon), "图标重新显示只播放一次有限动作提示");
            var unloadedChannels = icon;
            gallery.Children.Remove(icon); PumpAnimations(30);
            Check(!ActionIconMotion.GetIsAnimating(icon) && ReferenceEquals(icon.RenderTransform, original)
                && !unloadedChannels.HasAnimatedProperties, "图标卸载立即复位且停止自有时钟");
            gallery.Children.Add(icon); root.UpdateLayout(); PumpAnimations(30);

            MotionPolicy.Apply(true, false);
            Check(!ActionIconMotion.GetIsAnimating(icon) && ReferenceEquals(icon.RenderTransform, original),
                "减弱动效策略变化即时停止已在播放的图标");
            ActionIconMotion.Play(icon);
            Check(!ActionIconMotion.GetIsAnimating(icon), "减弱动效时显式动作请求也保持静态");
            MotionPolicy.Apply(false, false); ActionIconMotion.Play(icon); PumpAnimations(30);
            MotionPolicy.Apply(false, true);
            Check(!ActionIconMotion.GetIsAnimating(icon) && ReferenceEquals(icon.RenderTransform, original),
                "省电策略变化即时停止图标动画");
            ActionIconMotion.Play(icon);
            Check(!ActionIconMotion.GetIsAnimating(icon), "省电模式不启动图标动画");
            MotionPolicy.Apply(false, false); MotionPolicy.OverrideRenderTierForDebug(0);
            ActionIconMotion.Play(icon);
            Check(!ActionIconMotion.GetIsAnimating(icon), "软件渲染降级时图标保持静态");
            MotionPolicy.OverrideRenderTierForDebug(2);

            original.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(5, 12, TimeSpan.FromSeconds(2)) { FillBehavior = FillBehavior.Stop });
            ActionIconMotion.Play(icon); PumpAnimations(70); ActionIconMotion.Stop(icon);
            Check(original.HasAnimatedProperties && ReferenceEquals(icon.RenderTransform, original),
                "结束图标提示不移除调用方原变换内部的动画时钟");
            original.BeginAnimation(RotateTransform.AngleProperty, null);

            var model = new MotionIconTransformModel { Transform = original };
            BindingOperations.SetBinding(icon, UIElement.RenderTransformProperty,
                new System.Windows.Data.Binding(nameof(MotionIconTransformModel.Transform)) { Source = model });
            ActionIconMotion.Play(icon); PumpAnimations(30); ActionIconMotion.Stop(icon);
            Check(BindingOperations.IsDataBound(icon, UIElement.RenderTransformProperty)
                && ReferenceEquals(icon.RenderTransform, original), "图标动画前后保留原 RenderTransform 数据绑定");
            ActionIconMotion.Play(icon); PumpAnimations(30);
            var replacement = new TranslateTransform(2, 0); model.Transform = replacement;
            BindingOperations.GetBindingExpression(icon, UIElement.RenderTransformProperty)!.UpdateTarget();
            ActionIconMotion.Stop(icon);
            Check(BindingOperations.IsDataBound(icon, UIElement.RenderTransformProperty)
                && ReferenceEquals(icon.RenderTransform, replacement), "调用方更新绑定后停止动画不覆盖新的变换");
            BindingOperations.ClearBinding(icon, UIElement.RenderTransformProperty); icon.RenderTransform = original;

            var objectAnimation = new ObjectAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(300) };
            objectAnimation.KeyFrames.Add(new DiscreteObjectKeyFrame(replacement, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            icon.BeginAnimation(UIElement.RenderTransformProperty, objectAnimation); PumpAnimations(30);
            ActionIconMotion.Play(icon);
            Check(ActionIconMotion.GetIsAnimating(icon)
                && DependencyPropertyHelper.GetValueSource(icon, UIElement.RenderTransformProperty).IsAnimated,
                "内部笔划过渡与调用方变换时钟并存，不替换调用方动画");
            icon.BeginAnimation(UIElement.RenderTransformProperty, null);

            ActionIconMotion.Play(icon); PumpAnimations(30);
            ActionIconMotion.SetKind(icon, ActionIconAnimation.None);
            Check(!ActionIconMotion.GetIsAnimating(icon) && ReferenceEquals(icon.RenderTransform, original),
                "禁用附加行为即时停止并解除生命周期事件");
        }
        finally
        {
            ActionIconMotion.SetKind(icon, ActionIconAnimation.None); root.Children.Remove(gallery);
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduced, saved);
        }
    }

    private sealed class MotionIconTransformModel { public Transform Transform { get; set; } = Transform.Identity; }

    private static byte[] MotionIconPixels(FrameworkElement visual)
    {
        visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen()) dc.DrawRectangle(new VisualBrush(visual), null,
            new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
        bitmap.Render(drawing);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels;
    }
}
