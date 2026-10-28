using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Services;
using NAudio.Wave;
using CheckBox = System.Windows.Controls.CheckBox;
using Image = System.Windows.Controls.Image;
using TextBox = System.Windows.Controls.TextBox;
using PlaybackState = NAudio.Wave.PlaybackState;

internal static partial class Program
{
    private static void GreetingMediaChecks(alpha_island.MainWindow main, Grid root, string testRoot)
    {
        string folder = Path.Combine(testRoot, "greeting-media"); Directory.CreateDirectory(folder);
        string source = Path.Combine(folder, "picture.png");
        var pixels = new byte[1024 * 512 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 210; pixels[i + 1] = 180; pixels[i + 2] = 35; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(1024, 512, 96, 96, PixelFormats.Bgra32, null, pixels, 4096);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(source)) encoder.Save(file);
        var store = new GreetingMediaStore(Path.Combine(folder, "assets"));
        string image = store.ImportImage(source); var imported = GreetingMediaStore.LoadImage(image);
        Check(imported.IsFrozen && imported.PixelWidth == 128 && imported.PixelHeight == 64,
            "欢迎图片导入为冻结缩略图，保留比例且最长边限制在 128 像素");
        using (var exclusive = File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(exclusive.CanWrite, "图片解码完成后释放原文件，用户可以移动或删除素材");
        string moved = Path.Combine(folder, "moved.png"); File.Move(source, moved);
        Check(File.Exists(image) && GreetingMediaStore.LoadImage(image).PixelWidth == 128, "移动原图片不影响软件保留的欢迎图片");
        string corrupt = Path.Combine(folder, "broken.png"); File.WriteAllText(corrupt, "not an image");
        int imageCount = Directory.GetFiles(store.DirectoryPath).Length; bool invalid = false;
        try { store.ImportImage(corrupt); } catch { invalid = true; }
        Check(invalid && Directory.GetFiles(store.DirectoryPath).Length == imageCount, "损坏图片不产生残缺导入文件或覆盖旧素材");
        store.RemoveOwnedAsset(moved); Check(File.Exists(moved), "清理旧欢迎图片不会删除目录外的原始素材");

        string wave = Path.Combine(folder, "welcome.wav");
        using (var input = GreetingAudioPlayer.OpenDefaultSound())
        using (var file = File.Create(wave)) input.CopyTo(file);
        using (var sound = new WaveFileReader(wave))
            Check(sound.TotalTime.TotalSeconds is > .3 and < 2 && sound.WaveFormat.BitsPerSample == 16,
                "默认原创欢迎音效是短时 16 位 WAV，安装程序内嵌资源完整可解码");
        string importedSound = store.ImportSound(wave);
        using (var sound = new WaveFileReader(importedSound)) Check(sound.TotalTime.TotalSeconds is > .3 and < 2, "自选音效转为独立 WAV 副本，保持正常时长");
        string tooLong = Path.Combine(folder, "long.wav");
        using (var writer = new WaveFileWriter(tooLong, new WaveFormat(8000, 16, 1))) writer.Write(new byte[11 * 8000 * 2], 0, 11 * 8000 * 2);
        int soundCount = Directory.GetFiles(store.DirectoryPath).Length; bool longRejected = false;
        try { store.ImportSound(tooLong); } catch (InvalidDataException) { longRejected = true; }
        Check(longRejected && Directory.GetFiles(store.DirectoryPath).Length == soundCount, "拒绝超过十秒的音效，保留已有音效");

        var outputs = new List<GreetingWaveOutput>();
        using (var player = new GreetingAudioPlayer(main.Dispatcher, () => { var output = new GreetingWaveOutput(); outputs.Add(output); return output; }))
        {
            player.Play("", 35);
            Check(player.IsPlaying && Math.Abs(outputs[^1].Volume - .35) < .001, "欢迎播放使用独立音量，默认 35%，不会修改系统音量");
            player.SetVolume(60); Check(Math.Abs(outputs[^1].Volume - .6) < .001, "音效音量修改立即作用于正在试听的输出");
            var previous = outputs[^1]; previous.Finish(); player.Play(importedSound, 40); PumpAnimations(20);
            Check(previous.Disposed && player.IsPlaying && !outputs[^1].Disposed, "切换试听后旧结束事件不会停止新音效");
            var current = outputs[^1]; current.Finish(); PumpAnimations(20);
            Check(current.Disposed && !player.IsPlaying && AgentField<object?>(player, "_stream") == null
                && AgentField<object?>(player, "_deadline") == null, "音效自然结束释放输出、文件流和兜底计时器，不常驻音频设备");
            int before = outputs.Count; player.Play("", 0); Check(outputs.Count == before && !player.IsPlaying, "零音量不创建播放设备");
            player.Play("", 30); var stopped = outputs[^1]; player.Stop();
            Check(stopped.Disposed && !player.IsPlaying, "隐藏或退出时可立即停止并释放音效");
        }

        var options = AgentField<IslandOptions>(main, "_options");
        SettingsRegistry.Write(options, "GreetingImagePath", image); PrivateCall(main, "ApplyGreetingMedia");
        Check(main.GreetingCustomImage.Visibility == Visibility.Visible && main.GreetingIcon.Visibility == Visibility.Collapsed
            && main.GreetingCustomImage.Source is BitmapSource { IsFrozen: true }, "真实欢迎层用自选图片替换默认项目 Logo，并使用冻结资源");
        AgentField<IslandLayerHost>(main, "_layers").HideAll(); options.GreetingEnabled = true; options.GreetingSeconds = 10;
        PrivateCall(main, "SetState", IslandState.Bar, true);
        PrivateCall(main, "ShowStartupGreeting"); PumpAnimations(500); root.UpdateLayout();
        Console.WriteLine($"Greeting layout: image={main.GreetingCustomImage.ActualWidth}x{main.GreetingCustomImage.ActualHeight}, layerWidth={main.GreetingLayer.Width}, layerActual={main.GreetingLayer.ActualWidth}x{main.GreetingLayer.ActualHeight}, visible={main.GreetingLayer.Visibility}, opacity={main.GreetingLayer.Opacity}");
        var imageSlot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(main.GreetingCustomImage);
        Check(imageSlot.Width == 32 && imageSlot.Height == 32 && main.GreetingLayer.Width == 328
            && Math.Abs(main.GreetingCustomImage.ActualWidth / main.GreetingCustomImage.ActualHeight - 2) < .001,
            "自选图片保持原有欢迎图标与胶囊布局尺寸");
        Directory.CreateDirectory("bin/WelcomeMedia150/previews"); SaveThemePreview(main.GreetingLayer, "bin/WelcomeMedia150/previews/welcome-custom-image.png");
        string gifSource = Path.Combine(folder, "animated.gif");
        var gifEncoder = new GifBitmapEncoder();
        gifEncoder.Frames.Add(BitmapFrame.Create(bitmap));
        var pixels2 = new byte[1024 * 512 * 4];
        for (int i = 0; i < pixels2.Length; i += 4) { pixels2[i] = 30; pixels2[i + 1] = 220; pixels2[i + 2] = 245; pixels2[i + 3] = 255; }
        gifEncoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1024, 512, 96, 96, PixelFormats.Bgra32, null, pixels2, 4096)));
        using (var file = File.Create(gifSource)) gifEncoder.Save(file);
        string importedGif = store.ImportImage(gifSource);
        var gifFrames = GreetingMediaStore.LoadFrames(importedGif);
        Check(Path.GetExtension(importedGif) == ".gif" && gifFrames.Length == 2 && gifFrames.All(f => f.Image.IsFrozen && f.Image.PixelWidth == 128 && f.Image.PixelHeight == 64),
            "欢迎动图导入保留 GIF 副本并解码为冻结的缩略帧序列");
        Check(GreetingPresetCatalog.LoadFrames(importedGif, firstFrameOnly: true).Length == 1,
            "自定义 GIF 静态预览只解码一帧，完整播放仍保留全部帧");
        bool reduced = MotionPolicy.ReduceMotion, saving = MotionPolicy.PowerSave; int tier = MotionPolicy.RenderTier;
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            SettingsRegistry.Write(options, "GreetingImagePath", importedGif); PrivateCall(main, "ApplyGreetingMedia");
            var firstFrame = main.GreetingCustomImage.Source;
            PrivateCall(main, "OnGreetingImageTick", null, EventArgs.Empty);
            Check(MotionPolicy.InstantMorph || !ReferenceEquals(firstFrame, main.GreetingCustomImage.Source),
                "欢迎展示且允许动画时，自定义 GIF 按帧切换左侧图片");
        }
        finally { MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduced, saving); }
        SettingsRegistry.Write(options, "GreetingImagePath", Path.Combine(folder, "missing.png")); PrivateCall(main, "ApplyGreetingMedia");
        Check(main.GreetingCustomImage.Source == null && main.GreetingIcon.Visibility == Visibility.Visible, "欢迎图片缺失时回退默认项目 Logo，不影响提示启动");
        Check(AgentField<GreetingAudioPlayer?>(main, "_greetingAudio") == null, "隔离 UI 检查不会播放真实声音或访问系统音频设备");
        PrivateCall(main, "DismissGreeting");

        var settings = new alpha_island.Settings.SettingsWindow(false, greetingAssetsDirectory: Path.Combine(folder, "settings-assets"));
        settings.LoadFrom(new IslandOptions().Normalized());
        int changed = 0; settings.OptionsChanged += (_, _) => changed++;
        var panel = (Grid)settings.Content; settings.Content = null; panel.Resources = settings.Resources;
        using var host = new HwndSource(new HwndSourceParameters("Greeting media settings")
        { Width = 880, Height = 740, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        host.RootVisual = panel;
        try
        {
            Check(new[] { "GreetingImageChoose", "GreetingImageReset", "GreetingSoundChoose", "GreetingSoundReset", "GreetingSoundPreview", "GreetingSoundToggle", "GreetingSoundVolumeBox" }
                .All(name => settings.FindName(name) is FrameworkElement), "欢迎图片、音效、试听、恢复默认和独立音量入口均存在于真实设置控件树");
            PrivateCall(settings, "ImportGreetingMedia", moved, true); var configured = AgentField<IslandOptions>(settings, "_options");
            string selected = SettingsRegistry.GetText(configured, "GreetingImagePath", "");
            Check(File.Exists(selected) && changed > 0, "选择欢迎图片后立即应用配置，并保存软件自己的素材副本");
            PrivateCall(settings, "ImportGreetingMedia", wave, false);
            string selectedSound = SettingsRegistry.GetText(configured, "GreetingSoundPath", "");
            Check(File.Exists(selectedSound), "音效文件选择通过同一实时设置流程保存");
            bool failed = false; try { PrivateCall(settings, "ImportGreetingMedia", corrupt, true); } catch { failed = true; }
            Check(failed && SettingsRegistry.GetText(configured, "GreetingImagePath", "") == selected, "无效图片选择不会丢失当前配置");
            var volume = (TextBox)settings.FindName("GreetingSoundVolumeBox"); volume.Text = "42";
            Check(SettingsRegistry.GetInt(configured, "GreetingSoundVolume", -1) == 42, "音效音量数值实时写回设置");
            ((CheckBox)settings.FindName("GreetingSoundToggle")).IsChecked = false;
            Check(!SettingsRegistry.GetBool(configured, "GreetingSoundEnabled", true) && !volume.IsEnabled, "关闭欢迎音效立即保存，并禁用音量输入");
            ((CheckBox)settings.FindName("GreetingSoundToggle")).IsChecked = true;
            PrivateCall(settings, "SelectPageByTag", "notifications"); panel.UpdateLayout();
            var rows = (StackPanel)((Border)((Grid)settings.GreetingToggle.Parent).Parent).Parent;
            SaveThemePreview(rows, "bin/WelcomeMedia150/previews/welcome-settings.png");
            PrivateCall(settings, "ResetGreetingMedia", true); PrivateCall(settings, "ResetGreetingMedia", false); settings.ReportSettingsSaved(true);
            Check(SettingsRegistry.GetText(configured, "GreetingImagePath", "") == "" && SettingsRegistry.GetText(configured, "GreetingSoundPath", "") == ""
                && !File.Exists(selected) && !File.Exists(selectedSound) && File.Exists(moved), "恢复默认在成功保存后只清理旧导入副本，保留用户原始文件");
        }
        finally { host.RootVisual = null; settings.ForceClose = true; settings.Close(); }
        GreetingPresetChecks(main, testRoot);
    }

    private sealed class GreetingWaveOutput : IWavePlayer
    {
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public PlaybackState PlaybackState { get; private set; }
        public float Volume { get; set; }
        public WaveFormat OutputWaveFormat { get; private set; } = new WaveFormat(48000, 16, 1);
        internal bool Disposed { get; private set; }
        public void Init(IWaveProvider waveProvider) { OutputWaveFormat = waveProvider.WaveFormat; }
        public void Play() => PlaybackState = PlaybackState.Playing;
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Stop() => PlaybackState = PlaybackState.Stopped;
        public void Dispose() => Disposed = true;
        internal void Finish() { Stop(); PlaybackStopped?.Invoke(this, new StoppedEventArgs(null)); }
    }
}
