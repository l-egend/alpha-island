using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Core;
using alpha_island.Plugins;
using alpha_island.Plugins.Isolation;
using Button = System.Windows.Controls.Button;
using Grid = System.Windows.Controls.Grid;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBox = System.Windows.Controls.TextBox;

internal static partial class Program
{
    private static void PluginDetailsAvailabilityChecks(alpha_island.MainWindow window, string testRoot)
    {
        string directory = Path.Combine(testRoot, "plugin-details"); Directory.CreateDirectory(directory);
        string api = Path.Combine(directory, "api"); Directory.CreateDirectory(api);
        string apiSource = ExternalPluginSource($"ApiChatPlugin/bin/{TestBuildConfiguration}/net8.0-windows");
        foreach (string file in Directory.GetFiles(apiSource).Where(file => file.EndsWith(".dll") || file.EndsWith(".deps.json") || Path.GetFileName(file) == "plugin.json"))
            File.Copy(file, Path.Combine(api, Path.GetFileName(file)));
        string probe = Path.Combine(directory, "probe"); Directory.CreateDirectory(probe);
        File.Copy(Path.GetFullPath($"tools/PluginFixture/bin/{TestBuildConfiguration}/net8.0-windows/AlphaIsland.PluginFixture.dll"), Path.Combine(probe, "AlphaIsland.PluginFixture.dll"));
        File.WriteAllText(Path.Combine(probe, "plugin.json"), JsonSerializer.Serialize(new
        { id = "fixture.settings-only", name = "配置进程测试", version = "1.0", apiVersion = 7, assembly = "AlphaIsland.PluginFixture.dll", entryType = "AlphaIsland.PluginFixture.SettingsOnlyProbePlugin" }));
        var options = new IslandOptions();
        SettingsRegistry.SetExtra(options, PluginHost.EnabledKey("ai.api-chat"), false);
        using var host = new PluginHost(window.Dispatcher, options, [directory], _ => { }, _ => { }, _ => { });
        host.DataRoot = Path.Combine(testRoot, "settings-data"); host.Discover();
        var settings = new alpha_island.Settings.SettingsWindow(false);
        using var source = new HwndSource(new HwndSourceParameters("Plugin details availability")
        { Width = 1100, Height = 1050, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        try
        {
            settings.LoadFrom(options); settings.BindPluginHost(host); settings.OpenPluginSettings();
            var content = (Grid)settings.Content; settings.Content = null; source.RootVisual = content; content.UpdateLayout(); PumpAnimations(30);
            var rows = (StackPanel)settings.FindName("PluginRows");
            var apiEntry = host.Entries.Single(entry => entry.Manifest!.Id == "ai.api-chat");
            var probeEntry = host.Entries.Single(entry => entry.Manifest!.Id == "fixture.settings-only");
            var sessions = AgentField<Dictionary<string, PluginHost.SettingsSession>>(settings, "_pluginSettingsSessions");
            Button Details(PluginHost.Entry entry) => Descendants<Button>(rows).Single(button => Equals(button.Tag, "plugin-settings:" + entry.Directory));
            PluginHost.SettingsSession Open(PluginHost.Entry entry)
            {
                Details(entry).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntilState(() => sessions.TryGetValue(entry.Directory, out var value) && !value.Loading, 15000);
                var session = sessions[entry.Directory];
                Check(session.Ready, "独立设置进程加载成功：" + entry.Manifest!.Id + " " + session.Error);
                return session;
            }
            RemotePluginView View(string name)
            {
                PumpUntilState(() => Descendants<RemotePluginView>(rows).Any(view => view.FindName(name) != null), 10000);
                return Descendants<RemotePluginView>(rows).Single(view => view.FindName(name) != null);
            }
            int Marks(string name) => File.Exists(Path.Combine(host.DataRoot, name + ".txt"))
                ? File.ReadAllText(Path.Combine(host.DataRoot, name + ".txt")).Count(c => c == ';') : 0;
            void Leave()
            {
                PrivateCall(settings, "SelectPageByTag", "general"); PumpAnimations(20);
                AwaitPlugin(host.ShutdownCompletion.ContinueWith(_ => true));
                Check(sessions.Count == 0, "离开插件页回收按需设置会话");
            }
            Check(host.Entries.All(entry => !entry.Running && entry.Instance == null) && sessions.Count == 0,
                "仅浏览关闭的插件列表不会启动后台或配置进程");
            var apiSession = Open(apiEntry); int apiPid = apiSession.ProcessId;
            var apiView = View("AiBaseUrlBox");
            Check(apiView.FindName("AiApiKeyBox") != null && apiView.FindName("AiModelBox") != null,
                "API 插件关闭时仍能显示接口地址、密钥框和模型选择");
            Check(!PluginHost.IsEnabled(options, "ai.api-chat") && !apiEntry.Running && apiEntry.Pages.Count == 0,
                "打开 API 配置不会启用插件或挂载对话卡片");
            string previews = Path.GetFullPath("bin/PluginSettings180"); Directory.CreateDirectory(previews);
            content.UpdateLayout();
            var preview = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            preview.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(preview));
            using (var file = File.Create(Path.Combine(previews, "disabled-api-settings.png"))) encoder.Save(file);
            ((TextBox)apiView.FindName("AiBaseUrlBox")).Text = "http://127.0.0.1:12345/v1";
            PumpUntilState(() => SettingsRegistry.GetText(options, "AiBaseUrl", "") == "http://127.0.0.1:12345/v1", 5000);
            Check(SettingsRegistry.GetText(options, "AiBaseUrl", "").EndsWith(":12345/v1", StringComparison.Ordinal),
                "独立 API 设置通过真实跨进程输入更新配置，不发送网络请求");
            Leave(); Check(!ProcessAlive(apiPid), "离开插件页后 API 配置工作进程退出");
            settings.OpenPluginSettings(); PumpAnimations(30);
            Check(sessions.Count == 0, "重新浏览插件页不会自动恢复已关闭的配置进程");
            var probeSession = Open(probeEntry); int probePid = probeSession.ProcessId; var probeView = View("ProbeEndpointBox");
            Check(Marks("service-start") == 0 && Marks("network-read") == 0 && Marks("page-created") == 0,
                "只打开配置时不启动后台服务、不读取数据源、不创建功能页面");
            ((Button)probeView.FindName("ProbeToolsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntilState(() => Descendants<System.Windows.Controls.TextBlock>(rows).Any(text => text.Text == "0/0/False"), 5000);
            Check(Descendants<System.Windows.Controls.TextBlock>(rows).Any(text => text.Text == "0/0/False"),
                "配置模式的工具和桌面数据请求及时返回，不执行后台工具也不等待超时");
            ((TextBox)probeView.FindName("ProbeEndpointBox")).Text = "saved-while-disabled";
            PumpUntilState(() => SettingsRegistry.GetText(options, "plugin/fixture.settings-only/endpoint", "") == "saved-while-disabled", 5000);
            Check(!PluginHost.IsEnabled(options, "fixture.settings-only"), "配置保存后仍保留插件关闭状态");
            Leave(); Check(!ProcessAlive(probePid), "关闭配置后没有残留的独立插件进程");

            SettingsRegistry.SetExtra(options, PluginHost.EnabledKey("fixture.settings-only"), true); host.Sync(options);
            PumpUntilState(() => probeEntry.Running, 10000);
            Check(Marks("service-start") == 1 && probeEntry.Pages.Count == 1, "正常启用插件仍启动后台服务并挂载页面");
            host.SetGameSuspended(true);
            PumpUntilState(() => probeEntry.GameSuspended && !probeEntry.Running, 10000);
            AwaitPlugin(probeEntry.Teardown.ContinueWith(_ => true));
            settings.LoadFrom(options); settings.OpenPluginSettings(); PumpAnimations(30);
            var gameSession = Open(probeEntry); var gameView = View("ProbeEndpointBox");
            Check(((TextBox)gameView.FindName("ProbeEndpointBox")).Text == "saved-while-disabled",
                "游戏暂停后独立设置继续显示之前保存的值");
            Check(probeEntry.GameSuspended && !probeEntry.Running && Marks("service-start") == 1,
                "游戏中修改配置不恢复已暂停的后台服务");
            Leave(); host.SetGameSuspended(false); PumpUntilState(() => probeEntry.Running, 10000);
            Check(Marks("service-start") == 2 && PluginHost.IsEnabled(options, "fixture.settings-only"),
                "游戏结束后按原开关恢复正常后台，与配置会话互不干扰");
            SettingsRegistry.SetExtra(options, PluginHost.EnabledKey("fixture.settings-only"), false); host.Sync(options);
            AwaitPlugin(probeEntry.Teardown.ContinueWith(_ => true));
            SettingsRegistry.SetExtra(options, "FixtureFailService", true);
            SettingsRegistry.SetExtra(options, PluginHost.EnabledKey("fixture.settings-only"), true); host.Sync(options);
            PumpUntilState(() => probeEntry.Error.Length > 0, 10000);
            AwaitPlugin(probeEntry.Teardown.ContinueWith(_ => true));
            settings.LoadFrom(options); settings.OpenPluginSettings(); PumpAnimations(30);
            var failedSession = Open(probeEntry); View("ProbeEndpointBox");
            Check(probeEntry.Error.Length > 0 && !probeEntry.Running && failedSession.Ready,
                "后台服务加载失败时仍可单独打开配置用于修复");
            int failedPid = failedSession.ProcessId;
            source.RootVisual = null; PumpAnimations(20); AwaitPlugin(host.ShutdownCompletion.ContinueWith(_ => true));
            Check(!ProcessAlive(failedPid) && sessions.Count == 0, "设置窗口卸载后回收全部按需进程");
            source.RootVisual = content; content.UpdateLayout(); settings.OpenPluginSettings(); PumpAnimations(20);
            var uninstallSession = Open(probeEntry); View("ProbeEndpointBox"); int uninstallPid = uninstallSession.ProcessId;
            Check(AwaitPlugin(host.UninstallAsync(probeEntry)), "配置界面打开时仍可正常卸载插件 " + probeEntry.UninstallError);
            Check(!ProcessAlive(uninstallPid) && !Directory.Exists(probe), "卸载先回收配置进程，再删除插件目录");
        }
        finally
        {
            source.RootVisual = null; settings.ForceClose = true; settings.Close();
            host.Dispose(); AwaitPlugin(host.ShutdownCompletion.ContinueWith(_ => true));
        }
    }
}
