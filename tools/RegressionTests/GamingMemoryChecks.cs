using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Plugins.Isolation;
using alpha_island.Plugins;
using alpha_island.Services;

internal static partial class Program
{
    private static void GamingMemoryChecks(alpha_island.MainWindow window, Grid root, string testRoot)
    {
        var failures = new List<Exception>();
        void Case(Action action) { try { action(); } catch (Exception error) { failures.Add(error); Console.WriteLine("FAIL: " + error.Message); } }
        var options = AgentField<IslandOptions>(window, "_options");
        var saved = new Dictionary<string, JsonElement>(options.Extras);
        var icon = new SemanticIcon { Width = 32, Height = 32, Shape = IconShape.Agent };
        var clips = (IDictionary)typeof(AgentAvatarMotion).GetField("Clips", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        root.Children.Add(icon);
        try
        {
            SettingsRegistry.Write(options, "NotificationGameQuietEnabled", true);
            foreach (var state in new[] { AgentRunState.Idle, AgentRunState.Thinking, AgentRunState.Streaming })
                foreach (string source in new[] { "codex", "antigravity", "deepseek-harness", "claude-code" })
                    AgentAvatarMotion.Configure(icon, source, state, null);
            Check(clips.Count is > 0 and <= 8, "内存探针通过真实播放路径加载四家助手动画，并保持缓存有界");
            PrivateCall(window, "ApplyGameActivity", new GameActivityState(1, true, "内存回归", "fixture", 1));
            Case(() => Check(clips.Count == 0 && !AgentAvatarMotion.ClockRunning, "游戏模式释放共享动画缓存并停止助手帧时钟"));
            AgentAvatarMotion.Configure(icon, "codex", AgentRunState.ToolWait, null);
            Case(() => Check(clips.Count == 0, "游戏后台的状态更新不会重新解码并缓存整段动画"));
            PrivateCall(window, "ApplyGameActivity", GameActivityState.Inactive);
            Case(() => Check(icon.Source is BitmapSource { PixelWidth: 192 } && clips.Count > 0,
                "退出游戏后按当前助手状态恢复完整分辨率动画"));
        }
        finally
        {
            PrivateCall(window, "ApplyGameActivity", GameActivityState.Inactive);
            AgentAvatarMotion.Release(icon); root.Children.Remove(icon); options.Extras = saved;
        }
        string directory = Path.Combine(testRoot, "lazy-worker"); Directory.CreateDirectory(directory);
        File.Copy(Path.GetFullPath("tools/PluginFixture/bin/Release/net8.0-windows/AlphaIsland.PluginFixture.dll"), Path.Combine(directory, "AlphaIsland.PluginFixture.dll"));
        string data = Path.Combine(directory, "data"); Directory.CreateDirectory(data);
        var session = new PluginProcessSession(window.Dispatcher);
        try
        {
            AwaitPlugin(session.StartAsync(new(directory, "AlphaIsland.PluginFixture.dll", "AlphaIsland.PluginFixture.LazyUiProbePlugin", "fixture.lazy-ui", data, new()), CancellationToken.None));
            string created = Path.Combine(data, "created.txt");
            Check(File.Exists(Path.Combine(data, "started.txt")), "插件后台服务正常启动，不因界面懒加载而停用");
            Case(() => Check(!File.Exists(created), "插件启动时不创建从未打开的 WPF 页面"));
            AwaitPlugin(session.Request<RemoteStatus>("status", new { }));
            AwaitPlugin(session.Request<RemoteFrame>("frame", new RemoteViewRequest("probe", 414, 294, false)));
            Case(() => Check(!File.Exists(created), "状态轮询和停用消息不会触发隐藏页面创建"));
            Check(AwaitPlugin(session.Request<bool>("prefill", new RemoteCommand("probe", "prefill", Text: "draft"))), "首次预填会按需创建页面并保留草稿");
            var frame = AwaitPlugin(session.Request<RemoteFrame>("frame", new RemoteViewRequest("probe", 414, 294)));
            Check(frame.Elements.Any(e => e.Name == "AiInputBox" && e.Text == "draft"), "首次打开显示已有草稿");
            Check(AwaitPlugin(session.Request<bool>("action", "edit")), "工具栏操作可以唤起需要的插件页面");
            frame = AwaitPlugin(session.Request<RemoteFrame>("frame", new RemoteViewRequest("probe", 414, 294)));
            Check(frame.Elements.Any(e => e.Name == "AiInputBox" && e.Text == "action") && File.ReadAllText(created) == "created;", "反复显示和操作复用同一页面，不重复分配界面");
        }
        finally { session.Dispose(); AwaitPlugin(session.Completion.ContinueWith(_ => true)); }
        Check(File.Exists(Path.Combine(data, "stopped.txt")) && File.Exists(Path.Combine(data, "disposed.txt")), "懒加载插件停用后仍执行完整服务与界面清理");
        string hostRoot = Path.Combine(testRoot, "hidden-worker"); string pluginDirectory = Path.Combine(hostRoot, "fixture.lazy-ui"); Directory.CreateDirectory(pluginDirectory);
        File.Copy(Path.Combine(directory, "AlphaIsland.PluginFixture.dll"), Path.Combine(pluginDirectory, "AlphaIsland.PluginFixture.dll"));
        File.WriteAllText(Path.Combine(pluginDirectory, "plugin.json"), JsonSerializer.Serialize(new
        { id = "fixture.lazy-ui", name = "Lazy", version = "1.0", apiVersion = 2, assembly = "AlphaIsland.PluginFixture.dll", entryType = "AlphaIsland.PluginFixture.LazyUiProbePlugin" }));
        var hostOptions = new IslandOptions(); hostOptions.Extras[PluginHost.EnabledKey("fixture.lazy-ui")] = JsonSerializer.SerializeToElement(true);
        var transparentCardHost = new Grid { Opacity = 0, Width = 414, Height = 294 }; root.Children.Add(transparentCardHost);
        using (var host = new PluginHost(window.Dispatcher, hostOptions, [hostRoot], page =>
        { page.View.Visibility = Visibility.Visible; transparentCardHost.Children.Add(page.View); }, page => transparentCardHost.Children.Remove(page.View), _ => { }) { DataRoot = Path.Combine(testRoot, "hidden-data") })
        {
            try
            {
                host.Discover(); var entry = host.Entries.Single(); PumpUntilState(() => entry.Running || entry.Error.Length > 0);
                Check(entry.Running, "透明宿主探针启动正式独立插件代理");
                string hostData = entry.Context!.DataDirectory; string creation = Path.Combine(hostData, "created.txt");
                root.UpdateLayout(); PumpAnimations(650);
                Case(() => Check(!File.Exists(creation), "卡片透明收起后不再捕获远程页面，IsVisible 为真也不会后台绘图"));
                var opened = System.Diagnostics.Stopwatch.StartNew(); transparentCardHost.Opacity = 1; root.UpdateLayout();
                PumpUntilState(() => Descendants<System.Windows.Controls.TextBox>(transparentCardHost).Any(t => t.Name == "AiInputBox"));
                Check(opened.ElapsedMilliseconds < 650 && File.Exists(creation), "重新展开立即请求第一帧，不等待低频轮询");
                transparentCardHost.Opacity = 0; PumpAnimations(650);
                Case(() => Check(File.Exists(Path.Combine(hostData, "deactivated.txt")), "透明收起及时触发插件页面停用，停止隐藏 UI 的活动"));
            }
            finally
            {
                root.Children.Remove(transparentCardHost); host.Dispose();
                AwaitPlugin(Task.WhenAll(host.Entries.Select(entry => entry.Teardown)).ContinueWith(_ => true));
            }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }
}
