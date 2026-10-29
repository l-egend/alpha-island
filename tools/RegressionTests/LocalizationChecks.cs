using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Plugins;
using alpha_island.Plugins.Isolation;
using alpha_island.Services;
using L = AlphaIsland.PluginSdk.Localization.UiText;

internal static partial class Program
{
    private static void LocalizationCoreChecks()
    {
        var numeric = CultureInfo.CurrentCulture;
        var windowsLanguage = CultureInfo.CurrentUICulture;
        Check(L.ResolveLanguage("system", CultureInfo.GetCultureInfo("en-GB")) == L.English
            && L.ResolveLanguage("system", CultureInfo.GetCultureInfo("zh-TW")) == L.Chinese
            && L.ResolveLanguage("system", CultureInfo.GetCultureInfo("fr-FR")) == L.English,
            "系统语言归并到支持的中英文，其他语言使用英文");
        var options = new IslandOptions(); SettingsRegistry.Write(options, "InterfaceLanguage", "invalid");
        Check(SettingsRegistry.GetText(options, "InterfaceLanguage", "") == "system", "无效语言设置归一化，旧配置无需迁移");
        SettingsRegistry.Write(options, "InterfaceLanguage", L.English);
        var saved = JsonSerializer.Deserialize<IslandOptions>(JsonSerializer.Serialize(options))!;
        Check(SettingsRegistry.GetText(saved, "InterfaceLanguage", "") == L.English
            && SettingsRegistry.Find("InterfaceLanguage")?.NeedsRestart == true, "语言选择可保存并明确需要重启");
        L.Initialize(L.English);
        Check(L.ResolveLanguage("system") == (windowsLanguage.TwoLetterISOLanguageName == "zh" ? L.Chinese : L.English),
            "手动切换语言后，跟随系统仍使用原 Windows 显示语言而非已切换的界面文化");
        Check(L.T("欢迎回来") == "Welcome back" && L.ByKey(L.KeyFor("天气")) == "Weather", "英文资源在共享程序集内可读取，XAML 与代码使用同一字典");
        Check(CultureInfo.CurrentCulture.Equals(numeric), "界面语言不改变数值输入和协议使用的区域格式");
        string device = "我的键盘 {private}", message = "用户聊天：不要翻译我";
        Check(L.F($"已连接设备 · {7} 项") == "Connected devices · 7"
            && L.F($"{device} 正在使用麦克风") == device + " is using the microphone"
            && L.T(message) == message, "翻译格式模板但不改写设备名、用户输入或插值中的花括号");
        var invalid = new List<string>();
        foreach (var pair in L.EnglishStrings)
        {
            var placeholders = Regex.Matches(pair.Key, @"(?<!\{)\{\d+(?:,[^}:]+)?(?::[^}]+)?\}").Select(m => m.Value).Order().ToArray();
            var translated = Regex.Matches(pair.Value, @"(?<!\{)\{\d+(?:,[^}:]+)?(?::[^}]+)?\}").Select(m => m.Value).Order().ToArray();
            if (!placeholders.SequenceEqual(translated) || pair.Value.Length == 0) invalid.Add(pair.Key);
            if (placeholders.Length > 0)
                try { CompositeFormat.Parse(pair.Value); } catch (FormatException) { invalid.Add(pair.Key); }
        }
        Check(invalid.Count == 0, "全部英文文案保留原始格式占位符且模板格式有效：" + string.Join(" / ", invalid.Take(3)));
        var stamp = new DateTimeOffset(2026, 10, 29, 9, 12, 0, TimeSpan.Zero);
        Check(NotificationTimestamp.Format(stamp, stamp).StartsWith("Today ", StringComparison.Ordinal)
            && NotificationTimestamp.Format(stamp.AddDays(-1), stamp).StartsWith("Yesterday ", StringComparison.Ordinal), "通知时间使用英文日期标签");
        Check(WeatherService.Describe(2) == "Partly cloudy" && StatusDisplayOptions.Label("memory") == "Memory usage"
            && GreetingPresetCatalog.WelcomeText == "Welcome back", "天气、系统状态和欢迎提示已英文化");
        EnglishReminderChecks();
        L.Initialize(L.Chinese);
        Check(L.T("欢迎回来") == "欢迎回来" && GreetingPresetCatalog.Labels[1].StartsWith("奶龙", StringComparison.Ordinal),
            "切回中文后显示文案恢复，欢迎组合不缓存错误语言");
    }

    private static void EnglishReminderChecks()
    {
        var now = new DateTimeOffset(2026, 10, 29, 8, 0, 0, TimeSpan.Zero);
        var zone = TimeZoneInfo.Utc;
        ReminderParseResult Parse(string text) => ReminderTextParser.Parse(text, now, zone);
        Check(Parse("Remind me to drink water in 30 minutes").Draft is { Title: "drink water" } relative
            && relative.Due == now.AddMinutes(30), "英文相对提醒保留事项并正确计算时间");
        Check(Parse("Please remind me in 1 hour and 15 minutes to stretch").Draft?.Due == now.AddMinutes(75), "英文组合时长提醒");
        Check(Parse("Remind me tomorrow at 9 am to call Alex").Draft is { Title: "call Alex" } tomorrow
            && tomorrow.Due == now.AddDays(1).AddHours(1), "英文明确日期与上午时刻");
        Check(Parse("Remind me to focus every weekday at 9:30").Draft is { } weekdays
            && weekdays.Weekdays.SequenceEqual(new[] { 1, 2, 3, 4, 5 }), "英文工作日重复提醒");
        Check(Parse("Remind me to rest every 2 hours").Draft?.IntervalMinutes == 120
            && Parse("Remind me to review every Friday at 5 pm").Draft?.Weekdays.SequenceEqual(new[] { 5 }) == true, "英文间隔和每周提醒");
        Check(Parse("Remind me to rest later").Draft == null && Parse("Remind me to rest in 0 seconds").Draft == null
            && Parse("Remind me at 25:00 to rest").Draft == null && Parse("Remind me every 1 second to rest").Draft == null,
            "英文含糊、无效时刻和过短重复间隔不创建提醒");
        Check(!Parse("What does remind me in 30 minutes mean?").IsRequest
            && ReminderTextParser.IsListRequest("List my reminders") && ReminderTextParser.CancellationTitle("Cancel reminder drink water") == "drink water",
            "英文查看与取消指令可用，解释性问题不被误设为提醒");
        Check(Parse("30分钟后提醒我喝水").Draft?.Due == now.AddMinutes(30), "英文界面仍接受原有中文提醒指令");
    }

    private static void LocalizationUiChecks(alpha_island.MainWindow window, string root)
    {
        Check(Descendants<TextBlock>((FrameworkElement)window.Content).Any(text => text.Text == "Master volume"), "主岛卡片从 XAML 读取英文标题");
        Check(new DelegateIslandPage("volume", "系统音量调节", new Grid(), 0, 200).Title == "Volume controls",
            "组件菜单翻译页面标题，同时保留固定页面标识");
        var options = new IslandOptions(); SettingsRegistry.Write(options, "InterfaceLanguage", L.English);
        var settings = new alpha_island.Settings.SettingsWindow(false, Path.Combine(root, "language-secret.dat"),
            greetingAssetsDirectory: Path.Combine(root, "greetings"));
        try
        {
            settings.LoadFrom(options);
            Check(settings.Title == "Alpha Island Settings", "设置窗口标题已切换为英文");
            var choice = (System.Windows.Controls.ComboBox)settings.FindName("InterfaceLanguageBox");
            Check(choice.SelectedValue as string == L.English && choice.Items.Count == 3, "语言设置有跟随系统、中、英三个稳定选项");
            var general = (StackPanel)settings.FindName("Page_General");
            var content = (FrameworkElement)settings.Content;
            foreach (int width in new[] { 880, 780 })
            {
                using var source = ThemeWindowSource(settings, width, 680, out var host);
                foreach (string page in new[] { "general", "appearance", "media", "focus", "behavior", "notifications", "system", "assistant", "ai", "plugins", "about" })
                {
                    PrivateCall(settings, "SelectPageByTag", page); host.UpdateLayout(); PumpAnimations(30); host.UpdateLayout();
                    var problems = Descendants<TextBlock>(host).Where(text => text.IsVisible && text.Text.Length > 0
                        && text.Style == settings.FindResource("W11RowLabel") && text.ActualWidth > 0
                        && text.TextWrapping == TextWrapping.NoWrap
                        && new System.Windows.Media.FormattedText(text.Text, L.Culture, System.Windows.FlowDirection.LeftToRight,
                            new System.Windows.Media.Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
                            text.FontSize, text.Foreground, 1).WidthIncludingTrailingWhitespace > text.ActualWidth + 2).Select(text => text.Text).ToArray();
                    Check(problems.Length == 0, $"{width} DIP / {page}：英文设置标签完整排版 " + string.Join(" / ", problems.Take(3)));
                    if (width == 880) SaveThemePreview(host, "bin/LocalizationAudit/previews/settings-" + page + ".png");
                }
                host.Child = null; settings.Content = content;
            }
            PrivateCall(settings, "SelectPageByTag", "general");
            ((System.Windows.Controls.TextBox)settings.FindName("SearchBox")).Text = "appearance";
            var navigation = (StackPanel)settings.FindName("NavPanel");
            Check(navigation.Children.OfType<System.Windows.Controls.RadioButton>().Any(item => Equals(item.Tag, "appearance")
                && item.Visibility == Visibility.Visible), "英文设置搜索可以定位外观页面");
            choice.SelectedValue = L.Chinese;
            var stored = AgentField<IslandOptions>(settings, "_options");
            Check(SettingsRegistry.GetText(stored, "InterfaceLanguage", "") == L.Chinese && L.Language == L.English,
                "切换选项只保存偏好，重启前运行中的插件和窗口语言保持一致");
        }
        finally { settings.ForceClose = true; settings.Close(); }
        LocalizationPluginChecks(window, root);
    }

    private static void LocalizationPluginChecks(alpha_island.MainWindow window, string root)
    {
        string target = Path.Combine(root, "english-worker"); Directory.CreateDirectory(target);
        string source = ExternalPluginSource($"ApiChatPlugin/bin/{TestBuildConfiguration}/net8.0-windows");
        foreach (string file in Directory.GetFiles(source).Where(file => file.EndsWith(".dll") || file.EndsWith(".deps.json") || file.EndsWith("plugin.json")))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        var manifest = PluginManifest.Read(Path.Combine(target, "plugin.json"));
        using var session = new PluginProcessSession(window.Dispatcher);
        try
        {
            AwaitPlugin(session.StartAsync(new(target, manifest.Assembly, manifest.EntryType, manifest.Id,
                Path.Combine(root, "worker-data"), new Dictionary<string, JsonElement>(), SettingsOnly: true, InterfaceLanguage: L.English), CancellationToken.None));
            var frame = AwaitPlugin(session.Request<RemoteFrame>("frame", new RemoteViewRequest("$settings", 600, 680)));
            Check(frame.Elements.Any(element => element.Text == "Manage chat data")
                && frame.Elements.Any(element => element.Text == "Fetch models"), "真实隔离插件收到宿主语言，设置与按钮均为英文");
            var connect = frame.Elements.Single(element => element.Name == "ConnectAiModelsButton");
            Check(new System.Windows.Media.FormattedText(connect.Text, L.Culture, System.Windows.FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface(connect.FontFamily), connect.FontSize, System.Windows.Media.Brushes.White, 1).Width + 28 <= connect.Width,
                "英文模型连接按钮在插件中完整显示");
            var remote = new RemotePluginView("$settings", _ => Task.CompletedTask, error => throw error);
            try
            {
                remote.Apply(frame); remote.Measure(new System.Windows.Size(600, 680)); remote.Arrange(new Rect(0, 0, 600, 680)); remote.UpdateLayout();
                Check(frame.Elements.Where(element => element.Kind == "text" && element.Wrap).Any(element => element.LineHeight > 0)
                    && Descendants<TextBlock>(remote).Any(text => text.LineHeight == 18), "隔离插件保留多行提示的实际行高，避免英文换行后裁切");
                SaveThemePreview(remote, "bin/LocalizationAudit/previews/api-plugin.png");
            }
            finally { remote.Close(); }
        }
        finally { session.Dispose(); AwaitPlugin(session.Completion.ContinueWith(_ => true)); }
        string earbuds = Path.Combine(root, "english-earbuds"); Directory.CreateDirectory(earbuds);
        string earSource = ExternalPluginSource($"OppoPodsPlugin/bin/{TestBuildConfiguration}/net8.0-windows10.0.19041.0");
        foreach (string file in Directory.GetFiles(earSource).Where(file => file.EndsWith(".dll") || file.EndsWith(".deps.json") || file.EndsWith("plugin.json")))
            File.Copy(file, Path.Combine(earbuds, Path.GetFileName(file)));
        var earManifest = PluginManifest.Read(Path.Combine(earbuds, "plugin.json"));
        using var earSession = new PluginProcessSession(window.Dispatcher);
        try
        {
            var registry = AwaitPlugin(earSession.StartAsync(new(earbuds, earManifest.Assembly, earManifest.EntryType, earManifest.Id,
                Path.Combine(root, "ear-data"), new Dictionary<string, JsonElement>(), SettingsOnly: true, InterfaceLanguage: L.English), CancellationToken.None));
            var model = registry.Settings.Single(setting => setting.Key == "model");
            Check(model.Label == "Headphone model" && model.Choices?.First() == "自动识别"
                && JsonSerializer.SerializeToElement(model.DefaultValue).GetString() == "自动识别",
                "真实耳机插件翻译设置标签，自动识别的存储值与连接逻辑保持兼容");
        }
        finally { earSession.Dispose(); AwaitPlugin(earSession.Completion.ContinueWith(_ => true)); }
    }
}
