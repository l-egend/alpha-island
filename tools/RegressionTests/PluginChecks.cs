using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using AlphaIsland.PluginSdk;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Plugins;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;

internal static partial class Program
{
    private static object? PrivateCall(object owner, string name, params object?[] values)
    {
        var method = owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var args = method.GetParameters().Select((p, i) => i < values.Length ? values[i] : p.DefaultValue).ToArray();
        return method.Invoke(owner, args);
    }
    private static void PluginAndSettingsChecks(alpha_island.MainWindow window, string testRoot)
    {
        var pages = (IslandPageHost)typeof(alpha_island.MainWindow).GetField("_pages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        string previous = pages.ActiveId;
        var previousState = typeof(alpha_island.MainWindow).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
        int builtins = pages.Count;
        string pluginRoot = Path.Combine(testRoot, "plugins"); Directory.CreateDirectory(pluginRoot);
        string build = "Debug", tfm = "net8.0-windows";
        string clockSource = Path.GetFullPath($"examples/ClockPlugin/bin/{build}/{tfm}");
        string fixtureSource = Path.GetFullPath($"tools/PluginFixture/bin/{build}/{tfm}");
        void WritePlugin(string folder, string id, string assembly, string type, string source, int api = 1)
        {
            string directory = Path.Combine(pluginRoot, folder); Directory.CreateDirectory(directory);
            foreach (string file in Directory.GetFiles(source).Where(f => f.EndsWith(".dll") || f.EndsWith(".deps.json")))
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), true);
            File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(new
            { id, name = id == "example.clock" ? "时钟示例" : id, version = "1.0.0", apiVersion = api, assembly, entryType = type, description = "独立插件模块" }));
        }
        WritePlugin("clock", "example.clock", "AlphaIsland.Examples.ClockPlugin.dll", "AlphaIsland.Examples.ClockPlugin.ClockPlugin", clockSource);
        foreach (string id in new[] { "fixture.probe", "fixture.init-fails", "fixture.start-fails", "fixture.callback-fails", "fixture.settings-fails" })
            WritePlugin(id, id, "AlphaIsland.PluginFixture.dll", "AlphaIsland.PluginFixture.ProbePlugin", fixtureSource);
        WritePlugin("z-duplicate", "example.clock", "AlphaIsland.Examples.ClockPlugin.dll", "AlphaIsland.Examples.ClockPlugin.ClockPlugin", clockSource);
        WritePlugin("z-future", "fixture.future", "AlphaIsland.PluginFixture.dll", "AlphaIsland.PluginFixture.ProbePlugin", fixtureSource, api: 99);
        WritePlugin("z-escape", "fixture.escape", "../AlphaIsland.PluginFixture.dll", "AlphaIsland.PluginFixture.ProbePlugin", fixtureSource);
        string malformed = Path.Combine(pluginRoot, "z-malformed"); Directory.CreateDirectory(malformed); File.WriteAllText(Path.Combine(malformed, "plugin.json"), "invalid json");
        var options = new IslandOptions();
        var viewport = (Grid)window.FindName("CardViewport");
        using var host = new PluginHost(window.Dispatcher, options, new[] { pluginRoot },
            page => { viewport.Children.Add(page.View); pages.Register(page); },
            page => { pages.Unregister(page.Id); viewport.Children.Remove(page.View); },
            id => PrivateCall(window, "SelectComponent", id));
        host.Changed += () => PrivateCall(window, "SwitchCardPage", Math.Max(0, pages.ActiveIndex), false, false);
        try
        {
            host.Discover();
            Check(host.Entries.Count == 10 && host.Entries.Count(e => e.Manifest == null) == 4,
                "插件清单拒绝重复 Id、跨目录 DLL、未来 API 和损坏 JSON，正常插件仍被发现");
            Check(host.Entries.All(e => e.Instance == null) && pages.Count == builtins, "发现插件只读清单，新插件默认禁用且不执行程序集");
            void Enable(string id, bool enabled) { options.Extras[PluginHost.EnabledKey(id)] = JsonSerializer.SerializeToElement(enabled); host.Sync(options); }
            Enable("example.clock", true);
            var clock = host.Entries.Single(e => e.Manifest?.Id == "example.clock");
            var clockPage = clock.Pages.Single();
            Check(clock.Running && pages.Count == builtins + 1 && ReferenceEquals(clockPage.View, viewport.Children[viewport.Children.Count - 1])
                && clock.Context!.Settings.Count == 4 && clock.Started.Count == 1, "真实示例 DLL 加载并注册卡片、四类设置和服务");
            Check(clock.Instance!.GetType().Assembly != typeof(PluginApi).Assembly && typeof(IIslandPlugin).IsAssignableFrom(clock.Instance.GetType()),
                "插件独立加载上下文共享 SDK 类型身份");
            Check(clock.Context!.PluginDirectory == Path.Combine(pluginRoot, "clock"), "SDK 提供独立插件资产目录");
            var menu = new alpha_island.Controls.ComponentRadialMenu(window.IslandBody.Background, System.Windows.Media.Brushes.Cyan);
            menu.SetPages(pages.Pages, pages.ActiveId, window.IslandBody.Background);
            Check(menu.ItemIds.Contains(clockPage.Id) && Descendants<TextBlock>(menu).Any(t => t.Text == clockPage.Glyph),
                "插件卡片自动进入组件圆形菜单并使用自己的图标");
            clock.Context.OpenPage("clock"); PumpAnimations(30);
            ((FrameworkElement)window.IslandBody).UpdateLayout();
            Check(pages.ActiveId == clockPage.Id && clockPage.View.IsHitTestVisible && clockPage.View.Visibility == Visibility.Visible,
                "插件 OpenPage 接入主岛实际展开与卡片切换");
            SaveThemePreview(window.IslandBody, "docs/screens/plugin-clock.png");
            pages.SetActiveIndex(pages.IndexOfOrDefault(previous));
            PrivateCall(window, "SwitchCardPage", pages.ActiveIndex, false, false);
            int oldIndex = pages.ActiveIndex;
            pages.Register(new DelegateIslandPage("plugin:test-earlier", "临时页", new Grid(), order: -100, preferredHeight: 214));
            Check(pages.ActiveId == previous && pages.ActiveIndex == oldIndex + 1, "动态注册前置页面保持原有活动页面身份");
            pages.Unregister("plugin:test-earlier");
            Check(pages.ActiveId == previous && pages.ActiveIndex == oldIndex, "撤销页面重排不误切到相邻卡片");

            SettingsPluginMotionChecks(host, options, clock);
            clockPage = clock.Pages.Single();
            options.Extras[PluginHost.SettingKey("example.clock", "caption")] = JsonSerializer.SerializeToElement("独立模块");
            options.Extras[PluginHost.SettingKey("example.clock", "size")] = JsonSerializer.SerializeToElement(500d);
            options.Extras[PluginHost.SettingKey("example.clock", "format")] = JsonSerializer.SerializeToElement("无效格式");
            host.Sync(options);
            Check(clock.Context!.GetSetting("size", 0d) == 44 && clock.Context.GetSetting("format", "") == "24 小时",
                "插件数值设置夹取上限，无效枚举回退已验证的默认值");
            Check(Descendants<TextBlock>(clockPage.View).Any(t => t.Text == "独立模块"), "设置变更通过插件回调即时更新真实视图");
            var lifetime = clock.Context.Lifetime;
            pages.SetActiveIndex(pages.IndexOf(clockPage.Id));
            Enable("example.clock", false);
            Check(!clock.Running && lifetime.IsCancellationRequested && pages.IndexOf(clockPage.Id) < 0
                && !viewport.Children.Contains(clockPage.View) && pages.ActiveId != clockPage.Id, "禁用活动插件取消任务、移除视图并切回可用卡片");
            using (var released = File.Open(Path.Combine(pluginRoot, "clock", "AlphaIsland.Examples.ClockPlugin.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(released.CanWrite, "停用托管插件后 DLL 可独占打开，WPF 缓存不再锁住磁盘文件");
            Enable("example.clock", true);
            Check(clock.Running && clock.Context!.GetSetting("caption", "") == "独立模块" && pages.Count == builtins + 1,
                "重新启用插件保留自定义设置且不会重复注册页面");
            host.Discover();
            Check(clock.Running && pages.Count == builtins + 1, "刷新列表保留运行中的插件实例和页面");

            Enable("fixture.probe", true);
            var probe = host.Entries.Single(e => e.Manifest?.Id == "fixture.probe"); var probeInstance = probe.Instance!;
            bool Property(string name) => (bool)probeInstance.GetType().GetProperty(name)!.GetValue(probeInstance)!;
            var probeToken = (CancellationToken)probeInstance.GetType().GetProperty("Token")!.GetValue(probeInstance)!;
            Check(Property("Started") && !Property("Stopped"), "插件后台服务在启用时启动");
            Enable("fixture.probe", false);
            Check(Property("Stopped") && Property("Disposed") && probeToken.IsCancellationRequested, "禁用执行服务 Stop、入口 Dispose 和生命周期取消");
            foreach (string id in new[] { "fixture.init-fails", "fixture.start-fails" })
            {
                Enable(id, true); var failed = host.Entries.Single(e => e.Manifest?.Id == id);
                Check(failed.Attempted && failed.Instance == null && failed.Context == null && failed.Error.Length > 0
                    && pages.IndexOf(PluginHost.PageId(id, "probe")) < 0, "插件失败原子回滚：" + id);
                string failure = failed.Error; host.Sync(options);
                Check(failed.Error == failure && failed.Instance == null && clock.Running, "失败插件不反复重启且其它插件继续运行：" + id);
            }
            Enable("fixture.callback-fails", true);
            var callback = host.Entries.Single(e => e.Manifest?.Id == "fixture.callback-fails");
            pages.SetActiveIndex(pages.IndexOf(callback.Pages.Single().Id)); PumpAnimations(30);
            Check(callback.Error.Contains("activation failed") && callback.Instance == null && clock.Running,
                "页面生命周期异常撤下故障插件，宿主和其它插件可继续工作");
            Enable("fixture.settings-fails", true); host.Sync(options);
            Check(host.Entries.Single(e => e.Manifest?.Id == "fixture.settings-fails").Error.Contains("settings failed") && clock.Running,
                "设置回调异常隔离到单个插件");
            var saved = JsonSerializer.Serialize(options); var restored = JsonSerializer.Deserialize<IslandOptions>(saved)!.Normalized();
            Check(PluginHost.IsEnabled(restored, "example.clock") && restored.Extras[PluginHost.SettingKey("example.clock", "caption")].GetString() == "独立模块",
                "插件开关和设置随现有 options.json 格式持久化往返");
            var lastLifetime = clock.Context!.Lifetime;
            host.Dispose();
            Check(lastLifetime.IsCancellationRequested && pages.Count == builtins && host.Entries.All(e => e.Instance == null), "退出宿主清理所有插件且恢复内置页面数量");
        }
        finally
        {
            host.Dispose(); pages.SetActiveIndex(pages.IndexOfOrDefault(previous));
            PrivateCall(window, "SwitchCardPage", pages.ActiveIndex, false, false);
            PrivateCall(window, "SetState", previousState, true, previous);
            typeof(alpha_island.MainWindow).GetField("_componentCardAwaitingPointer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
        }
    }

    private static void SettingsPluginMotionChecks(PluginHost host, IslandOptions options, PluginHost.Entry clock)
    {
        bool reduce = MotionPolicy.ReduceMotion, save = MotionPolicy.PowerSave; int tier = MotionPolicy.RenderTier;
        var settings = new alpha_island.Settings.SettingsWindow(persistSettings: false) { ForceClose = true };
        try
        {
            settings.BindPluginHost(host); settings.LoadFrom(options);
            settings.OptionsChanged += (_, updated) => { options.CopyFrom(updated); host.Sync(options); };
            var root = (Grid)settings.Content; settings.Content = null; root.Resources = settings.Resources;
            using var source = new HwndSource(new HwndSourceParameters("Settings plugin regression")
            { Width = 880, Height = 600, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
            source.RootVisual = root; PumpAnimations(20); root.UpdateLayout();
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            PrivateCall(settings, "SelectPageByTag", "plugins"); PumpAnimations(45); root.UpdateLayout();
            var pluginPage = (FrameworkElement)settings.FindName("Page_Plugins");
            Check(pluginPage.Opacity > 0.15 && pluginPage.Opacity < 1 && ((TranslateTransform)pluginPage.RenderTransform).Y > 0,
                $"设置页切换具有真实淡入和短距离位移动画（opacity={pluginPage.Opacity:0.000}, y={((TranslateTransform)pluginPage.RenderTransform).Y:0.000}, loaded={pluginPage.IsLoaded}）");
            PrivateCall(settings, "SelectPageByTag", "system"); PumpAnimations(30); PrivateCall(settings, "SelectPageByTag", "plugins");
            PumpAnimations(250); root.UpdateLayout();
            Check(pluginPage.Opacity == 1 && ((TranslateTransform)pluginPage.RenderTransform).Y == 0
                && ((FrameworkElement)settings.FindName("Page_System")).Visibility == Visibility.Collapsed,
                "快速连续切换设置页恢复正确页面、透明度和位置");
            var hidePaused = (CheckBox)settings.FindName("MediaHideWhenPausedToggle");
            Check(hidePaused.IsChecked == false, "暂停隐藏设置按默认值正确回填");
            hidePaused.IsChecked = true;
            Check(SettingsRegistry.GetBool(options, "MediaHideWhenPaused", false), "暂停隐藏开关真实点击通过设置注册表写回");
            hidePaused.IsChecked = false;
            Check(!SettingsRegistry.GetBool(options, "MediaHideWhenPaused", true), "暂停隐藏开关可关闭并恢复原显示行为");
            var editors = (StackPanel)settings.FindName("PluginRows");
            var caption = Descendants<TextBox>(editors).Single(t => Equals(t.Tag, PluginHost.SettingKey("example.clock", "caption")));
            caption.Text = "插件卡片标题"; PumpAnimations(20);
            Check(clock.Context!.GetSetting("caption", "") == "插件卡片标题"
                && ReferenceEquals(caption, Descendants<TextBox>(editors).Single(t => Equals(t.Tag, caption.Tag))),
                "插件设置实时保存且编辑时不会重建输入框或丢失焦点");
            var otherRows = editors.Children.Cast<UIElement>().Skip(1).ToArray();
            foreach (var row in otherRows) editors.Children.Remove(row);
            root.UpdateLayout(); SaveThemePreview(root, "docs/screens/settings-plugins.png");
            foreach (var row in otherRows) editors.Children.Add(row);
            Check(Descendants<CheckBox>(editors).Any(c => Equals(c.Tag, PluginHost.EnabledKey("example.clock"))), "设置插件页生成按模块开关");
            var enabledToggle = Descendants<CheckBox>(editors).Single(c => Equals(c.Tag, PluginHost.EnabledKey("example.clock")));
            enabledToggle.IsChecked = false; PumpAnimations(30);
            Check(!clock.Running && !PluginHost.IsEnabled(options, "example.clock"), "插件管理页关闭开关实际停用插件");
            Descendants<CheckBox>(editors).Single(c => Equals(c.Tag, PluginHost.EnabledKey("example.clock"))).IsChecked = true; PumpAnimations(30);
            Check(clock.Running && clock.Context!.GetSetting("caption", "") == "插件卡片标题", "插件管理页重新开启恢复插件和设置");
            var nav = (FrameworkElement)settings.FindName("NavPane");
            PrivateCall(settings, "ApplyNavExpanded", false, true); PumpAnimations(45);
            Check(nav.Width > 48 && nav.Width < 244, "导航栏收起使用平滑宽度过渡");
            PrivateCall(settings, "ApplyNavExpanded", true, true); PumpAnimations(260);
            Check(nav.Width == 244, "导航栏动画反向切换不会被旧完成回调缩回");
            var switches = new StackPanel(); var first = new CheckBox { Style = (Style)settings.FindResource("W11Switch"), IsChecked = false };
            var second = new CheckBox { Style = first.Style, IsChecked = false }; switches.Children.Add(first); switches.Children.Add(second); root.Children.Add(switches);
            PumpAnimations(20); root.UpdateLayout();
            var firstTrack = (Border)first.Template.FindName("Track", first); var secondTrack = (Border)second.Template.FindName("Track", second);
            Color unchanged = ((SolidColorBrush)secondTrack.Background).Color;
            first.IsChecked = true; PumpAnimations(40);
            var knob = (FrameworkElement)first.Template.FindName("Knob", first); var transform = (TranslateTransform)knob.RenderTransform;
            Check(transform.X > 0 && transform.X < 20 && ((SolidColorBrush)secondTrack.Background).Color == unchanged
                && !ReferenceEquals(firstTrack.Background, secondTrack.Background), "设置开关滑动与渐变独立，不污染其它开关主题笔刷");
            first.IsChecked = false; PumpAnimations(200);
            Check(transform.X == 0, "快速反向切换开关回到关闭位置");
            first.IsChecked = true; PrivateCall(settings, "SelectPageByTag", "audio"); PrivateCall(settings, "ApplyNavExpanded", false, true); PumpAnimations(30);
            MotionPolicy.Apply(true, false);
            var audio = (FrameworkElement)settings.FindName("Page_Audio");
            Check(transform.X == 20 && audio.Opacity == 1 && nav.Width == 48, "途中开启减弱动效立即完成页面、导航栏与开关动画");
            MotionPolicy.Apply(false, true); first.IsChecked = false; PrivateCall(settings, "SelectPageByTag", "plugins");
            Check(transform.X == 0 && pluginPage.Opacity == 1, "节能模式下设置切换直接到位");
            MotionPolicy.Apply(false, false); MotionPolicy.OverrideRenderTierForDebug(0); first.IsChecked = true;
            PrivateCall(settings, "SelectPageByTag", "audio");
            Check(transform.X == 20 && audio.Opacity == 1, "软件渲染下自动跳过设置动画");
            root.Children.Remove(switches); source.RootVisual = null;
        }
        finally { settings.Close(); MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduce, save); }
    }
}
