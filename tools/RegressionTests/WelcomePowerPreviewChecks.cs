using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Services;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;

internal static partial class Program
{
    private static void WelcomePowerPreviewChecks(alpha_island.MainWindow window, Grid root)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        var animator = AgentField<IslandAnimator>(window, "_animator");
        int tier = MotionPolicy.RenderTier;
        bool reduce = MotionPolicy.ReduceMotion, save = MotionPolicy.PowerSave;
        var output = Path.GetFullPath("bin/welcome-power-previews"); Directory.CreateDirectory(output);
        RenderTargetBitmap Render(string name)
        {
            root.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var origin = window.IslandBody.TransformToAncestor(root).Transform(new Point(0, 0));
            var crop = new CroppedBitmap(bitmap, new Int32Rect((int)Math.Round(origin.X), (int)Math.Round(origin.Y),
                (int)Math.Ceiling(window.IslandBody.ActualWidth), (int)Math.Ceiling(window.IslandBody.ActualHeight)));
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(crop));
            using var stream = File.Create(Path.Combine(output, name + ".png")); png.Save(stream);
            return bitmap;
        }
        try
        {
            layers.HideAll(); options.GreetingEnabled = true; options.GreetingSeconds = 10;
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            PrivateCall(window, "SetState", IslandState.Bar, true);
            PrivateCall(window, "ShowStartupGreeting");
            PumpAnimations(500); root.UpdateLayout();
            Check(window.GreetingLayer.Visibility == Visibility.Visible && !animator.IsRunning,
                "启动问候完成展开后显示项目 Logo");
            var logo = window.GreetingIcon;
            Check(DependencyPropertyHelper.GetValueSource(logo, alpha_island.Controls.WelcomeLogoIcon.ProgressProperty).IsAnimated,
                "问候使用有限的入场、飞离与返程动画");
            PumpAnimations(450); Render("welcome-logo");
            Check(logo.ActualWidth == 32 && logo.ActualHeight == 32, "Logo 显示区不挤压欢迎文字布局");
            PumpAnimations((int)alpha_island.Controls.WelcomeLogoIcon.PlaybackDuration.TotalMilliseconds + 200);
            Check(!DependencyPropertyHelper.GetValueSource(logo, alpha_island.Controls.WelcomeLogoIcon.ProgressProperty).IsAnimated,
                "问候动画完成后释放时钟");
            PrivateCall(window, "DismissGreeting"); PumpAnimations(500);
            Check(!DependencyPropertyHelper.GetValueSource(logo, alpha_island.Controls.WelcomeLogoIcon.ProgressProperty).IsAnimated,
                "问候隐藏后不保留动画");

            options.BatteryNotifyEnabled = false; options.BatteryNotifySeconds = 2;
            var settings = new alpha_island.Settings.SettingsWindow(false);
            try
            {
                settings.LoadFrom(options);
                Check(settings.FindName("PreviewChargingButton") == null && settings.FindName("PreviewUnpluggedButton") == null,
                    "充放电测试按钮已从设置移除");
                int changed = 0; settings.OptionsChanged += (_, _) => changed++;
                var charging = new Button(); var unplugged = new Button();
                charging.Click += (_, _) => PrivateCall(window, "PreviewBatteryAnimation", BatteryPowerEvent.PluggedIn);
                unplugged.Click += (_, _) => PrivateCall(window, "PreviewBatteryAnimation", BatteryPowerEvent.Unplugged);
                PrivateCall(window, "SetState", IslandState.Bar, true);
                charging.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(animator.IsRunning && !AgentField<bool>(window.BatteryRipple, "_animating"),
                    "接通电源测试先展开岛，尚未提前消耗充电波纹动画");
                PumpAnimations(550);
                Check(!animator.IsRunning && layers.IsActive("battery") && AgentField<bool>(window.BatteryRipple, "_animating")
                    && AgentField<bool>(window.BatteryRipple, "_isCharging"), "展开完成后开始完整绿色充电动画");
                Check(window.BatteryStatusText.Text.Contains("测试") && window.BatteryBoltIcon.Visibility == Visibility.Visible,
                    "模拟充电明确标注测试并显示充电状态");
                Render("charging-preview");
                PumpAnimations(2200);
                Check(!AgentField<bool>(window.BatteryRipple, "_animating") && layers.IsActive("battery"),
                    "两秒停留设置下测试仍给完整动画留出时间，波纹随后结束");
                unplugged.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpAnimations(150);
                Check(AgentField<bool>(window.BatteryRipple, "_animating") && !AgentField<bool>(window.BatteryRipple, "_isCharging")
                    && window.BatteryBoltIcon.Visibility == Visibility.Collapsed, "拔出电源测试使用橙色断电动画");
                Render("unplugged-preview");
                charging.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                unplugged.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpAnimations(150);
                Check(!AgentField<bool>(window.BatteryRipple, "_isCharging") && AgentField<object?>(window, "_batteryAnimationTimer") == null,
                    "连续测试只播放最后一次动作，启动轮询及时停止");
                Check(changed == 0 && !options.BatteryNotifyEnabled && options.BatteryNotifySeconds == 2
                    && AgentField<BatteryService?>(window, "_battery") == null, "预览不修改选项、不启动电池服务、不生成真实电源事件");
                layers.Hide("battery"); PrivateCall(window, "SyncBatteryNotifyContent");
                PrivateCall(window, "SetState", IslandState.Bar, true); PumpAnimations(450);
                charging.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                layers.Hide("battery"); PrivateCall(window, "SyncBatteryNotifyContent"); PumpAnimations(100);
                Check(AgentField<object?>(window, "_batteryAnimationTimer") == null && !AgentField<bool>(window.BatteryRipple, "_animating"),
                    "动画启动前通知被取消时不遗留计时器或波纹");
            }
            finally { settings.ForceClose = true; settings.Close(); }
        }
        finally
        {
            window.BatteryRipple.Stop(); PrivateCall(window, "DismissGreeting"); layers.Hide("battery"); animator.Stop();
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduce, save);
        }
    }
}
