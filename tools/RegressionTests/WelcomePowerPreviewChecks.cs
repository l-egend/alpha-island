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
                "启动问候完成展开后显示兔子出框图标");
            var rabbit = window.GreetingIcon;
            var leftEar = (RotateTransform)rabbit.FindName("LeftEarRotation");
            Check(leftEar.HasAnimatedProperties, "问候使用有限的耳朵过渡动画");
            PumpAnimations(1050);
            Check(!leftEar.HasAnimatedProperties, "问候动画完成后释放时钟");
            var bounds = VisualTreeHelper.GetDescendantBounds(rabbit);
            Check(bounds.Top < -5 && rabbit.ActualHeight == 32 && !rabbit.ClipToBounds,
                "兔子耳朵真实超出32像素圆框，不挤压问候文字布局");
            var bitmap = Render("welcome-rabbit");
            var rabbitOrigin = rabbit.TransformToAncestor(root).Transform(new Point(0, 0));
            int width = bitmap.PixelWidth, stride = width * 4;
            var pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0);
            int earPixels = 0;
            for (int y = (int)rabbitOrigin.Y - 7; y < (int)rabbitOrigin.Y; y++)
                for (int x = (int)rabbitOrigin.X; x < (int)rabbitOrigin.X + 32; x++)
                {
                    if (y < 0 || x < 0 || x >= width) continue;
                    int offset = y * stride + x * 4;
                    if (pixels[offset] > 180 && pixels[offset + 1] > 180 && pixels[offset + 2] > 180) earPixels++;
                }
            Check(earPixels > 12, $"实际岛体裁切后仍能看到圆框上方的耳朵（{earPixels}像素）");
            PrivateCall(window, "DismissGreeting"); PumpAnimations(500);
            Check(!leftEar.HasAnimatedProperties, "问候隐藏后不保留动画");

            options.BatteryNotifyEnabled = false; options.BatteryNotifySeconds = 2;
            var settings = new alpha_island.Settings.SettingsWindow(false);
            try
            {
                settings.LoadFrom(options);
                Check(settings.FindName("BatteryRippleToggle") == null, "移除充放电波纹开关，测试入口继续保留");
                int changed = 0; settings.OptionsChanged += (_, _) => changed++;
                settings.Left = -32000; settings.Top = -32000; settings.Width = settings.MinWidth;
                settings.ShowInForeground(requestFocus: false);
                PrivateCall(settings, "SelectPageByTag", "system"); PumpAnimations(300); settings.UpdateLayout();
                var row = (Grid)settings.FindName("BatteryAnimationPreviewRow");
                var lastButton = (Button)settings.FindName("PreviewUnpluggedButton");
                var buttonEnd = lastButton.TranslatePoint(new Point(lastButton.ActualWidth, 0), row);
                Check(row.ActualHeight >= 50 && lastButton.ActualWidth >= 64 && buttonEnd.X <= row.ActualWidth,
                    "最窄设置窗口中两个测试按钮仍完整显示");
                row.BringIntoView(); PumpAnimations(100); settings.UpdateLayout();
                var settingsRoot = (Grid)settings.Content;
                var fullSettings = new RenderTargetBitmap((int)settingsRoot.ActualWidth, (int)settingsRoot.ActualHeight,96,96,PixelFormats.Pbgra32);
                fullSettings.Render(settingsRoot);
                var rowOrigin = row.TransformToAncestor(settingsRoot).Transform(new Point(0,0));
                int rowX = Math.Max(0,(int)Math.Round(rowOrigin.X)), rowY = Math.Max(0,(int)Math.Round(rowOrigin.Y));
                int rowWidth = Math.Min((int)Math.Floor(row.ActualWidth),fullSettings.PixelWidth-rowX);
                int rowHeight = Math.Min((int)Math.Floor(row.ActualHeight),fullSettings.PixelHeight-rowY);
                Check(rowHeight >= row.ActualHeight-2 && rowWidth >= row.ActualWidth-2,
                    "滚动到测试选项后整行内容在设置窗口内可见");
                var rowBitmap = new CroppedBitmap(fullSettings, new Int32Rect(rowX,rowY,rowWidth,rowHeight));
                var rowPng = new PngBitmapEncoder(); rowPng.Frames.Add(BitmapFrame.Create(rowBitmap));
                using (var stream = File.Create(Path.Combine(output, "settings-preview-row.png"))) rowPng.Save(stream);
                settings.BatteryAnimationPreviewRequested += (_, kind) => PrivateCall(window, "PreviewBatteryAnimation", kind);
                var charging = (Button)settings.FindName("PreviewChargingButton");
                var unplugged = (Button)settings.FindName("PreviewUnpluggedButton");
                Check(charging.IsEnabled && unplugged.IsEnabled, "临时测试入口在关闭日常电池通知时仍可使用");
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
