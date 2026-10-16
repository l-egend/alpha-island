using System.Diagnostics;
using System.IO;
using System.Text.Json;
using alpha_island.Core;
using alpha_island.Plugins.Isolation;

internal static partial class Program
{
    private static T AwaitPlugin<T>(Task<T> task, int timeout = 20000)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.ElapsedMilliseconds < timeout) PumpAnimations(15);
        if (!task.IsCompleted) throw new TimeoutException("Plugin isolation fixture timed out");
        return task.GetAwaiter().GetResult();
    }
    private static bool ProcessAlive(int id)
    {
        try { using var process = Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
    private static void PluginIsolationChecks(alpha_island.MainWindow window, string root)
    {
        string directory = Path.Combine(root, "isolated-plugin"); Directory.CreateDirectory(directory);
        string binary = "AlphaIsland.PluginFixture.dll";
        File.Copy(Path.GetFullPath("tools/PluginFixture/bin/Release/net8.0-windows/" + binary), Path.Combine(directory, binary));
        PluginStart Start(string id) => new(directory, binary, "AlphaIsland.PluginFixture.ProbePlugin", id,
            Path.Combine(root, "worker-data"), new Dictionary<string, JsonElement>());
        using (var session = new PluginProcessSession(window.Dispatcher))
        {
            var registry = AwaitPlugin(session.StartAsync(Start("fixture.isolation.normal"), CancellationToken.None));
            Check(session.ProcessId != Environment.ProcessId && session.IsAlive && registry.Pages.Length == 1, "插件入口在真正独立的进程中初始化");
            var frame = AwaitPlugin(session.Request<RemoteFrame>("frame", new RemoteViewRequest("probe", 414, 194)));
            Check(frame.Elements.Any(element => element.Name == "IsolationInput") && frame.Background.Length > 0, "插件视图通过受控控件值和像素协议传回");
            var input = frame.Elements.Single(element => element.Name == "IsolationInput");
            var button = frame.Elements.Single(element => element.Name == "IsolationButton");
            AwaitPlugin(session.Request<bool>("input", new RemoteCommand("probe", "text", input.Id, "你好，独立插件")));
            AwaitPlugin(session.Request<bool>("input", new RemoteCommand("probe", "click", button.Id)));
            frame = AwaitPlugin(session.Request<RemoteFrame>("frame", new RemoteViewRequest("probe", 414, 194)));
            Check(frame.Elements.Single(element => element.Name == "IsolationLabel").Text == "你好，独立插件", "文本输入和按钮操作由插件进程执行并更新视图");
            int child = 0;
            session.Event += item => { if (item.Name == "configuration" && item.Key == "fixtureChildPid" && item.Value is JsonElement value) child = value.GetInt32(); };
            AwaitPlugin(session.Request<bool>("action", "child"));
            var wait = Stopwatch.StartNew(); while (child == 0 && wait.ElapsedMilliseconds < 2000) PumpAnimations(20);
            Check(child > 0 && ProcessAlive(child), "测试插件创建的后台子进程可跟踪");
            int worker = session.ProcessId; session.Dispose();
            wait.Restart(); while ((ProcessAlive(worker) || ProcessAlive(child)) && wait.ElapsedMilliseconds < 3000) PumpAnimations(20);
            Check(!ProcessAlive(worker) && !ProcessAlive(child), "关闭插件 Job 回收插件本身与其子进程");
            string lifecycle = File.ReadAllText(Path.Combine(root, "worker-data", "fixture.isolation.normal.txt"));
            Check(lifecycle.Contains("start;stop;dispose:True;"), "正常停用先取消并执行插件 Stop、Dispose，再回收剩余子进程");
        }
        using (var session = new PluginProcessSession(window.Dispatcher))
        {
            AwaitPlugin(session.StartAsync(Start("fixture.isolation.crash"), CancellationToken.None));
            bool isolated = false;
            try { AwaitPlugin(session.Request<bool>("action", "failfast"), 5000); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { isolated = true; }
            Check(isolated && !session.IsAlive, "Environment.FailFast 只终止插件进程，主程序继续执行");
        }
        using (var session = new PluginProcessSession(window.Dispatcher))
        using (var ticker = new IslandTicker(window.Dispatcher, TimeSpan.FromMilliseconds(20)))
        {
            AwaitPlugin(session.StartAsync(Start("fixture.isolation.hang"), CancellationToken.None));
            int pulses = 0; using var pulse = ticker.Subscribe("ui", TimeSpan.FromMilliseconds(20), () => pulses++);
            bool timeout = false;
            try { AwaitPlugin(session.Request<bool>("action", "hang", TimeSpan.FromMilliseconds(300)), 3000); }
            catch (TimeoutException) { timeout = true; }
            Check(timeout && pulses >= 3, "插件线程挂死时主 UI 仍运行，IPC 在截止时间退出");
            int worker = session.ProcessId; session.Dispose();
            var watch = Stopwatch.StartNew(); while (ProcessAlive(worker) && watch.ElapsedMilliseconds < 2000) PumpAnimations(20);
            Check(!ProcessAlive(worker), "挂死插件能被回收且不会留下后台进程");
        }
    }
}
