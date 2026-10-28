using System.IO;
using System.Text.Json;
using System.Reflection;
using System.Windows;
using alpha_island.Core;
using alpha_island.Services;
using ComboBox = System.Windows.Controls.ComboBox;

internal static partial class Program
{
    private static void GreetingRandomChecks(alpha_island.MainWindow main, string testRoot)
    {
        string[] builtIns = [GreetingPresetCatalog.Logo, GreetingPresetCatalog.Nailong, GreetingPresetCatalog.Amiya, GreetingPresetCatalog.HoshinoSwimsuit];
        Check(Enumerable.Range(0, 100).All(_ => builtIns.Contains(GreetingPresetCatalog.ChooseRandomPreset())),
            "随机欢迎只从四个内置组合中抽取，不会选到随机本身或失效的自定义文件");
        string legacyPath = Path.Combine(testRoot, "retired-greeting.json");
        File.WriteAllText(legacyPath, "{\"GreetingText\":\"旧的自定义问候\",\"GreetingSeconds\":6,\"GlassOpacity\":0.23,\"Extras\":{\"GreetingPreset\":\"random\",\"GreetingSoundVolume\":18,\"GreetingSoundEnabled\":false}}");
        var migrated = IslandOptions.Load(legacyPath);
        Check(!File.ReadAllText(legacyPath).Contains("GreetingText") && File.ReadAllText(legacyPath + ".bak").Contains("旧的自定义问候"),
            "旧自定义问候升级后退出活动配置，旧内容仍留在恢复备份");
        Check(migrated.GreetingSeconds == 6 && migrated.GlassOpacity == .23 && GreetingPresetCatalog.GetId(migrated) == GreetingPresetCatalog.Random
            && SettingsRegistry.GetInt(migrated, "GreetingSoundVolume", 0) == 18 && !SettingsRegistry.GetBool(migrated, "GreetingSoundEnabled", true),
            "移除自定义问候时保留停留时间、外观、随机选择、音量和静音设置");
        var live = AgentField<IslandOptions>(main, "_options");
        var previous = new IslandOptions(); previous.CopyFrom(live);
        string? cache = AgentField<string?>(main, "_greetingRandomPreset");
        string SessionPreset() => (string)typeof(alpha_island.MainWindow).GetProperty("GreetingPresetForPresentation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        try
        {
            PrivateCall(main, "DismissGreeting");
            GreetingPresetCatalog.Select(live, GreetingPresetCatalog.Random);
            SetAgentField(main, "_greetingRandomPreset", null);
            string saved = JsonSerializer.Serialize(live), selected = SessionPreset();
            Check(builtIns.Contains(selected) && AgentField<string?>(main, "_greetingRandomPreset") == selected,
                "本次进程首次使用随机欢迎时抽选并缓存一个有效组合");
            for (int i = 0; i < 20; i++) PrivateCall(main, "ApplyGreetingMedia");
            Check(SessionPreset() == selected && AgentField<string?>(main, "_greetingImagePath") == GreetingPresetCatalog.ImageKey(live, selected)
                && JsonSerializer.Serialize(live) == saved, "重复应用设置不会重新抽取，也不把随机设置改写为固定组合");
            SetAgentField(main, "_greetingRandomPreset", GreetingPresetCatalog.HoshinoSwimsuit);
            PrivateCall(main, "ApplyGreetingMedia");
            using (var sound = GreetingPresetCatalog.OpenBuiltInSound(GreetingPresetCatalog.SoundKey(live, SessionPreset())))
                Check(AgentField<string?>(main, "_greetingImagePath") == GreetingPresetCatalog.HoshinoSwimsuitMedia && sound.Length > 100_000,
                    "随机组合的图片和语音使用同一个缓存结果");
            var settings = new alpha_island.Settings.SettingsWindow(false) { ForceClose = true, GreetingPresetResolver = SessionPreset };
            try
            {
                settings.LoadFrom(live);
                Check(settings.FindName("GreetingTextBox") == null && settings.FindName("Icon_GreetingText") == null,
                    "设置中已删除问候文字输入项，没有空白卡片占位");
                var combo = (ComboBox)settings.FindName("GreetingPresetCombo");
                Check(combo.Items.Count == 5 && combo.SelectedIndex == 4 && AgentField<GreetingGifFrame[]>(settings, "_greetingPreviewFrames").Length == 1,
                    "随机选项正确回填，设置预览只保留本次组合的静态首帧");
                var preview = AgentField<GreetingGifFrame[]>(settings, "_greetingPreviewFrames")[0].Image;
                Check(preview.PixelWidth == 128 && AgentField<System.Windows.Controls.TextBlock>(settings, "_greetingImageHint").Text.Contains("星野"),
                    "随机设置的预览与本次启动实际选择一致");
            }
            finally { settings.Close(); }
            live.GreetingEnabled = false;
            SettingsRegistry.Write(live, "GreetingSoundEnabled", false);
            PrivateCall(main, "PreviewStartupGreeting"); PumpAnimations(400); main.GreetingLayer.UpdateLayout();
            var center = main.GreetingText.TranslatePoint(new System.Windows.Point(main.GreetingText.ActualWidth / 2, 0), main.GreetingLayer);
            Check(main.GreetingText.Text == "欢迎回来" && Math.Abs(center.X - main.GreetingLayer.ActualWidth / 2) <= .5,
                "欢迎回来固定显示，并按整个胶囊的中心对齐");
            Check(SessionPreset() == GreetingPresetCatalog.HoshinoSwimsuit && GreetingPresetCatalog.GetId(live) == GreetingPresetCatalog.Random
                && !live.GreetingEnabled, "临时测试沿用本次随机组合，不修改随机选项和启动欢迎开关");
        }
        finally
        {
            PrivateCall(main, "DismissGreeting"); live.CopyFrom(previous); SetAgentField(main, "_greetingRandomPreset", cache); PrivateCall(main, "ApplyGreetingMedia");
        }
    }
}
