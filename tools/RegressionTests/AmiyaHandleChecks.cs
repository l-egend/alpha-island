using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;
using NAudio.Wave;
using Color = System.Windows.Media.Color;
using Grid = System.Windows.Controls.Grid;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

internal static partial class Program
{
    private static void AmiyaHandleChecks(alpha_island.MainWindow main, Grid root, string testRoot)
    {
        string previews = "bin/AmiyaWelcome164/previews"; Directory.CreateDirectory(previews);
        var options = new IslandOptions().Normalized();
        SettingsRegistry.Write(options, "GreetingSoundVolume", 12);
        GreetingPresetCatalog.Select(options, GreetingPresetCatalog.Amiya);
        Check(GreetingPresetCatalog.GetId(options) == GreetingPresetCatalog.Amiya
            && GreetingPresetCatalog.ImageKey(options) == GreetingPresetCatalog.AmiyaMedia
            && GreetingPresetCatalog.SoundKey(options) == GreetingPresetCatalog.AmiyaMedia, "阿米娅内置组合绑定透明动画与问候语音");
        Check(SettingsRegistry.GetInt(options, "GreetingSoundVolume", 0) == 12, "切换阿米娅保留用户音量");
        using (var stream = GreetingPresetCatalog.OpenAmiyaSound())
        {
            Check(Convert.ToHexString(SHA256.HashData(stream)) == "C3BB5D7C766CFCC9CFD90ECB7D143503F64BA81F3C961A638D1278C6BC8F4117",
                "内嵌阿米娅语音与用户提供的问候 WAV 完全一致");
        }
        GreetingWaveOutput? voiceOutput = null;
        using (var voice = new GreetingAudioPlayer(main.Dispatcher, () => voiceOutput = new GreetingWaveOutput()))
        {
            voice.Play(GreetingPresetCatalog.AmiyaMedia, 12);
            var wave = AgentField<WaveStream>(voice, "_stream");
            Check(voice.IsPlaying && voiceOutput?.Volume == .12f && wave.TotalTime.TotalSeconds is > 1.94 and < 1.96
                && wave.WaveFormat.SampleRate == 44100 && wave.WaveFormat.Channels == 1,
                "阿米娅语音通过内置键播放约两秒原始音频，并保留音量");
            voiceOutput!.Finish(); PumpAnimations(20);
            Check(voiceOutput.Disposed && !voice.IsPlaying && AgentField<object?>(voice, "_stream") == null,
                "阿米娅语音结束后释放播放设备与音频流");
        }
        var frames = GreetingPresetCatalog.LoadFrames(GreetingPresetCatalog.AmiyaMedia);
        Check(frames.Length == 96 && frames.Sum(f => f.Delay.TotalMilliseconds) == 4000,
            "保留原视频稳定取景的四秒动作，以 24 帧播放");
        Check(frames.All(f => f.Image.IsFrozen && f.Image.PixelWidth == 128 && f.Image.PixelHeight == 128), "动画帧尺寸有界并冻结解码资源");
        int soft = 0, yellow = 0, minimum = int.MaxValue, maximum = 0; bool transparent = true;
        var fingerprints = new HashSet<string>();
        foreach (var frame in frames)
        {
            var bitmap = new FormatConvertedBitmap(frame.Image, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[128 * 128 * 4]; bitmap.CopyPixels(pixels, 512, 0);
            fingerprints.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels)));
            int opaque = 0;
            for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++)
            {
                int i = (y * 128 + x) * 4, a = pixels[i + 3], b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                if (a > 32) opaque++;
                if (a is > 0 and < 255) soft++;
                if (a > 32 && r > 160 && g > 150 && b < 120 && Math.Min(r, g) > b * 1.9) yellow++;
                if ((y == 0 || y >= 115 || x == 0 || x == 127) && a != 0) transparent = false;
            }
            minimum = Math.Min(minimum, opaque); maximum = Math.Max(maximum, opaque);
        }
        Check(transparent, "所有动画帧边缘和画布底部都是真实透明");
        Check(minimum > 1800 && maximum < 8000, $"逐帧检查人物未丢失或混入整块背景（pixels={minimum}..{maximum}）");
        Check(yellow == 0, $"黄色选中外框已从全部帧移除（remaining={yellow}）");
        Check(soft > 20000 && fingerprints.Count > 70, "透明边缘保留抗锯齿，眨眼与抬手动作保留原视频变化");
        Check(GreetingPresetCatalog.LoadFrames(GreetingPresetCatalog.AmiyaMedia, true).Length == 1, "后台仅加载阿米娅首帧");
        string audioFolder = Path.Combine(testRoot, "amiya-greeting-audio"); Directory.CreateDirectory(audioFolder);
        var settings = new alpha_island.Settings.SettingsWindow(false, greetingAssetsDirectory: audioFolder) { ForceClose = true };
        try
        {
            settings.LoadFrom(options);
            var combo = (ComboBox)settings.FindName("GreetingPresetCombo");
            Check(combo.Items.Count == GreetingPresetCatalog.Ids.Length && combo.SelectedIndex == Array.IndexOf(GreetingPresetCatalog.Ids, GreetingPresetCatalog.Amiya), "设置中保留阿米娅选项并正确回填");
            combo.SelectedIndex = 1; combo.SelectedIndex = 2;
            var saved = AgentField<IslandOptions>(settings, "_options");
            Check(GreetingPresetCatalog.ImageKey(saved) == GreetingPresetCatalog.AmiyaMedia && GreetingPresetCatalog.SoundKey(saved) == GreetingPresetCatalog.AmiyaMedia,
                "从奶龙切到阿米娅时同步切换为阿米娅问候语音");
            string sourceSound = Path.Combine(testRoot, "amiya-custom.wav");
            using (var source = typeof(GreetingPresetCatalog).Assembly.GetManifestResourceStream("AlphaIsland.StartupGentle.wav")!)
            using (var destination = File.Create(sourceSound)) source.CopyTo(destination);
            PrivateCall(settings, "ImportGreetingMedia", sourceSound, false);
            Check(GreetingPresetCatalog.GetId(saved) == GreetingPresetCatalog.Custom
                && GreetingPresetCatalog.ImageKey(saved) == GreetingPresetCatalog.AmiyaMedia && File.Exists(GreetingPresetCatalog.SoundKey(saved)),
                "以后单独导入新音效时保留阿米娅动画，不退回默认兔子");
        }
        finally { settings.Close(); }

        bool reduce = MotionPolicy.ReduceMotion, save = MotionPolicy.PowerSave; int tier = MotionPolicy.RenderTier;
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            var live = AgentField<IslandOptions>(main, "_options");
            GreetingPresetCatalog.Select(live, GreetingPresetCatalog.Amiya); live.GreetingEnabled = true; live.GreetingSeconds = 8;
            SettingsRegistry.Write(live, "GreetingSoundEnabled", false);
            AgentField<IslandLayerHost>(main, "_layers").HideAll();
            PrivateCall(main, "ApplyGreetingMedia"); PrivateCall(main, "SetState", IslandState.Bar, true);
            PrivateCall(main, "ShowStartupGreeting"); PumpAnimations(500); root.UpdateLayout();
            var portrait = AgentField<GreetingPortraitAdorner?>(main, "_greetingPortrait");
            Check(portrait != null && AgentField<GreetingGifFrame[]>(main, "_greetingFrames").Length == 96, "欢迎岛加载完整动作并显示独立出框头像");
            var bounds = VisualTreeHelper.GetDescendantBounds(portrait!);
            Check(bounds.Width == 64 && bounds.Height == 56, "阿米娅上半身以 64×56 显示，保留帽子和兔耳");
            var picture = portrait!.TransformToAncestor(root).TransformBounds(bounds);
            var body = main.IslandBody.TransformToAncestor(root).TransformBounds(new Rect(main.IslandBody.RenderSize));
            var text = main.GreetingText.TransformToAncestor(root).TransformBounds(new Rect(main.GreetingText.RenderSize));
            Check(picture.Top >= 0 && picture.Right <= text.Left - 2, "头像不越过窗口顶部，也不遮挡欢迎文字");
            var capture = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            capture.Render(root); int left = Math.Max(0, (int)body.Left - 16), top = Math.Max(0, (int)picture.Top - 4);
            var crop = new CroppedBitmap(capture, new Int32Rect(left, top, Math.Min(capture.PixelWidth - left, (int)body.Width + 32),
                Math.Min(capture.PixelHeight - top, (int)Math.Ceiling(body.Bottom) - top + 8)));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(crop));
            using (var file = File.Create(previews + "/amiya-island.png")) encoder.Save(file);
            var amiyaPortrait = portrait;
            GreetingPresetCatalog.Select(live, GreetingPresetCatalog.Nailong); PrivateCall(main, "ApplyGreetingMedia");
            Check(!ReferenceEquals(amiyaPortrait, AgentField<GreetingPortraitAdorner?>(main, "_greetingPortrait")), "切换内置形象重建裁切层，不误用另一形象的视口");
            GreetingPresetCatalog.Select(live, GreetingPresetCatalog.Amiya); PrivateCall(main, "ApplyGreetingMedia");
            PrivateCall(main, "DismissGreeting");
            Check(AgentField<GreetingGifFrame[]>(main, "_greetingFrames").Length == 1
                && AgentField<object?>(main, "_greetingPortrait") == null && AgentField<object?>(main, "_greetingImageTimer") == null,
                "欢迎结束释放 95 帧、出框层和计时器，避免增加常驻内存");
            HandleBackdropChecks(main, root, previews);
        }
        finally { PrivateCall(main, "DismissGreeting"); MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduce, save); }
    }

    private static void HandleBackdropChecks(alpha_island.MainWindow main, Grid root, string previews)
    {
        main.ComponentHandle.Height = 48; main.ComponentHandle.Visibility = Visibility.Visible; root.UpdateLayout();
        var probe = (Rect?)PrivateCall(main, "ComponentHandleProbe");
        Check(probe.HasValue && probe.Value.Width > 0 && probe.Value.Height > 0, "切换条有独立的小块背景采样位置");
        main.ComponentHandle.Visibility = Visibility.Collapsed;
        Check(PrivateCall(main, "ComponentHandleProbe") == null, "隐藏切换条不请求额外背景采样");
        var handle = new IslandContourHandle { Width = 28, Height = 48, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var panel = new Grid { Width = 100, Height = 72 }; panel.Children.Add(handle);
        using var host = new HwndSource(new HwndSourceParameters("Adaptive handle fixture")
        { Width = 100, Height = 72, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        host.RootVisual = panel; panel.UpdateLayout(); PumpAnimations(30); handle.StopHint();
        foreach (var item in new[] { (Name: "light", Color: Colors.White), (Name: "dark", Color: Color.FromRgb(25, 28, 40)),
            (Name: "blue", Color: Color.FromRgb(75, 104, 135)) })
        {
            panel.Background = new SolidColorBrush(item.Color);
            handle.AdaptToBackdrop(SpectrumLuminance(item.Color), false); panel.UpdateLayout();
            var color = (Color)handle.GetValue(IslandContourHandle.BackdropColorProperty);
            Check(color == (item.Name == "light" ? IslandContourHandle.LightBackdropBlue : Colors.White), item.Name + " 背景选择主题蓝或白色");
            Check(SpectrumRatio(color, item.Color) > 3, item.Name + " 背景的切换条对比度足够");
            SaveThemePreview(panel, previews + "/handle-" + item.Name + ".png");
        }
        handle.AdaptToBackdrop(0, false); handle.AdaptToBackdrop(1);
        Check((Color)handle.GetValue(IslandContourHandle.BackdropColorProperty) == Colors.White && handle.HasAnimatedProperties, "背景改变后从当前白色开始过渡，首帧不突变");
        PumpAnimations(180); var middle = (Color)handle.GetValue(IslandContourHandle.BackdropColorProperty);
        Check(middle != Colors.White && middle != IslandContourHandle.LightBackdropBlue, "过渡中存在真实中间颜色");
        handle.AdaptToBackdrop(0);
        var continuation = (Color)handle.GetValue(IslandContourHandle.BackdropColorProperty);
        Check(continuation == middle, $"快速反向切换从正在显示的颜色继续，不重置到端点（before={middle}, after={continuation}, visible={handle.IsVisible}, save={MotionPolicy.PowerSave}, instant={MotionPolicy.InstantMorph}, animated={handle.HasAnimatedProperties}）");
        PumpAnimations(550);
        Check((Color)handle.GetValue(IslandContourHandle.BackdropColorProperty) == Colors.White && !handle.HasAnimatedProperties, "480 毫秒过渡结束后回收动画时钟");
        handle.AdaptToBackdrop(.32, false); handle.AdaptToBackdrop(.30);
        Check(!handle.HasAnimatedProperties && (Color)handle.GetValue(IslandContourHandle.BackdropColorProperty) == IslandContourHandle.LightBackdropBlue,
            "阈值附近亮度抖动不会造成反复变色");
        handle.AdaptToBackdrop(double.NaN); handle.AdaptToBackdrop(-1);
        Check((Color)handle.GetValue(IslandContourHandle.BackdropColorProperty) == IslandContourHandle.LightBackdropBlue, "忽略无效背景采样");
        handle.AdaptToBackdrop(0); host.RootVisual = null; PumpAnimations(20);
        Check(!handle.HasAnimatedProperties, "切换条卸载时释放颜色过渡动画");
    }
}
