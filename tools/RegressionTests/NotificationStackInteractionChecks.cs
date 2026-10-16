using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;

internal static partial class Program
{
    private static void NotificationStackInteractionChecks(DesktopNotificationStackCard stack, DesktopNotification noLogo, Grid root)
    {
        stack.ToggleButton.ApplyTemplate();
        var face = (Border)stack.ToggleButton.Parent;
        var surface = Descendants<Border>(stack.ToggleButton).Single(border => border.Name == "Surface");
        Check(surface.CornerRadius == face.CornerRadius, "通知堆选中背景与外卡片使用同一圆角");
        var original = surface.Background;
        try
        {
            surface.SetCurrentValue(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 255, 255, 255)));
            int width = (int)Math.Ceiling(stack.ToggleButton.ActualWidth), height = (int)Math.Ceiling(stack.ToggleButton.ActualHeight);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(stack.ToggleButton), null, new Rect(0, 0, width, height));
            bitmap.Render(visual); var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
            Check(new[] { (3, 3), (width - 4, 3), (3, height - 4), (width - 4, height - 4) }.All(point => pixels[(point.Item2 * width + point.Item1) * 4 + 3] < 5),
                "实际高亮渲染的四个外角透明，没有直角背景残留");
            SaveThemePreview(stack, "docs/screens/v109-notification-stack-selected.png");
        }
        finally { surface.SetCurrentValue(Border.BackgroundProperty, original); }
        var wechatLogo = DesktopNotificationIcons.GetLogo(noLogo);
        var qqLogo = DesktopNotificationIcons.GetLogo(stack.Group.Latest);
        Check(wechatLogo is { IsFrozen: true } && qqLogo is { IsFrozen: true } && !ReferenceEquals(wechatLogo, qqLogo),
            "无自带 Logo 的微信和 QQ 通知仍有各自清晰的应用图标");
        Check(ReferenceEquals(wechatLogo, DesktopNotificationIcons.GetLogo(noLogo with { Logo = new byte[] { 1, 2, 3 } })),
            "微信损坏图标也可回退，不会永久缓存为默认图标");
        var known = noLogo with { AppId = "Visual.Stack.Registered!App", AppName = "已注册应用", Logo = TestNotificationImage(System.Windows.Media.Colors.Cyan) };
        var validated = DesktopNotificationIcons.GetLogo(known);
        Check(validated != null && ReferenceEquals(validated, DesktopNotificationIcons.GetLogo(known with { Logo = null }))
            && ReferenceEquals(validated, DesktopNotificationIcons.GetLogo(known with { Logo = new byte[] { 1, 2, 3 } })),
            "相同应用新消息缺少或损坏图标时复用此前已验证的 Logo");
        Check(DesktopNotificationIcons.GetLogo(known with { AppId = "Visual.Stack.Other!App", Logo = null }) == null
            && DesktopNotificationIcons.GetLogo(noLogo with { Kind = DesktopEventKind.PowerConnected }) == null,
            "应用图标缓存不串到其他应用或电源事件");
        bool reduced = MotionPolicy.ReduceMotion, power = MotionPolicy.PowerSave; int tier = MotionPolicy.RenderTier;
        var content = AgentField<Border>(stack, "_expansion");
        List<(double Height, double Opacity)> Frames(bool expanded)
        {
            var frames = new List<(double, double)>();
            EventHandler handler = (_, _) => frames.Add((content.ActualHeight, content.Opacity));
            CompositionTarget.Rendering += handler;
            try { stack.SetExpanded(expanded); PumpAnimations(340); root.UpdateLayout(); }
            finally { CompositionTarget.Rendering -= handler; }
            return frames;
        }
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            var opening = Frames(true);
            Check(opening.Any(frame => frame.Height > 0 && frame.Opacity > .01 && frame.Opacity < .99),
                "真实绘制的展开中间帧逐步显现消息");
            Check(opening.Where(frame => frame.Opacity > .01 && frame.Opacity < .99).Select(frame => Math.Round(frame.Height)).Distinct().Count() >= 3,
                "长列表展开包含多个不同高度的可见中间帧");
            Check(content.Visibility == Visibility.Visible && double.IsNaN(content.Height) && content.Opacity == 1 && !content.HasAnimatedProperties,
                "展开完成恢复自然高度、完整不透明并清理时钟");
            var closing = Frames(false);
            Check(closing.Any(frame => frame.Height > 0 && frame.Opacity > .01 && frame.Opacity < .99),
                "真实绘制的收起中间帧逐步淡出消息");
            Check(content.Visibility == Visibility.Collapsed && content.Height == 0 && !content.HasAnimatedProperties,
                "收起完成隐藏内容，不留透明点击区或动画时钟");
            MotionPolicy.Apply(true, false); stack.SetExpanded(true);
            Check(content.Visibility == Visibility.Visible && double.IsNaN(content.Height) && !content.HasAnimatedProperties,
                "减弱动效仍可即时展开并正确显示消息");
            stack.SetExpanded(false);
        }
        finally
        {
            stack.SetExpanded(false, animate: false);
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduced, power);
        }
    }
}