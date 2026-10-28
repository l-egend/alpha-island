using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Services;
using NAudio.Wave;
using ComboBox = System.Windows.Controls.ComboBox;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;

internal static partial class Program
{
    private static void GreetingPresetChecks(alpha_island.MainWindow main, string testRoot)
    {
        var options = new IslandOptions().Normalized();
        Check(GreetingPresetCatalog.GetId(options) == GreetingPresetCatalog.Logo, "新配置仍默认选择项目 Logo与轻柔启动音");
        SettingsRegistry.Write(options, "GreetingSoundVolume", 10);
        SettingsRegistry.Write(options, "GreetingSoundEnabled", false);
        SettingsRegistry.Write(options, "GreetingImagePath", "old-picture.gif");
        SettingsRegistry.Write(options, "GreetingSoundPath", "old-sound.wav");
        GreetingPresetCatalog.Select(options, GreetingPresetCatalog.Nailong);
        Check(GreetingPresetCatalog.ImageKey(options) == GreetingPresetCatalog.NailongMedia
            && GreetingPresetCatalog.SoundKey(options) == GreetingPresetCatalog.NailongMedia
            && SettingsRegistry.GetText(options, "GreetingImagePath", "x") == ""
            && SettingsRegistry.GetText(options, "GreetingSoundPath", "x") == "", "选择奶龙同时切换动画和声音，清除旧自定义路径依赖");
        Check(SettingsRegistry.GetInt(options, "GreetingSoundVolume", 0) == 10 && !SettingsRegistry.GetBool(options, "GreetingSoundEnabled", true),
            "组合切换保留音量和静音偏好");
        var frames = GreetingPresetCatalog.LoadFrames(GreetingPresetCatalog.NailongMedia);
        Check(frames.Length == 98 && frames.Sum(f => f.Delay.TotalMilliseconds) == 7840
            && frames.All(f => f.Image.IsFrozen && f.Image.PixelWidth == 128 && f.Image.PixelHeight == 128),
            "内置透明动画保留 98 帧与 7.84 秒原始节奏，帧资源冻结且有界");
        int translucent = 0, fringe = 0; bool transparentCorners = true;
        foreach (var frame in frames)
        {
            var rgba = new FormatConvertedBitmap(frame.Image, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[128 * 128 * 4]; rgba.CopyPixels(pixels, 128 * 4, 0);
            transparentCorners &= pixels[3] == 0 && pixels[^1] == 0;
            for (int y = 1; y < 127; y++) for (int x = 1; x < 127; x++)
            {
                int i = (y * 128 + x) * 4; byte a = pixels[i + 3];
                if (a is > 0 and < 255) translucent++;
                if (a < 20 || pixels[i] < 240 || pixels[i + 1] < 240 || pixels[i + 2] < 240) continue;
                if (pixels[i - 4 + 3] == 0 || pixels[i + 4 + 3] == 0 || pixels[i - 512 + 3] == 0 || pixels[i + 512 + 3] == 0) fringe++;
            }
        }
        Check(transparentCorners, "全部动画帧四周保持真实透明");
        Check(translucent > 10_000 && fringe == 0, $"动画边缘使用连续透明度，无中性白色描边残留（soft={translucent}, fringe={fringe}）");
        foreach (int frame in new[] { 34, 66, 75 })
        {
            var rgba = new FormatConvertedBitmap(frames[frame].Image, PixelFormats.Bgra32, null, 0); var pixel = new byte[4];
            rgba.CopyPixels(new Int32Rect(64, 107, 1, 1), pixel, 4, 0);
            Check(pixel[3] == 0, "双腿之间的白色背景孔洞已清除");
        }
        Check(GreetingPresetCatalog.LoadFrames(GreetingPresetCatalog.NailongMedia, firstFrameOnly: true).Length == 1,
            "隐藏或静态预览仅解码首帧，不常驻整段动画");
        using (var input = GreetingPresetCatalog.OpenNailongSound())
            Check(Convert.ToHexString(SHA256.HashData(input)) == "C5C8F4ABF5E35111F49993DA7949FF4FAFE4BD5EDCD1CE9E93C8512A5D225C1E",
                "内置配套音效与用户当前选择完全一致");
        using (var audio = new WaveFileReader(GreetingPresetCatalog.OpenNailongSound()))
            Check(audio.TotalTime.TotalSeconds is > 1 and <= 10, "配套音效完整可解码且符合短音效时长限制");
        HoshinoGreetingAssetChecks();
        HoshinoBlinkIsolationChecks();
        GreetingRandomChecks(main, testRoot);
        GreetingWaveOutput? output = null;
        using (var player = new GreetingAudioPlayer(main.Dispatcher, () => output = new GreetingWaveOutput()))
        {
            player.Play(GreetingPresetCatalog.NailongMedia, 10);
            Check(player.IsPlaying && output?.Volume == .1f, "内置组合试听读取嵌入音效并遵守独立音量");
            player.Stop(); Check(output!.Disposed && !player.IsPlaying, "内置组合试听结束释放设备和音频流");
        }
        bool rejected = false; try { GreetingPresetCatalog.Select(options, "invalid"); } catch (ArgumentException) { rejected = true; }
        Check(rejected && GreetingPresetCatalog.GetId(options) == GreetingPresetCatalog.Nailong, "无效组合不会覆盖当前选择");
        var roundtrip = System.Text.Json.JsonSerializer.Deserialize<IslandOptions>(System.Text.Json.JsonSerializer.Serialize(options))!;
        Check(GreetingPresetCatalog.GetId(roundtrip) == GreetingPresetCatalog.Nailong, "组合选择随配置保存和重启保留");
        GreetingPresetSettingsChecks(options, testRoot);

        var live = AgentField<IslandOptions>(main, "_options");
        GreetingPresetCatalog.Select(live, GreetingPresetCatalog.Nailong); PrivateCall(main, "ApplyGreetingMedia");
        Check(main.GreetingCustomImage.Source != null && main.GreetingIcon.Visibility == Visibility.Collapsed, "欢迎岛直接加载内置奶龙，无本机导入路径依赖");
        bool reduce = MotionPolicy.ReduceMotion, save = MotionPolicy.PowerSave; int tier = MotionPolicy.RenderTier;
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            AgentField<IslandLayerHost>(main, "_layers").HideAll(); live.GreetingEnabled = true; live.GreetingSeconds = 8;
            PrivateCall(main, "SetState", IslandState.Bar, true); PrivateCall(main, "ShowStartupGreeting"); PumpAnimations(400);
            var first = main.GreetingCustomImage.Source; PumpAnimations(180);
            Check(MotionPolicy.InstantMorph || !ReferenceEquals(first, main.GreetingCustomImage.Source), "真实欢迎岛展示时按原时序播放内置动画");
            SaveThemePreview(main.IslandBody, "bin/WelcomePresets160/previews/nailong-island.png");
            PrivateCall(main, "DismissGreeting");
        }
        finally { MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduce, save); }
        Check(AgentField<GreetingGifFrame[]>(main, "_greetingFrames").Length == 1
            && AgentField<object?>(main, "_greetingImageTimer") == null, "欢迎岛隐藏后只保留首帧并释放动画计时器");
        GreetingPresetCatalog.Select(live, GreetingPresetCatalog.Logo); PrivateCall(main, "ApplyGreetingMedia");
        Check(main.GreetingCustomImage.Source == null && main.GreetingIcon.Visibility == Visibility.Visible
            && GreetingPresetCatalog.SoundKey(live) == "", "切回兔子同时恢复默认图标和轻柔音效");
    }

    private static void GreetingPresetSettingsChecks(IslandOptions options, string testRoot)
    {
        var settings = new alpha_island.Settings.SettingsWindow(false) { ForceClose = true };
        try
        {
            settings.LoadFrom(options);
            var combo = (ComboBox)settings.FindName("GreetingPresetCombo");
            var saved = AgentField<IslandOptions>(settings, "_options");
            int changes = 0; settings.OptionsChanged += (_, _) => changes++;
            Check(combo.Items.Count == 5 && combo.SelectedIndex == 1, "真实设置展示四个内置组合与随机选项并正确回填奶龙选择");
            combo.SelectedIndex = 0;
            Check(GreetingPresetCatalog.GetId(saved) == GreetingPresetCatalog.Logo && GreetingPresetCatalog.ImageKey(saved) == ""
                && GreetingPresetCatalog.SoundKey(saved) == "" && changes == 1, "下拉切换兔子通过同一次设置事件同步更换图片和音效");
            combo.SelectedIndex = 1;
            Check(GreetingPresetCatalog.GetId(saved) == GreetingPresetCatalog.Nailong && changes == 2
                && SettingsRegistry.GetInt(saved, "GreetingSoundVolume", 0) == 10, "下拉切换奶龙保留用户音量且不重复提交设置");
            combo.SelectedIndex = 3;
            Check(GreetingPresetCatalog.GetId(saved) == GreetingPresetCatalog.HoshinoSwimsuit
                && GreetingPresetCatalog.ImageKey(saved) == GreetingPresetCatalog.HoshinoSwimsuitMedia
                && GreetingPresetCatalog.SoundKey(saved) == GreetingPresetCatalog.HoshinoSwimsuitMedia && changes == 3,
                "星野组合通过同一次设置事件绑定透明动画与截取后的日配语音");
            Check(AgentField<GreetingGifFrame[]>(settings, "_greetingPreviewFrames").Length == 1
                && SettingsRegistry.GetInt(saved, "GreetingSoundVolume", 0) == 10
                && !SettingsRegistry.GetBool(saved, "GreetingSoundEnabled", true), "星野设置只保留静态首帧，切换保留音量与静音偏好");
            var panel = (Grid)settings.Content; settings.Content = null; panel.Resources = settings.Resources;
            using var host = new HwndSource(new HwndSourceParameters("Greeting preset preview")
            { Width = 1000, Height = 900, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
            host.RootVisual = panel;
            PrivateCall(settings, "SelectPageByTag", "notifications"); panel.UpdateLayout();
            var rows = (System.Windows.Controls.StackPanel)((System.Windows.Controls.Border)((Grid)settings.GreetingToggle.Parent).Parent).Parent;
            Directory.CreateDirectory("bin/WelcomePresets160/previews");
            SaveThemePreview(rows, "bin/WelcomePresets160/previews/preset-settings.png");
            Directory.CreateDirectory("bin/HoshinoGreeting172/previews");
            SaveThemePreview(rows, "bin/HoshinoGreeting172/previews/preset-settings.png");
            host.RootVisual = null;
        }
        finally { settings.Close(); }
    }

    private static void HoshinoBlinkIsolationChecks()
    {
        var frames = GreetingPresetCatalog.LoadFrames(GreetingPresetCatalog.HoshinoSwimsuitMedia);
        byte[] Pixels(GreetingGifFrame frame)
        {
            var bitmap = new FormatConvertedBitmap(frame.Image, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[128 * 128 * 4]; bitmap.CopyPixels(pixels, 512, 0); return pixels;
        }
        bool Eye(int x, int y) => x is >= 36 and <= 67 && y is >= 63 and <= 86
            || x is >= 66 and <= 95 && y is >= 56 and <= 82;
        var baseline = Pixels(frames[0]);
        int stableChanges = 0, eyeChanges = 0;
        foreach (var frame in frames)
        {
            var pixels = Pixels(frame);
            for (int y = 60; y < 125; y++) for (int x = 12; x < 123; x++)
            {
                int p = (y * 128 + x) * 4;
                bool changed = Enumerable.Range(0, 4).Any(c => pixels[p + c] != baseline[p + c]);
                if (!changed) continue;
                if (Eye(x, y)) eyeChanges++; else stableChanges++;
            }
        }
        Console.WriteLine($"Hoshino isolated blink: non-eye changes={stableChanges}, eye changes={eyeChanges}");
        Check(stableChanges == 0, "星野眨眼全过程中，眼睛范围以外的脸部、头发、手和服装像素保持不变");
        Check(eyeChanges > 100, "保持人物静止的同时仍有清晰可见的眨眼");
        Check(Pixels(frames[^1]).SequenceEqual(baseline), "光环浮动结束后回到原位，最终保持同一张睁眼底图");
    }

    private static void HoshinoGreetingAssetChecks()
    {
        var options = new IslandOptions().Normalized();
        GreetingPresetCatalog.Select(options, GreetingPresetCatalog.HoshinoSwimsuit);
        var roundtrip = System.Text.Json.JsonSerializer.Deserialize<IslandOptions>(System.Text.Json.JsonSerializer.Serialize(options))!;
        Check(GreetingPresetCatalog.GetId(roundtrip) == GreetingPresetCatalog.HoshinoSwimsuit
            && GreetingPresetCatalog.ImageKey(roundtrip) == GreetingPresetCatalog.HoshinoSwimsuitMedia
            && GreetingPresetCatalog.SoundKey(roundtrip) == GreetingPresetCatalog.HoshinoSwimsuitMedia,
            "星野内置组合可持久保存，不依赖下载文件或本机素材路径");
        var frames = GreetingPresetCatalog.LoadFrames(GreetingPresetCatalog.HoshinoSwimsuitMedia);
        Check(frames.Length == 96 && frames.Sum(f => f.Delay.TotalMilliseconds) == 4000
            && frames.All(f => f.Image.IsFrozen && f.Image.PixelWidth == 128 && f.Image.PixelHeight == 128),
            "星野上半身包含 96 帧眨眼与光环轻动效，四秒播放且解码尺寸有界");
        bool transparentBorder = true; int haloPixels = 0;
        var hashes = new HashSet<string>();
        foreach (var frame in frames)
        {
            var rgba = new FormatConvertedBitmap(frame.Image, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[128 * 128 * 4]; rgba.CopyPixels(pixels, 128 * 4, 0);
            hashes.Add(Convert.ToHexString(SHA256.HashData(pixels)));
            for (int i = 0; i < 128; i++)
                transparentBorder &= pixels[i * 4 + 3] == 0 && pixels[(127 * 128 + i) * 4 + 3] == 0
                    && pixels[i * 128 * 4 + 3] == 0 && pixels[(i * 128 + 127) * 4 + 3] == 0;
            for (int y = 0; y < 45; y++) for (int x = 0; x < 128; x++)
            {
                int p = (y * 128 + x) * 4;
                if (pixels[p + 3] > 128 && pixels[p] > 210 && pixels[p + 1] > 210 && pixels[p + 2] > 210) haloPixels++;
            }
        }
        Check(transparentBorder && hashes.Count > 60, "星野全部帧四周真实透明，动画具有连续变化而非重复静态帧");
        Check(haloPixels > 10_000, "裁切保留头顶光环，未把浅色光环当作背景去除");
        Check(GreetingPresetCatalog.LoadFrames(GreetingPresetCatalog.HoshinoSwimsuitMedia, firstFrameOnly: true).Length == 1,
            "星野静态预览与隐藏状态仅解码一帧");
        using (var audio = new WaveFileReader(GreetingPresetCatalog.OpenBuiltInSound(GreetingPresetCatalog.HoshinoSwimsuitMedia)))
            Check(Math.Abs(audio.TotalTime.TotalSeconds - 1.98) < .001 && audio.WaveFormat.SampleRate == 44100
                && audio.WaveFormat.BitsPerSample == 16 && audio.WaveFormat.Channels == 1,
                "星野日配只保留欢迎回来老师的开场，后续台词未纳入播放");
        using (var sound = GreetingPresetCatalog.OpenBuiltInSound(GreetingPresetCatalog.HoshinoSwimsuitMedia))
            Check(Convert.ToHexString(SHA256.HashData(sound)) == "1937513F0F43AFC924ACBEDF5263AC2C22750DE82D26A97E938ED279154076DE",
                "嵌入星野语音内容与已核对的截取文件一致");
    }
}
