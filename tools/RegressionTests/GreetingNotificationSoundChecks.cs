using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.Wave;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;
using CheckBox = System.Windows.Controls.CheckBox;

internal static partial class Program
{
    private static void GreetingNotificationSoundChecks(alpha_island.MainWindow main, Grid root, string testRoot)
    {
        using (var sound = new WaveFileReader(alpha_island.MainWindow.OpenNotificationSound()))
            Check(sound.TotalTime.TotalSeconds is > .45 and < .55 && sound.WaveFormat.SampleRate == 48000,
                "内置通知提示音可独立读取，时长约半秒");
        GreetingWaveOutput? output = null;
        using (var audio = new GreetingAudioPlayer(main.Dispatcher, () => output = new GreetingWaveOutput(), alpha_island.MainWindow.OpenNotificationSound))
        {
            audio.Play("", 25);
            Check(audio.IsPlaying && output?.Volume == .25f, "通知试听使用独立音量且可以播放内置短音");
            output!.Finish(); PumpAnimations(20);
            Check(output.Disposed && AgentField<object?>(audio, "_stream") == null && AgentField<object?>(audio, "_deadline") == null,
                "通知提示音自然播放完后释放设备、音频流和计时器");
        }
        var policy = new NotificationSoundPolicy();
        Check(policy.TryAccept(IslandNotification.Seconds("bluetooth", 4), false, 1000), "新设备通知允许播放提示音");
        Check(!policy.TryAccept(IslandNotification.Seconds("battery", 4), false, 1500), "连续通知短时间合并声音，不密集叠加");
        Check(policy.TryAccept(IslandNotification.Seconds("battery", 4), true, 2300), "新的限时事件可以再次播放提示音");
        foreach (string key in new[] { "greeting", "volume", "brightness", "clipboard", "netspeed", "status", "agentstatus:fixture", "charging" })
            Check(!policy.TryAccept(IslandNotification.Seconds(key, 4), false, 6000), key + " 不叠加通知提示音");
        Check(!policy.TryAccept(IslandNotification.Refreshing("download", 4), true, 6000)
            && !policy.TryAccept(IslandNotification.While("approval", () => true), true, 6000), "后台续期和已有持续提示不重复发声");
        Check(!policy.TryAccept(IslandNotification.Seconds("system", 4, opensIsland: false), false, 6000), "不弹出的后台状态保持安静");
        var options = new IslandOptions();
        Check(NotificationSoundPolicy.Enabled(options, false, false), "通知提示音默认启用，供实际体验");
        Check(!NotificationSoundPolicy.Enabled(options, true, false) && !NotificationSoundPolicy.Enabled(options, false, true), "锁屏和游戏免打扰抑制提示音");
        SettingsRegistry.Write(options, "NotificationQuietMode", true);
        Check(!NotificationSoundPolicy.Enabled(options, false, false), "手动静默模式不播放通知音");
        SettingsRegistry.Write(options, "NotificationQuietMode", false); SettingsRegistry.Write(options, "NotificationSoundEnabled", false);
        Check(!NotificationSoundPolicy.Enabled(options, false, false), "关闭提示音后保持静音");
        SettingsRegistry.Write(options, "NotificationSoundEnabled", true); SettingsRegistry.Write(options, "NotificationSoundVolume", 0);
        Check(!NotificationSoundPolicy.Enabled(options, false, false), "通知音量设为零后保持静音");
        NotificationSoundSettingsChecks(options, testRoot);

        var live = AgentField<IslandOptions>(main, "_options");
        bool reduce = MotionPolicy.ReduceMotion, save = MotionPolicy.PowerSave; int tier = MotionPolicy.RenderTier;
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            GreetingPresetCatalog.Select(live, GreetingPresetCatalog.Nailong); live.GreetingEnabled = true; live.GreetingSeconds = 8;
            AgentField<IslandLayerHost>(main, "_layers").HideAll();
            PrivateCall(main, "ApplyGreetingMedia"); PrivateCall(main, "SetState", IslandState.Bar, true);
            PrivateCall(main, "ShowStartupGreeting"); PumpAnimations(500); root.UpdateLayout();
            var portrait = AgentField<GreetingPortraitAdorner?>(main, "_greetingPortrait");
            Check(portrait != null && main.GreetingCustomImage.Opacity == 0 && main.GreetingCustomImage.Width == 48,
                "奶龙欢迎形象通过独立出框层显示，并给文字保留间距");
            Rect bounds = VisualTreeHelper.GetDescendantBounds(portrait!);
            Check(bounds.Width == 64 && bounds.Height == 56 && bounds.Top == -16, "奶龙显示为 64 像素宽的上半身，保持原图比例");
            var picture = portrait!.TransformToAncestor(root).TransformBounds(bounds);
            var body = main.IslandBody.TransformToAncestor(root).TransformBounds(new Rect(main.IslandBody.RenderSize));
            var text = main.GreetingText.TransformToAncestor(root).TransformBounds(new Rect(main.GreetingText.RenderSize));
            Check(picture.Top >= 0 && picture.Top < body.Top - 3 && picture.Right <= text.Left - 2,
                "奶龙头部伸出岛体上沿，不越过窗口边界或遮挡欢迎文字");
            Directory.CreateDirectory("bin/GreetingNotification162/previews");
            SaveThemePreview(root, "bin/GreetingNotification162/previews/nailong-portrait-detail.png");
            var capture = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            capture.Render(root);
            int left = Math.Max(0, (int)body.Left - 16), top = Math.Max(0, (int)picture.Top - 4);
            var crop = new CroppedBitmap(capture, new Int32Rect(left, top, Math.Min(capture.PixelWidth - left, (int)body.Width + 32),
                Math.Min(capture.PixelHeight - top, (int)Math.Ceiling(body.Bottom) - top + 8)));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(crop));
            using (var file = File.Create("bin/GreetingNotification162/previews/nailong-portrait.png")) encoder.Save(file);
            main.GreetingLayer.Opacity = 0;
            Check(portrait.Visibility == Visibility.Collapsed, "欢迎层淡出时独立头像同步隐藏");
            main.GreetingLayer.Opacity = 1;
            PrivateCall(main, "DismissGreeting");
            Check(AgentField<object?>(main, "_greetingPortrait") == null && AgentField<GreetingGifFrame[]>(main, "_greetingFrames").Length == 1,
                "欢迎结束后清理出框层并只保留动画首帧");
            GreetingPresetCatalog.Select(live, GreetingPresetCatalog.Rabbit); PrivateCall(main, "ApplyGreetingMedia");
            Check(main.GreetingIcon.Visibility == Visibility.Visible && main.GreetingCustomImage.Width == 32,
                "切回兔子保持原来的显示尺寸，不沿用奶龙裁切");
        }
        finally { PrivateCall(main, "DismissGreeting"); MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduce, save); }
    }

    private static void NotificationSoundSettingsChecks(IslandOptions options, string testRoot)
    {
        string source = Path.Combine(testRoot, "notification-source.wav");
        using (var sound = alpha_island.MainWindow.OpenNotificationSound())
        using (var file = File.Create(source)) sound.CopyTo(file);
        var settings = new alpha_island.Settings.SettingsWindow(false, greetingAssetsDirectory: Path.Combine(testRoot, "sound-assets")) { ForceClose = true };
        try
        {
            settings.LoadFrom(options);
            Check(new[] { "NotificationSoundToggle", "NotificationSoundVolumeSlider", "NotificationSoundChoose", "NotificationSoundPreview", "NotificationSoundReset" }
                .All(name => settings.FindName(name) is FrameworkElement), "设置提供通知音开关、音量、选择、试听和恢复默认");
            var current = AgentField<IslandOptions>(settings, "_options");
            PrivateCall(settings, "ImportNotificationSound", source);
            string imported = SettingsRegistry.GetText(current, "NotificationSoundPath", "");
            Check(imported != source && File.Exists(imported), "自选通知音保存独立副本");
            File.Move(source, source + ".moved");
            Check(File.Exists(imported), "移动原始文件不影响已经导入的通知音");
            ((Slider)settings.FindName("NotificationSoundVolumeSlider")).Value = 42;
            Check(SettingsRegistry.GetInt(current, "NotificationSoundVolume", -1) == 42, "通知音量调节实时保存");
            ((CheckBox)settings.FindName("NotificationSoundToggle")).IsChecked = false;
            Check(!((Slider)settings.FindName("NotificationSoundVolumeSlider")).IsEnabled
                && !((FrameworkElement)settings.FindName("NotificationSoundPreview")).IsEnabled, "关闭通知音同步禁用音量和试听");
            PrivateCall(settings, "ResetNotificationSound"); settings.ReportSettingsSaved(false);
            Check(File.Exists(imported), "保存失败时保留旧导入音效，避免丢失配置引用");
            settings.ReportSettingsSaved(true);
            Check(SettingsRegistry.GetText(current, "NotificationSoundPath", "x") == "" && !File.Exists(imported) && File.Exists(source + ".moved"),
                "恢复默认成功保存后清理旧副本，保留原始音效文件");
        }
        finally { settings.Close(); }
    }
}
