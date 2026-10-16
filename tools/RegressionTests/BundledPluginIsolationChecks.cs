using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using alpha_island.Plugins;
using alpha_island.Plugins.Isolation;

internal static partial class Program
{
    private static void BundledPluginIsolationChecks(alpha_island.MainWindow window, string root)
    {
        var definitions = new[] { ("ApiChatPlugin", "net8.0-windows"), ("LocalAgentsPlugin", "net8.0-windows"),
            ("ClockPlugin", "net8.0-windows"), ("VivoOfficePlugin", "net8.0-windows"),
            ("TrafficMonitorPlugin", "net8.0-windows"), ("OppoPodsPlugin", "net8.0-windows10.0.19041.0") };
        foreach (var (folder, framework) in definitions)
        {
            string target = Path.Combine(root, "bundled", folder); Directory.CreateDirectory(target);
            string source = Path.GetFullPath($"examples/{folder}/bin/{TestBuildConfiguration}/{framework}");
            foreach (string file in Directory.GetFiles(source).Where(file => file.EndsWith(".dll") || file.EndsWith(".deps.json")))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            File.Copy(Path.GetFullPath($"examples/{folder}/plugin.json"), Path.Combine(target, "plugin.json"));
            var manifest = PluginManifest.Read(Path.Combine(target, "plugin.json"));
            using var session = new PluginProcessSession(window.Dispatcher);
            try
            {
            var registry = AwaitPlugin(session.StartAsync(new(target, manifest.Assembly, manifest.EntryType, manifest.Id,
                Path.Combine(root, "bundled-data", folder), new Dictionary<string, JsonElement>()), CancellationToken.None));
            Check((registry.Pages.Length > 0 || registry.Networks.Length > 0) && session.IsAlive, manifest.Id + " 的真实插件可在独立进程初始化");
            foreach (var page in registry.Pages)
            {
                var frame = AwaitPlugin(session.Request<RemoteFrame>("frame", new RemoteViewRequest(page.Key, 414, page.Height - 20)));
                Check(frame.Elements.Any(element => element.Kind == "text" && element.Text.Length > 0) && frame.Background.Length > 0,
                    manifest.Id + "/" + page.Key + " 页面返回有效文字与图形");
                if (manifest.Id == "ai.local-agents")
                    Check(PluginButtonInk(frame.Elements.Single(item => item.Kind == "button" && item.Text == "选择目录").Artwork) > 20,
                        "真实本机 Agent 页面右侧按钮图像保留可见文字");
                var remote = new RemotePluginView(page.Key, _ => Task.CompletedTask, _ => { }); remote.Apply(frame);
                SaveConversationPreview(new Border { Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 24, 30)), Child = remote },
                    "docs/screens/v105-" + manifest.Id + ".png", 414, (int)page.Height - 20);
            }
            if (registry.HasSettings)
            {
                var frame = AwaitPlugin(session.Request<RemoteFrame>("frame", new RemoteViewRequest("$settings", 650, 680)));
                Check(frame.Elements.Any(element => element.Kind == "button") && frame.Elements.Any(element => element.Kind == "text"),
                    manifest.Id + " 的真实设置页保留文字和按钮");
                Check(frame.Elements.Where(element => element.Kind == "password").All(element => element.Text.Length == 0), "密钥不会经视图快照回传明文");
            }
            if (manifest.Id is "ai.api-chat" or "ai.local-agents")
            {
                string key = registry.Pages[0].Key;
                Check(AwaitPlugin(session.Request<bool>("prefill", new RemoteCommand(key, "prefill", Text: "仅预填，不发送"))),
                    manifest.Id + " 支持启动器在未打开卡片时预填文本");
                var frame = AwaitPlugin(session.Request<RemoteFrame>("frame", new RemoteViewRequest(key, 414, 294)));
                Check(frame.Elements.Any(element => element.Kind == "input" && element.Text == "仅预填，不发送"), "输入内容进入实际插件控件");
                if (manifest.Id == "ai.local-agents")
                {
                    var create = frame.Elements.Single(item => item.Kind == "button" && item.Text == "新建");
                    Check(AwaitPlugin(session.Request<bool>("input", new RemoteCommand(key, "click", create.Id))), "真实本机 Agent 的右侧按钮可通过隔离输入协议点击");
                    frame = AwaitPlugin(session.Request<RemoteFrame>("frame", new RemoteViewRequest(key, 414, 294)));
                    Check(frame.Elements.Any(item => item.Kind == "text" && item.Text.StartsWith("新会话")), "点击新建更新实际插件状态，不只响应宿主外观");
                }
            }
            int process = session.ProcessId; session.Dispose(); AwaitPlugin(session.Completion.ContinueWith(_ => true));
            Check(!ProcessAlive(process), manifest.Id + " 正常结束后不残留进程");
            } finally { session.Dispose(); AwaitPlugin(session.Completion.ContinueWith(_ => true)); }
        }
    }
}
