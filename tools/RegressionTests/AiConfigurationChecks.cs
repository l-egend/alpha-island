using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private const string TestApiKey = "test-only-not-a-real-key-57af2361";

    private static void AiConfigurationChecks(string testRoot)
    {
        string root = Path.Combine(testRoot, "configuration"); Directory.CreateDirectory(root);
        var options = new IslandOptions { GreetingSeconds = 6, GlassOpacity = .23 };
        SettingsRegistry.SetExtra(options, "AiModel", "test-model");
        SettingsRegistry.SetExtra(options, "PomodoroFocusMinutes", 57);
        SettingsRegistry.SetExtra(options, "plugin/vivo.office/enabled", true);
        SettingsRegistry.SetExtra(options, "plugin/oppo.pods/setting/name", "保留的插件参数");
        AtomicJsonFile.Write(Path.Combine(root, "options.json"), JsonSerializer.Serialize(options, IslandOptions.JsonOptions));
        AtomicJsonFile.Write(Path.Combine(root, "options.before-upgrade.json"), "{\"GreetingText\":\"旧备份\"}");
        string oldSecret = Path.Combine(root, "ai", "secrets.dat");
        Check(AiSecretStore.TrySave(oldSecret, TestApiKey) && AiSecretStore.Load(oldSecret) == TestApiKey, "真实 DPAPI 加密并回读测试密钥");
        byte[] encrypted = File.ReadAllBytes(oldSecret);
        Check(!Encoding.UTF8.GetString(encrypted).Contains(TestApiKey) && !Encoding.Unicode.GetString(encrypted).Contains(TestApiKey), "密文中没有 UTF8 或 UTF16 明文密钥");
        Directory.CreateDirectory(Path.Combine(root, "icons", "nested"));
        File.WriteAllText(Path.Combine(root, "icons", "nested", "custom.png"), "custom-icon");
        File.WriteAllText(Path.Combine(root, "icon.ico"), "custom-tray");
        Directory.CreateDirectory(Path.Combine(root, "cache")); File.WriteAllText(Path.Combine(root, "cache", "weather.json"), "cache");
        Directory.CreateDirectory(Path.Combine(root, "ai", "sessions")); File.WriteAllText(Path.Combine(root, "ai", "sessions", "default.json"), "history");
        Directory.CreateDirectory(Path.Combine(root, "plugins")); File.WriteAllText(Path.Combine(root, "plugins", "fixture.dll"), "plugin");
        ConfigurationLayout.Migrate(root);
        var migrated = IslandOptions.Load(ConfigurationLayout.OptionsFile(root));
        Check(migrated.GreetingSeconds == options.GreetingSeconds && migrated.GlassOpacity == .23, "集中目录迁移保留全局配置");
        Check(SettingsRegistry.GetText(migrated, "AiModel", "") == "test-model"
            && SettingsRegistry.GetInt(migrated, "PomodoroFocusMinutes", 0) == 57, "AI 与番茄钟配置同在 options.json 并完整迁移");
        Check(SettingsRegistry.GetBool(migrated, "plugin/vivo.office/enabled", false)
            && SettingsRegistry.GetText(migrated, "plugin/oppo.pods/setting/name", "") == "保留的插件参数", "插件开关与参数也纳入集中配置");
        string secrets = ConfigurationLayout.SecretFile(root);
        Check(File.ReadAllBytes(secrets).SequenceEqual(encrypted) && AiSecretStore.Load(secrets) == TestApiKey, "迁移密文不解密重写且原密钥可用");
        Check(!File.Exists(oldSecret) && !File.Exists(Path.Combine(root, "options.json")), "迁移后旧位置不再保留活动配置");
        Check(File.Exists(Path.Combine(root, "config", "backups", "options.before-upgrade.json")), "历史设置备份也集中迁移");
        Check(File.ReadAllText(Path.Combine(root, "config", "icon.ico")) == "custom-tray"
            && File.ReadAllText(Path.Combine(root, "config", "icons", "nested", "custom.png")) == "custom-icon", "托盘图标与设置图标资源完整迁移");
        Check(File.ReadAllText(Path.Combine(root, "cache", "weather.json")) == "cache"
            && File.ReadAllText(Path.Combine(root, "ai", "sessions", "default.json")) == "history"
            && File.Exists(Path.Combine(root, "plugins", "fixture.dll")), "配置迁移不破坏缓存、历史或插件程序");
        int files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length;
        ConfigurationLayout.Migrate(root);
        Check(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == files && AiSecretStore.Load(secrets) == TestApiKey, "重复启动迁移幂等");
        File.WriteAllText(Path.Combine(root, "options.json"), "{\"GreetingText\":\"过时版本\"}");
        ConfigurationLayout.Migrate(root);
        Check(IslandOptions.Load(ConfigurationLayout.OptionsFile(root)).GreetingSeconds == options.GreetingSeconds
            && Directory.GetFiles(Path.Combine(root, "config", "backups"), "legacy-*-options.json").Length == 1, "新配置优先，旧冲突文件归档而不覆盖");
        options.Extras["AiApiKey"] = JsonSerializer.SerializeToElement(TestApiKey);
        string unsafePath = Path.Combine(testRoot, "no-plaintext-options.json"); options.Save(unsafePath);
        Check(!File.ReadAllText(unsafePath).Contains(TestApiKey), "程序设置保存入口阻止明文 API Key 落盘");
        using (var held = new FileStream(secrets, FileMode.Open, FileAccess.Read, FileShare.None))
            Check(!AiSecretStore.TrySave(secrets, "another-test-key"), "密钥目标被占用时保存明确失败");
        Check(File.ReadAllBytes(secrets).SequenceEqual(encrypted) && AiSecretStore.Load(secrets) == TestApiKey
            && !Directory.GetFiles(Path.GetDirectoryName(secrets)!, "*.tmp").Any(), "密钥保存失败保留原密文且清理临时文件");
        Check(AiSecretStore.TrySave(secrets, TestApiKey) && !File.ReadAllBytes(secrets).SequenceEqual(encrypted), "同一密钥重复加密产生不同密文");
        byte[] corrupt = File.ReadAllBytes(secrets); corrupt[^1] ^= 0x31; File.WriteAllBytes(secrets, corrupt);
        Check(AiSecretStore.Load(secrets) == "" && File.ReadAllBytes(secrets).SequenceEqual(corrupt), "篡改密文不能解密且不被静默覆盖");
        Check(AiSecretStore.TrySave(secrets, "") && !File.Exists(secrets), "明确删除密钥会移除密文文件");
        string legacyRoot = Path.Combine(testRoot, "legacy-key"); Directory.CreateDirectory(legacyRoot);
        File.WriteAllText(Path.Combine(legacyRoot, "options.json"), JsonSerializer.Serialize(new { GreetingText = "旧配置", Extras = new { AiApiKey = TestApiKey, AiModel = "legacy-model" } }));
        ConfigurationLayout.Migrate(legacyRoot);
        Check(AiSecretStore.Load(ConfigurationLayout.SecretFile(legacyRoot)) == TestApiKey
            && !File.ReadAllText(ConfigurationLayout.OptionsFile(legacyRoot)).Contains(TestApiKey), "旧明文密钥先加密再从配置中清除");
        string failedRoot = Path.Combine(testRoot, "failed-migration"); Directory.CreateDirectory(Path.Combine(failedRoot, "config"));
        File.WriteAllText(Path.Combine(failedRoot, "config", "ai"), "directory-blocker");
        string failedOptions = Path.Combine(failedRoot, "options.json");
        File.WriteAllText(failedOptions, JsonSerializer.Serialize(new { Extras = new { AiApiKey = TestApiKey } }));
        string original = File.ReadAllText(failedOptions); bool failed = false;
        try { ConfigurationLayout.Migrate(failedRoot); } catch (IOException) { failed = true; }
        Check(failed && File.ReadAllText(failedOptions) == original, "旧密钥加密失败时保留原配置以便恢复");
        string corruptRoot = Path.Combine(testRoot, "corrupt-migration"); Directory.CreateDirectory(corruptRoot);
        File.WriteAllText(Path.Combine(corruptRoot, "options.json"), "{broken");
        File.WriteAllText(Path.Combine(corruptRoot, "options.json.bak"), "{\"GreetingSeconds\":8}");
        ConfigurationLayout.Migrate(corruptRoot);
        Check(IslandOptions.Load(ConfigurationLayout.OptionsFile(corruptRoot)).GreetingSeconds == 8, "损坏设置迁移后仍可从备份恢复");
        Check(!IslandLog.DumpText().Contains(TestApiKey), "加密、迁移和失败日志不包含测试密钥");
        string codexOld = Path.Combine(testRoot, "codex-config.json");
        const string codexJson = "{\"endpoint\":\"http://127.0.0.1:8765/\",\"approvalMode\":\"native\",\"projectRoot\":\"test-project\",\"runtimeDir\":\"test-runtime\"}";
        File.WriteAllText(codexOld, codexJson);
        ConfigurationLayout.Migrate(root, codexOld);
        Check(File.ReadAllText(ConfigurationLayout.CodexFile(root)) == codexJson
            || JsonDocument.Parse(File.ReadAllText(ConfigurationLayout.CodexFile(root))).RootElement.GetProperty("approvalMode").GetString() == "native", "Codex 联动配置也集中保存且原审批模式保留");
        using (var pointer = JsonDocument.Parse(File.ReadAllText(codexOld)))
            Check(pointer.RootElement.EnumerateObject().Count() == 1 && pointer.RootElement.GetProperty("configPath").GetString() == ConfigurationLayout.CodexFile(root), "原 Codex 路径只保留兼容指针，已信任命令不变");
        string centralCodex = File.ReadAllText(ConfigurationLayout.CodexFile(root));
        ConfigurationLayout.Migrate(root, codexOld);
        Check(File.ReadAllText(ConfigurationLayout.CodexFile(root)) == centralCodex, "Codex 配置迁移重复执行不改写新配置");
        File.WriteAllText(codexOld, codexJson.Replace("native", "island"));
        ConfigurationLayout.Migrate(root, codexOld);
        Check(File.ReadAllText(ConfigurationLayout.CodexFile(root)) == centralCodex
            && File.Exists(Path.Combine(root, "config", "backups", "codex-hooks.legacy.json")), "Codex 新配置优先且旧冲突配置完整归档");
    }

    private static void AiSettingsConfigurationUiChecks(string root)
    {
            AlphaIsland.Plugins.ApiChat.ApiSettingsView? settings = null;
            try
            {
                var options = new IslandOptions().Normalized();
                string path = Path.Combine(root, "settings-key", "secrets.dat");
                settings = new AlphaIsland.Plugins.ApiChat.ApiSettingsView(false, path); settings.LoadFrom(options);
                var input = settings.AiApiKeyBox;
                foreach (char c in TestApiKey) input.Password += c;
                Check(input.Password == TestApiKey && !File.Exists(path), "逐字输入完整密钥不会提前保存或清空");
                settings.AiSystemPromptBox.Text = "编辑其他配置";
                var currentOptions = (IslandOptions)typeof(AlphaIsland.Plugins.ApiChat.ApiSettingsView).GetField("_options", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(settings)!;
                Check(!File.Exists(path) && SettingsRegistry.GetText(currentOptions, "AiApiKey", "") == "", "其他设置自动保存也不会写入未确认的密钥");
                Check(settings.SaveAiKeyButton.IsEnabled && !settings.ClearAiKeyButton.IsEnabled, "待保存密钥的按钮状态正确");
                settings.SaveAiKeyButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(AiSecretStore.Load(path) == TestApiKey && input.Password == "" && settings.ClearAiKeyButton.IsEnabled, "保存按钮提交完整密钥并清空输入框");
                Check(!SettingsRegistry.GetBool(currentOptions, "AiEnabled", false), "只保存密钥但尚未连接成功时不启用 AI 对话");
                input.Password = "replacement-test-key";
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    settings.SaveAiKeyButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Check(input.Password == "replacement-test-key" && settings.AiApiKeyState.Text.Contains("保存失败"), "保存失败明确提示并保留完整输入");
                }
                Check(AiSecretStore.Load(path) == TestApiKey, "设置界面更换密钥失败保留原密钥");
                input.Password = "new-test-key"; input.Password = "";
                Check(AiSecretStore.Load(path) == TestApiKey, "清空输入不删除已保存密钥");
                settings.ClearAiKeyButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(!File.Exists(path) && !settings.ClearAiKeyButton.IsEnabled, "删除按钮明确删除密钥");
                Check(DiagnosticsReport.EnvironmentText().Contains(AppPaths.ConfigDir), "诊断信息能定位集中配置目录");
                var content = (FrameworkElement)settings.Content;
                content.Measure(new System.Windows.Size(880, 600)); content.Arrange(new Rect(0, 0, 880, 600)); content.UpdateLayout();
                Check(content.IsAncestorOf(settings.SaveAiKeyButton), "密钥操作位于独立插件设置界面");
                string preview = Path.GetFullPath("docs/screens/ai-configuration.png"); Directory.CreateDirectory(Path.GetDirectoryName(preview)!);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(880, 600, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(content); var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file = File.Create(preview); png.Save(file);
                var hostSettings = new alpha_island.Settings.SettingsWindow(false);
                try { SettingsForegroundChecks(hostSettings); } finally { hostSettings.ForceClose=true; hostSettings.Close(); }
            }
            finally { if (settings != null) { settings.Dispose(); } }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindow")]
    private static extern IntPtr SettingsTestGetWindow(IntPtr handle, uint command);

    private static void SettingsForegroundChecks(alpha_island.Settings.SettingsWindow settings)
    {
        var fullScreenPeer = new Window { Left = -32000, Top = -32000, Width = 1200, Height = 800,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Topmost = true };
        try
        {
            settings.Left = -32000; settings.Top = -32000; settings.ShowActivated = false;
            fullScreenPeer.Show(); settings.ShowInForeground(requestFocus: false);
            var own = new System.Windows.Interop.WindowInteropHelper(settings).Handle;
            var peer = new System.Windows.Interop.WindowInteropHelper(fullScreenPeer).Handle;
            bool above = false;
            for (IntPtr handle = SettingsTestGetWindow(peer, 3); handle != IntPtr.Zero; handle = SettingsTestGetWindow(handle, 3))
                if (handle == own) { above = true; break; }
            Check(!settings.Topmost && (alpha_island.Interop.NativeMethods.GetWindowLong(own, -20) & 8) == 0 && !above,
                "设置窗口处于普通层级，前台顶置应用可以覆盖设置（真实 Win32 层级）");
            Check(settings.Owner == null && SettingsTestGetWindow(own, 4) == IntPtr.Zero,
                "设置窗口不依附顶置的岛，独立参与窗口切换");
            fullScreenPeer.Topmost = false;
            settings.ShowInForeground(requestFocus: false);
            alpha_island.Interop.NativeMethods.SetWindowPos(peer, IntPtr.Zero, 0, 0, 0, 0, 0x0013);
            bool peerAbove = false;
            for (IntPtr handle = SettingsTestGetWindow(own, 3); handle != IntPtr.Zero; handle = SettingsTestGetWindow(handle, 3))
                if (handle == peer) { peerAbove = true; break; }
            Check(peerAbove && !settings.Topmost, "其他普通窗口可以置于设置之上");
            settings.WindowState = WindowState.Minimized;
            Check(!settings.Topmost, "设置窗口最小化不进入置顶层");
            settings.ShowInForeground(requestFocus: false);
            Check(settings.WindowState == WindowState.Normal && settings.IsVisible && !settings.Topmost, "重复打开恢复最小化设置且保持普通层级");
            settings.CloseButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(!settings.IsVisible && !settings.Topmost, "关闭按钮隐藏设置并释放置顶，其他窗口可正常操作");
            settings.ShowInForeground(requestFocus: false);
            var key = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                System.Windows.PresentationSource.FromVisual(settings), 0, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
            settings.RaiseEvent(key);
            Check(key.Handled && !settings.IsVisible && !settings.Topmost, "Esc 可退出设置且隐藏后不残留置顶窗口");
            settings.ShowInForeground(requestFocus: false); settings.Close();
            Check(!settings.IsVisible && !settings.Topmost, "系统关闭路径也释放设置窗口置顶");
        }
        finally { fullScreenPeer.Close(); }
    }
}
