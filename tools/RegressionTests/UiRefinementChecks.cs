using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Text.Json;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using Grid = System.Windows.Controls.Grid;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;

internal static partial class Program
{
    private static void UiRefinementChecks(alpha_island.MainWindow window, Grid root, string testRoot)
    {
        const string previews = "bin/UiRefinement167/previews";
        Directory.CreateDirectory(previews);
        MotionPolicy.Apply(true, false);
        PrivateCall(window, "SetState", IslandState.Card, true, "screen-tools");
        PumpAnimations(100); root.UpdateLayout();
        var card = AgentField<Grid>(window, "_screenToolsCard");
        var actions = card.Children.OfType<Grid>().Single();
        bool fits = true;
        foreach (var button in actions.Children.OfType<ToolActionButton>())
        {
            var surface = (Border)button.Content;
            var position = surface.TranslatePoint(new System.Windows.Point(), button);
            var panel = (Grid)surface.Child;
            var icon = (FrameworkElement)panel.Children[0];
            var iconPosition = icon.TranslatePoint(new System.Windows.Point(), surface);
            bool inside = position.Y >= -.5 && position.Y + surface.ActualHeight <= button.ActualHeight + .5
                && iconPosition.Y >= surface.Padding.Top + surface.BorderThickness.Top - .5
                && iconPosition.Y + icon.ActualHeight <= surface.ActualHeight - surface.Padding.Bottom - surface.BorderThickness.Bottom + .5;
            fits &= inside;
        }
        SaveThemePreview(window.IslandBody, previews + "/screen-tools.png");
        Check(fits, "屏幕工具内容完整位于各自按钮边界内，紧凑行不向上下溢出");
        var footer = AgentField<TextBlock>(window, "_externalTranslationHint");
        var footerPosition = footer.TranslatePoint(new Point(), card);
        Check(footerPosition.Y + footer.ActualHeight <= card.ActualHeight + .5, "增加卡片侧边留白后，底部说明仍然完整显示");

        string path = Path.Combine(testRoot, "layout-clipboard.json");
        File.WriteAllText(path, JsonSerializer.Serialize(Enumerable.Range(1, 12).Select(i => new ClipboardEntry("布局检查记录 " + i, DateTime.Now.AddMinutes(-i)))));
        using var hotkeys = new HotkeyService(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        using var clipboard = new ClipboardService(System.Windows.Threading.Dispatcher.CurrentDispatcher, hotkeys, () => 1, () => "", path, _ => { });
        PrivateCall(clipboard, "LoadHistory"); SetAgentField(window, "_clipboard", clipboard);
        PrivateCall(window, "SetState", IslandState.Card, true, "clipboard");
        PrivateCall(window, "SyncClipboardHistoryList"); PumpAnimations(50); root.UpdateLayout();
        var scroll = window.ClipboardHistoryScroll;
        var vertical = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
        var track = (Track)vertical.Template.FindName("PART_Track", vertical);
        double offset = scroll.VerticalOffset;
        track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        track.Thumb.RaiseEvent(new DragDeltaEventArgs(0, 24) { RoutedEvent = Thumb.DragDeltaEvent });
        track.Thumb.RaiseEvent(new DragCompletedEventArgs(0, 24, false) { RoutedEvent = Thumb.DragCompletedEvent });
        PumpAnimations(80); root.UpdateLayout();
        Check(scroll.VerticalOffset > offset && AgentField<IslandState>(window, "_state") == IslandState.Card,
            "美化后的滚动条仍可拖动列表，卡片保持展开");
        scroll.ScrollToHome(); root.UpdateLayout();
        var first = (ContentPresenter)window.ClipboardHistoryList.ItemContainerGenerator.ContainerFromIndex(0);
        var item = Descendants<Border>(first).First(border => border.CornerRadius.TopLeft == 8);
        var itemOrigin = item.TranslatePoint(new Point(), scroll);
        Check(itemOrigin.X >= 1 && itemOrigin.Y >= 2 && itemOrigin.X + item.ActualWidth <= scroll.ViewportWidth - 2,
            "剪贴板记录与滚动区域边缘留有安全间距");
        SaveThemePreview(window.IslandBody, previews + "/clipboard.png");
        SetAgentField(window, "_clipboard", null);
        GreetingPreviewLayoutChecks(window, testRoot, previews);
        GreetingBoundsChecks(window, root, previews);
    }

    private static void GreetingPreviewLayoutChecks(alpha_island.MainWindow main, string testRoot, string previews)
    {
        var options = new IslandOptions { GreetingEnabled = false, GreetingSeconds = 1.5 };
        GreetingPresetCatalog.Select(options, GreetingPresetCatalog.Amiya);
        SettingsRegistry.Write(options, "GreetingSoundEnabled", false);
        var settings = new alpha_island.Settings.SettingsWindow(false, greetingAssetsDirectory: Path.Combine(testRoot, "welcome-assets"));
        try
        {
            settings.LoadFrom(options);
            int changes = 0, requests = 0;
            settings.OptionsChanged += (_, _) => changes++;
            settings.GreetingPreviewRequested += (_, _) => { requests++; PrivateCall(main, "PreviewStartupGreeting"); };
            var preview = AgentField<Image>(settings, "_greetingImagePreview");
            var panel = (Grid)settings.Content; settings.Content = null; panel.Resources = settings.Resources;
            using var host = new HwndSource(new HwndSourceParameters("Welcome layout checks")
            { Width = 1200, Height = 900, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
            host.RootVisual = panel; panel.Height = 760;
            PrivateCall(settings, "SelectPageByTag", "notifications");
            var test = (Button)settings.FindName("GreetingImageTest");
            FrameworkElement[] controls = [preview, (Button)settings.FindName("GreetingImageChoose"), test, (Button)settings.FindName("GreetingImageReset")];
            foreach (double width in new[] { 780d, 880d, 1100d })
            {
                panel.Width = width; panel.UpdateLayout(); test.BringIntoView(); PumpAnimations(30); panel.UpdateLayout();
                var row = (Grid)((StackPanel)((Grid)preview.Parent).Parent).Parent;
                bool inside = controls.All(control =>
                {
                    var origin = control.TranslatePoint(new Point(), row);
                    return origin.X >= -.5 && origin.Y >= -.5 && origin.X + control.ActualWidth <= row.ActualWidth + .5
                        && origin.Y + control.ActualHeight <= row.ActualHeight + .5;
                });
                Check(inside, $"设置宽度 {width:0} 时欢迎预览和操作按钮完整位于行边界内");
                SaveThemePreview((Border)row.Parent, previews + $"/welcome-settings-{width:0}.png");
            }
            var image = preview.Source;
            PumpAnimations(500);
            Check(preview.ActualWidth >= 64 && preview.ActualHeight >= 64 && ReferenceEquals(image, preview.Source),
                "设置预览放大后保持静态，不随时间切换帧");
            Check(AgentField<GreetingGifFrame[]>(settings, "_greetingPreviewFrames").Length == 1,
                "设置静态预览只保留一帧，不常驻完整动画");
            Check(!AgentField<WelcomeLogoIcon>(settings, "_greetingLogoPreview").AnimationEnabled,
                "默认项目 Logo的设置预览也保持静态");

            var live = AgentField<IslandOptions>(main, "_options"); live.CopyFrom(options);
            string before = JsonSerializer.Serialize(live);
            var startup = new TaskCompletionSource<bool>(); SetAgentField(main, "_greetingFinishedTcs", startup);
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            test.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(requests == 1 && changes == 0 && !live.GreetingEnabled && AgentField<bool>(main, "_greetingPreviewActive"),
                "关闭启动欢迎时仍可临时测试，不触发保存或打开启动开关");
            var frames = AgentField<GreetingGifFrame[]>(main, "_greetingFrames");
            Check(frames.Length > 1 || MotionPolicy.InstantMorph, "临时测试在实际欢迎岛中播放当前动画");
            Check(!startup.Task.IsCompleted, "临时测试不会完成或替代真实启动生命周期");
            var timer = AgentField<System.Windows.Threading.DispatcherTimer>(main, "_greetingTimer");
            Check(timer.Interval.TotalMilliseconds >= frames.Sum(frame => frame.Delay.TotalMilliseconds),
                "临时测试给整段动画留出播放时间");
            for (int i = 0; i < frames.Length + 2; i++) PrivateCall(main, "OnGreetingImageTick", null, EventArgs.Empty);
            var last = main.GreetingCustomImage.Source; PumpAnimations(100);
            Check(ReferenceEquals(last, main.GreetingCustomImage.Source) && AgentField<object?>(main, "_greetingImageTimer") == null,
                "临时动画播放一次后停在末帧并释放逐帧定时器");
            PrivateCall(main, "DismissGreeting");
            Check(!AgentField<bool>(main, "_greetingPreviewActive") && AgentField<object?>(main, "_greetingTimer") == null
                && AgentField<GreetingGifFrame[]>(main, "_greetingFrames").Length == 1, "测试结束收回欢迎岛并释放完整动画帧");
            Check(!startup.Task.IsCompleted && JsonSerializer.Serialize(live) == before, "临时测试结束后设置和真实启动状态保持不变");
            test.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(AgentField<int>(main, "_greetingFrameIndex") == 0 && requests == 2, "再次临时测试从首帧重新开始");
            PrivateCall(main, "DismissGreeting");
            live.GreetingEnabled = true; PrivateCall(main, "ShowStartupGreeting"); PrivateCall(main, "DismissGreeting");
            Check(startup.Task.IsCompletedSuccessfully, "临时测试之后真实启动欢迎仍能正常完成");
            host.RootVisual = null;
        }
        finally { settings.ForceClose = true; settings.Close(); MotionPolicy.Apply(true, false); }
    }
}
