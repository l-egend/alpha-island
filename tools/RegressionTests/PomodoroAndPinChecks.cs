using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Plugins;
using alpha_island.Settings;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;

#pragma warning disable CA1416 // The dedicated branch requires Windows 10 2004 or newer.

internal static partial class Program
{
    private static void PomodoroConfigurationChecks()
    {
        var options = new IslandOptions();
        SettingsRegistry.Write(options, "PomodoroFocusMinutes", 50);
        SettingsRegistry.Write(options, "PomodoroShortBreakMinutes", 5);
        SettingsRegistry.Write(options, "PomodoroUseLongBreak", true);
        SettingsRegistry.Write(options, "PomodoroLongBreakMinutes", 10);
        SettingsRegistry.Write(options, "PomodoroLongBreakEvery", 2);
        SettingsRegistry.Write(options, "PomodoroAutoCycle", true);
        SettingsRegistry.Write(options, "PomodoroCycleCount", 2);
        var restored = JsonSerializer.Deserialize<IslandOptions>(JsonSerializer.Serialize(options, IslandOptions.JsonOptions), IslandOptions.JsonOptions)!;
        var configuration = PomodoroOptions.From(restored);
        Check(configuration.FocusMinutes == 50 && configuration.UseLongBreak && configuration.CycleCount == 2, "番茄钟配置序列化后保留时长、长休息和循环计划");
        var timer = new PomodoroTimer(); timer.Configure(configuration);
        var now = DateTime.UtcNow;
        Check(timer.Remaining == TimeSpan.FromMinutes(50), "修改专注时长后未启动卡片立即显示新时长");
        timer.Start(now); timer.Pause(now.AddMinutes(7));
        timer.Configure(configuration with { FocusMinutes = 30 });
        Check(timer.Remaining == TimeSpan.FromMinutes(43) && !timer.Running, "调整设置保留已开始计时的暂停进度");
        timer.Start(now.AddHours(1));
        Check(timer.Tick(now.AddHours(1).AddMinutes(43)) && timer.IsBreak && !timer.IsLongBreak && timer.Running && timer.Remaining == TimeSpan.FromMinutes(5),
            "第一轮完成自动进入配置的短休息");
        now = now.AddHours(1).AddMinutes(48);
        Check(timer.Tick(now) && !timer.IsBreak && timer.Running && timer.Remaining == TimeSpan.FromMinutes(30), "休息完成使用新的专注时长自动开始第二轮");
        now = now.AddMinutes(30);
        Check(timer.Tick(now) && timer.IsLongBreak && timer.Remaining == TimeSpan.FromMinutes(10) && timer.CompletedFocus == 2, "到达指定轮数使用长休息");
        now = now.AddMinutes(10);
        Check(timer.Tick(now) && timer.CycleFinished && !timer.Running && !timer.IsBreak, "有限循环在最后一轮休息结束后停止");
        Check(!timer.Tick(now.AddDays(1)), "循环完成后不会重复触发完成提醒");
        timer.Start(now);
        Check(timer.Running && timer.CompletedFocus == 0 && !timer.CycleFinished, "完成后重新开始创建新循环");
        timer.Pause(now.AddSeconds(20)); timer.Configure(configuration with { ShowSeconds = false });
        Check(timer.DisplayRemaining() == "30 分钟", "隐藏秒数时显示向上取整的剩余分钟");
        timer.Reset();
        Check(timer.Remaining == TimeSpan.FromMinutes(50) && timer.CompletedFocus == 0 && !timer.Running, "重置清空已完成轮数并使用当前配置");
        SettingsRegistry.Write(options, "PomodoroFocusMinutes", -10); SettingsRegistry.Write(options, "PomodoroCycleCount", 10000);
        Check(PomodoroOptions.From(options).FocusMinutes == 1 && PomodoroOptions.From(options).CycleCount == 100, "非法时长与循环数按设置范围夹取");
        var manual = new PomodoroTimer(); manual.Configure(configuration with { AutoCycle = false });
        manual.Start(now); manual.Tick(now.AddHours(3));
        Check(manual.IsBreak && !manual.Running && !manual.Tick(now.AddDays(1)), "关闭自动循环后阶段结束等待手动开始且只提醒一次");
        manual.Start(now); manual.Tick(now.AddMinutes(5)); manual.Start(now); manual.Tick(now.AddMinutes(60));
        Check(manual.IsLongBreak, "手动计时也遵循长休息间隔");
        manual.Configure(configuration with { AutoCycle = false, UseLongBreak = false });
        Check(!manual.IsLongBreak && manual.Remaining == TimeSpan.FromMinutes(5), "等待开始长休息时可关闭长休息并切回短休息");
    }

    private static void FocusCardUiChecks(alpha_island.MainWindow window, Grid root)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Call(string name, params object?[] values)
        {
            var method = typeof(alpha_island.MainWindow).GetMethod(name, flags)!;
            var parameters = method.GetParameters();
            return method.Invoke(window, parameters.Select((p, i) => i < values.Length ? values[i] : p.DefaultValue).ToArray());
        }
        T Field<T>(string name) => (T)typeof(alpha_island.MainWindow).GetField(name, flags)!.GetValue(window)!;
        var pages = Field<IslandPageHost>("_pages"); var layers = Field<IslandLayerHost>("_layers");
        using var media = new alpha_island.Services.SmtcService(window.Dispatcher);
        media.SetDebugNowPlaying(new alpha_island.Services.NowPlayingInfo { Title = "顶置回归", State = alpha_island.Services.PlaybackState.Playing });
        typeof(alpha_island.MainWindow).GetField("_smtc", flags)!.SetValue(window, media);
        typeof(alpha_island.MainWindow).GetField("_weatherBusy", flags)!.SetValue(window, true);
        Call("SetMusicPinned", true); Call("SetLayerPinned", "download", true);
        foreach (string id in new[] { "focus", "weather", "screen-tools" })
        {
            Call("SelectComponent", id); Call("ToggleCardPin"); root.UpdateLayout();
            string key = "card/" + id;
            Check(layers.IsPinned(key) && layers.IsPinned("download") && Field<bool>("_isPinned")
                && Field<IslandState>("_state") == IslandState.Pill, id + " 独立顶置并保留原音乐和下载顶置");
            Check((UIElement)Call("GetNotifyLayerElement", key)! is { Visibility: Visibility.Visible }, id + " 顶置后显示自己的胶囊内容");
            Call("SetState", IslandState.Card);
            Check(pages.ActiveId == id, id + " 从胶囊展开返回自己的卡片");
            Call("ToggleCardPin");
            Check(!layers.IsPinned(key) && layers.IsPinned("download") && Field<bool>("_isPinned"), id + " 取消顶置不影响其他模块");
        }
        var plugin = new PluginIslandPage("probe", new("card", "测试插件", () => new Grid(), Glyph: "\uE7F6"), _ => { });
        window.CardViewport.Children.Add(plugin.View); pages.Register(plugin);
        var allOptions = new IslandOptions(); SettingsRegistry.SetExtra(allOptions, "AiEnabled", true); pages.Sync(allOptions);
        Call("SelectComponent", plugin.Id); Call("ToggleCardPin"); root.UpdateLayout();
        Check(layers.IsPinned("card/" + plugin.Id) && (string)Call("GetCurrentLayerGlyph", "card/" + plugin.Id)! == "\uE7F6",
            "插件卡片独立顶置并使用插件声明图标");
        Call("SetState", IslandState.Card);
        Check(pages.ActiveId == plugin.Id, "插件小岛点击展开返回插件卡片");
        Call("RemoveCardPinLayer", plugin.Id); pages.Unregister(plugin.Id); window.CardViewport.Children.Remove(plugin.View);
        Check(!layers.IsRegistered("card/" + plugin.Id) && !layers.IsPinned("card/" + plugin.Id) && layers.IsPinned("download"),
            "移除插件同步清除其顶置和图层并保留其他模块");
        bool allCards = true;
        foreach (var page in pages.Pages.Where(p => p.Id != IslandPageIds.Music && p.Id != IslandPageIds.Download).ToArray())
        {
            Call("SelectComponent", page.Id);
            string key = (string)Call("CardLayerForPage", page.Id)!;
            Call("ToggleCardPin");
            allCards &= layers.IsPinned(key) && layers.IsPinned("download");
            Call("SelectComponent", page.Id); Call("ToggleCardPin");
            allCards &= !layers.IsPinned(key) && layers.IsPinned("download");
        }
        Check(allCards, "所有已注册内置卡片都具有可独立切换的顶置目标");
        System.Console.WriteLine("Audited cards: " + string.Join(", ", pages.Pages.Select(p => p.Title)));
        Call("SelectComponent", "focus"); Call("ToggleCardPin");
        var timer = Field<PomodoroTimer>("_pomodoro");
        timer.Configure(new(FocusMinutes: 50)); Call("SyncFocus");
        var pill = (Grid)Call("GetNotifyLayerElement", "card/focus")!;
        Check(Descendants<TextBlock>(pill).Any(t => t.Text.Contains("50:00")), "顶置番茄钟胶囊同步可调的剩余时长");
        var pillTime = Descendants<TextBlock>(pill).Single(t => t.Name == "FocusPinTime");
        Check(pillTime.FontSize >= 26 && Grid.GetColumn(pillTime) == 2, "番茄钟主岛将倒计时独立放大在右侧");
        root.UpdateLayout();
        Check(pillTime.TransformToAncestor(pill).Transform(new System.Windows.Point()).X > pill.ActualWidth / 2,
            "倒计时在实际布局中位于主岛右半边");
        SaveThemePreview(pill, "docs/screens/pomodoro-pill.png");
        Call("SwitchMainIsland", "download"); root.UpdateLayout();
        var orbs = Field<Dictionary<string, Border>>("_satelliteOrbViews");
        var focusOrb = orbs["card/focus"];
        var orbTime = Descendants<TextBlock>(focusOrb).Single(t => t.Name == "OrbTime");
        Check(focusOrb.Visibility == Visibility.Visible && focusOrb.Width >= 100 && orbTime.Text == "50:00" && orbTime.FontSize >= 18,
            "番茄钟卫星小岛加宽并显示清晰的倒计时");
        timer.Start(DateTime.UtcNow); timer.Pause(DateTime.UtcNow.AddSeconds(70)); Call("SyncFocus");
        Check(orbTime.Text == pillTime.Text && orbTime.Text != "50:00", "卫星与主岛倒计时随计时变化同步更新");
        timer.Configure(timer.Options with { ShowSeconds = false }); Call("SyncFocus"); root.UpdateLayout();
        Check(focusOrb.Width >= 138 && orbTime.Text.Contains("分钟") && orbTime.DesiredSize.Width <= focusOrb.Width - 34,
            "仅显示分钟时小岛容纳中文时间而不截断");
        timer.Configure(new(FocusMinutes: 240)); timer.Reset(); Call("SyncFocus"); root.UpdateLayout();
        Check(orbTime.Text == "240:00" && orbTime.DesiredSize.Width <= focusOrb.Width - 34,
            "三位数分钟在缩略小岛完整显示");
        var rail = Field<SatelliteRail>("_satellites");
        var orbOrder = Field<List<string>>("_activeSatelliteOrder");
        bool noOverlap = true; double previousOuter = 0;
        foreach (string key in orbOrder)
        {
            string railKey = key == "music" ? "music-orb" : "orb-" + key;
            double width = key == "music" ? 36 : orbs[key].Width;
            double center = Math.Abs(rail.Offset(railKey));
            noOverlap &= center - width / 2 >= previousOuter + 7.9;
            previousOuter = center + width / 2;
        }
        Check(noOverlap, "加宽番茄钟后卫星轨道仍按实际宽度排列且互不重叠");
        timer.Configure(new(FocusMinutes: 50)); timer.Reset(); Call("SyncFocus"); root.UpdateLayout();
        PumpAnimations(400); SaveThemePreview(focusOrb, "docs/screens/pomodoro-satellite.png");
        Call("SwitchMainIsland", "card/focus");
        Call("SetState", IslandState.Card); root.UpdateLayout();
        var title = Descendants<TextBlock>(pages.ActivePage!.View).First(t => t.Text == "番茄钟");
        var doubleClick = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            { RoutedEvent = System.Windows.Input.Mouse.MouseDownEvent };
        typeof(System.Windows.Input.MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(doubleClick, 2);
        title.RaiseEvent(doubleClick);
        Check(timer.Running && doubleClick.Handled, "双击番茄钟内容可开始计时并阻止岛体点击冒泡");
        timer.Pause(DateTime.UtcNow); Call("SyncFocus"); root.UpdateLayout();
        SaveThemePreview(window.IslandBody, "docs/screens/pomodoro-card.png");

        var settings = new SettingsWindow(persistSettings: false);
        try
        {
            var configured = new IslandOptions();
            SettingsRegistry.Write(configured, "PomodoroFocusMinutes", 50);
            SettingsRegistry.Write(configured, "PomodoroSound", "完成");
            settings.LoadFrom(configured);
            Check(SettingsRegistry.All.Where(x => x.Key.StartsWith("Pomodoro")).All(x => settings.FindName(x.ControlName) != null),
                "12 个番茄钟配置都具有注册表绑定的设置控件");
            Check(((TextBox)settings.FindName("PomodoroFocusMinutesBox")).Text == "50"
                && ((ComboBox)settings.FindName("PomodoroSoundBox")).SelectedItem as string == "完成", "设置回填时长与提示音选择");
            var duration = (TextBox)settings.FindName("PomodoroLongBreakMinutesBox");
            Check(!duration.IsEnabled, "关闭长休息时相关输入置灰");
            ((CheckBox)settings.FindName("PomodoroUseLongBreakToggle")).IsChecked = true;
            Check(duration.IsEnabled, "启用长休息后输入可调整");
            ((TextBox)settings.FindName("PomodoroFocusMinutesBox")).Text = "45";
            ((ComboBox)settings.FindName("PomodoroSoundBox")).SelectedItem = "警告";
            typeof(SettingsWindow).GetMethod("ApplyRegistryControls", flags)!.Invoke(settings, null);
            var saved = (IslandOptions)typeof(SettingsWindow).GetField("_options", flags)!.GetValue(settings)!;
            Check(PomodoroOptions.From(saved).FocusMinutes == 45 && PomodoroOptions.From(saved).Sound == "警告",
                "番茄钟数字与提示音控件回写配置");
            ((CheckBox)settings.FindName("PomodoroPlaySoundToggle")).IsChecked = true;
            var section = (FrameworkElement)typeof(SettingsWindow).GetField("_pomodoroSettingsPanel", flags)!.GetValue(settings)!;
            ((System.Windows.Controls.Panel)section.Parent).Children.Remove(section);
            section.VerticalAlignment = VerticalAlignment.Top;
            using var settingsSource = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Pomodoro settings preview")
                { Width = 660, Height = 1200, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
            var surface = new Border { Padding = new Thickness(14), Child = section };
            settingsSource.RootVisual = surface;
            surface.Measure(new System.Windows.Size(660, 1200)); surface.Arrange(new Rect(0, 0, 660, 1200));
            PumpAnimations(20); surface.UpdateLayout();
            SaveThemePreview(section, "docs/screens/pomodoro-settings.png");
            Check(SettingsRegistry.All.Where(d => d.Key.StartsWith("Pomodoro") && d.Kind == SettingKind.Int)
                .All(d => ((TextBox)settings.FindName(d.ControlName)).ActualWidth >= 50)
                && Descendants<ComboBox>(section).Single().IsEnabled, "番茄钟数字输入和音效选择在设置布局中完整显示");
        }
        finally { settings.ForceClose = true; settings.Close(); }
    }
}
