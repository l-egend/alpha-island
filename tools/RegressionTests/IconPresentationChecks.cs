using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Animation;

internal static partial class Program
{
    private static void IconPresentationUiChecks(alpha_island.MainWindow window, Grid root)
    {
        bool reduced = MotionPolicy.ReduceMotion, saved = MotionPolicy.PowerSave;
        int tier = MotionPolicy.RenderTier;
        var panel = new Grid { Width = 80, Height = 80, Opacity = 0,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var icon = new alpha_island.Controls.SemanticIcon { Width = 32, Height = 32, Shape = alpha_island.Controls.IconShape.Bluetooth,
            Foreground = System.Windows.Media.Brushes.Cyan };
        panel.Children.Add(icon); root.Children.Add(panel); root.UpdateLayout(); PumpAnimations(20);
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            ActionIconMotion.SetKind(icon, ActionIconAnimation.Connect); PumpAnimations(80);
            Check(icon.IsVisible && !ActionIconMotion.GetIsAnimating(icon),
                "父级透明但子级 IsVisible=true 时，图标不提前消耗短动画");
            PumpAnimations(800); panel.Opacity = 1; root.UpdateLayout(); PumpAnimations(200);
            var originalTransform = icon.RenderTransform;
            Check(ActionIconMotion.GetIsAnimating(icon) && icon.HasAnimatedProperties
                && icon.TransitionProgress > 0 && icon.TransitionProgress < 1,
                "父级恢复显示时内部连接笔划过渡才开始，打开卡片不会错过动效");
            PumpAnimations(720);
            Check(ActionIconMotion.GetIsAnimating(icon), "图标显示近一秒后仍有短动画，便于看清动作");
            PumpAnimations(900);
            Check(!ActionIconMotion.GetIsAnimating(icon) && !icon.HasAnimatedProperties && ReferenceEquals(originalTransform, icon.RenderTransform),
                "延长后的动画仍然有限结束并释放全部自有时钟");
            panel.Opacity = 0; root.UpdateLayout(); PumpAnimations(50);
            panel.Opacity = 1; root.UpdateLayout(); PumpAnimations(100);
            Check(ActionIconMotion.GetIsAnimating(icon), "常驻卡片只改变透明度再次打开，也会显示一次图标动作");
            panel.Opacity = 0; root.UpdateLayout(); PumpAnimations(30);
            Check(!ActionIconMotion.GetIsAnimating(icon), "父级透明隐藏也立即停止图标动画");
        }
        finally
        {
            ActionIconMotion.SetKind(icon, ActionIconAnimation.None); root.Children.Remove(panel);
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduced, saved);
        }
    }
}
