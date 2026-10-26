using System.Text.Json;
using System.Windows.Controls;
using System.IO;
using System.Diagnostics;
using AlphaIsland.PluginSdk;
using alpha_island.Core;
using alpha_island.Services;
using alpha_island.Plugins;
using alpha_island.Plugins.Isolation;

internal static partial class Program
{
    private static void GameResourceBudgetChecks(alpha_island.MainWindow window, Grid root, string testRoot)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        var savedOptions = new Dictionary<string, JsonElement>(options.Extras);
        var originalHost = AgentField<IslandServiceHost>(window, "_services");
        var ticker = AgentField<IslandTicker>(window, "_ticker");
        var host = new IslandServiceHost();
        int starts = 0, stops = 0, weatherTicks = 0, clocks = 0, deferred = 0, captureCount = 0, requests = 0;
        int downloadStarts = 0, lazyStarts = 0; bool downloadsEnabled = true;
        host.Register(new DelegateIslandService(IslandServiceIds.Cpu, "采样探针", _ => true,
            () => starts++, () => stops++, autoStart: true));
        host.Register(new DelegateIslandService(IslandServiceIds.DownloadMonitor, "后台扫描探针", _ => downloadsEnabled,
            () => downloadStarts++, () => { }, autoStart: true));
        host.Register(new DelegateIslandService(IslandServiceIds.Spectrum, "按需采集探针", _ => true,
            () => lazyStarts++, () => { }, required: true));
        var originalVoice = AgentField<VoiceAssistantSession>(window, "_voiceAssistant");
        FakeVoiceCapture? capture = null;
        using var voice = new VoiceAssistantSession(window.Dispatcher, () => { captureCount++; return capture = new FakeVoiceCapture(); },
            (_, _) => { requests++; return Task.FromResult(new PluginTextInputResult(true, "ok")); }, _ => Task.CompletedTask,
            () => !AgentField<bool>(window, "_gameQuietActive"), () => Task.CompletedTask);
        Action<VoiceAssistantSnapshot> changed = snapshot => PrivateCall(window, "OnVoiceAssistantChanged", snapshot);
        voice.Changed += changed;
        var failures = new List<Exception>();
        void Case(bool passed, string label) { try { Check(passed, label); } catch (Exception error) { failures.Add(error); Console.WriteLine("FAIL: " + error.Message); } }
        try
        {
            SettingsRegistry.Write(options, "NotificationGameQuietEnabled", true);
            SettingsRegistry.Write(options, "AssistantGameWrapEnabled", false);
            SettingsRegistry.Write(options, "AiEnabled", true);
            SettingsRegistry.Write(options, "VoiceWakeEnabled", true);
            SettingsRegistry.Write(options, "VoiceWakeHoldEnabled", true);
            host.Sync(options); SetAgentField(window, "_services", host);
            SetAgentField(window, "_voiceAssistant", voice);
            PrivateCall(window, "ConfigureVoiceAssistant");
            AwaitPlugin(voice.WakeAsync().ContinueWith(_ => true));
            ticker.Register("weather", TimeSpan.FromHours(1), () => weatherTicks++);
            ticker.Register("productivity", TimeSpan.FromHours(1), () => clocks++);
            ticker.TriggerNow("weather");
            Check(starts == 1 && weatherTicks == 1, "游戏前必要的采样和天气回调探针已实际运行");
            PrivateCall(window, "ApplyGameActivity", new GameActivityState(1, true, "资源预算回归", "fixture", 1));
            ticker.TriggerNow("weather");
            Case(stops == 1 && !host.IsRunning(IslandServiceIds.Cpu), "游戏期间停止隐藏系统采样服务，释放其活动资源");
            Case(weatherTicks == 1, "游戏期间暂停天气后台任务，主动触发也不执行");
            ticker.TriggerNow("productivity");
            ticker.Register("new-hidden-probe", TimeSpan.FromHours(1), () => deferred++, runImmediately: true);
            Case(clocks == 1 && deferred == 0, "游戏计时仍可运行，新注册的非必要任务也受暂停约束");
            AwaitPlugin(PrivateCall(window, "ToggleVoiceAssistantAsync") is Task toggle ? toggle.ContinueWith(_ => true) : Task.FromResult(false));
            AwaitPlugin(voice.WakeAsync().ContinueWith(_ => true));
            Case(captureCount == 1 && capture?.Disposed == true && !voice.Visible && requests == 0,
                "游戏进入释放现有录音，快捷键入口与直接唤醒均不重新创建麦克风或发送请求");
            Case(!AgentField<bool>(window, "_voiceKeysRunning") && AgentField<IDisposable?>(window, "_voiceShortcut") == null
                && !AgentField<System.Windows.Threading.DispatcherTimer>(window, "_voiceHoldTimer").IsEnabled,
                "游戏期间没有语音系统快捷键、长按键盘监听或长按计时器");
            downloadsEnabled = false; host.Sync(options);
            Case(!host.EnsureStarted(IslandServiceIds.Spectrum) && lazyStarts == 0 && starts == 1,
                "游戏中应用设置或按需请求不会重新启动已暂停的组件");
            PrivateCall(window, "FinishGameQuietIntro"); PumpAnimations(750);
            Case(AgentField<IslandState>(window, "_state") == IslandState.Bar, "游戏期间保持收起状态");
            PrivateCall(window, "ApplyGameActivity", GameActivityState.Inactive);
            ticker.TriggerNow("weather");
            Case(starts == 2 && host.IsRunning(IslandServiceIds.Cpu) && weatherTicks == 2,
                "游戏退出恢复原有采样与天气任务，无需重新配置");
            Case(downloadStarts == 1 && lazyStarts == 1, "恢复遵守最新开关，仅恢复之前运行或游戏中确实请求过的服务");
            Case(!voice.Visible && captureCount == 1, "退出游戏恢复唤醒能力，不会自动打开麦克风");
            ticker.TriggerNow("new-hidden-probe");
            Case(deferred == 1, "游戏期间注册的任务在恢复后可正常执行");
            ticker.SetPaused("weather", true);
            PrivateCall(window, "ApplyGameActivity", new GameActivityState(2, true, "资源预算回归", "fixture", 1));
            window.ApplySessionNotification(true);
            PrivateCall(window, "ApplyGameActivity", GameActivityState.Inactive); ticker.TriggerNow("weather");
            Case(weatherTicks == 2, "游戏结束不会错误取消组件自身原有的暂停状态");
            Case(!AgentField<bool>(window, "_voiceHoldEnabled"), "游戏在锁屏期间结束也不会提前恢复语音监听");
            window.ApplySessionNotification(false);
            Case(AgentField<bool>(window, "_voiceHoldEnabled"), "游戏在锁屏期间结束，解锁后重新恢复原有语音唤醒配置");
            AwaitPlugin(PrivateCall(window, "ToggleVoiceAssistantAsync") is Task restored ? restored.ContinueWith(_ => true) : Task.FromResult(false));
            Case(captureCount == 2 && voice.Active, "退出游戏后用户可再次主动唤醒语音助手");
            AwaitPlugin(voice.CancelAsync().ContinueWith(_ => true));
        }
        finally
        {
            if (AgentField<bool>(window, "_sessionLocked")) window.ApplySessionNotification(false);
            PrivateCall(window, "ApplyGameActivity", GameActivityState.Inactive);
            ticker.Unregister("weather"); ticker.Unregister("productivity"); ticker.Unregister("new-hidden-probe");
            host.StopAll(); SetAgentField(window, "_services", originalHost);
            voice.Changed -= changed; SetAgentField(window, "_voiceAssistant", originalVoice);
            options.Extras = savedOptions;
        }
        if (failures.Count > 0) throw new AggregateException(failures);
        int shutdownStarts = 0;
        var stoppingHost = new IslandServiceHost();
        stoppingHost.Register(new DelegateIslandService("probe", "退出探针", _ => true,
            () => shutdownStarts++, () => { }, autoStart: true));
        stoppingHost.Sync(options); stoppingHost.SetSuspended(["probe"], true);
        stoppingHost.StopAll(); stoppingHost.SetSuspended(["probe"], false);
        Check(shutdownStarts == 1 && !stoppingHost.IsRunning("probe"), "退出清理后即使收到延迟恢复也不会复活已关闭组件");
        GameWorkerSuspensionChecks(window, testRoot);
    }

    private static void GameWorkerSuspensionChecks(alpha_island.MainWindow window, string testRoot)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        var saved = new Dictionary<string, JsonElement>(options.Extras);
        var original = AgentField<PluginHost?>(window, "_plugins");
        string directory = Path.Combine(testRoot, "game-workers"); Directory.CreateDirectory(directory);
        var definitions = new[] { ("fixture.idle", "LazyUiProbePlugin"), ("fixture.draft", "GameDraftProbePlugin"),
            ("fixture.busy", "TextInputProbePlugin"), ("fixture.legacy", "BatteryProbePlugin") };
        foreach (var (id, type) in definitions)
        {
            string target = Path.Combine(directory, id); Directory.CreateDirectory(target);
            File.Copy(Path.GetFullPath("tools/PluginFixture/bin/Release/net8.0-windows/AlphaIsland.PluginFixture.dll"), Path.Combine(target, "AlphaIsland.PluginFixture.dll"));
            File.WriteAllText(Path.Combine(target, "plugin.json"), JsonSerializer.Serialize(new
            { id, name = id, version = "1.0", apiVersion = type == "BatteryProbePlugin" ? 4 : 6, assembly = "AlphaIsland.PluginFixture.dll", entryType = "AlphaIsland.PluginFixture." + type }));
            options.Extras[PluginHost.EnabledKey(id)] = JsonSerializer.SerializeToElement(true);
        }
        PrivateCall(window, "BuildPluginHost");
        using var host = AgentField<PluginHost>(window, "_plugins");
        SetAgentField(host, "_roots", new[] { directory }); host.DataRoot = Path.Combine(directory, "data");
        var measurements = new List<object>();
        void Measure(string phase)
        {
            var ids = host.Entries.Select(entry => (entry.Instance as IsolatedPlugin)?.ProcessId ?? 0).Where(id => id > 0).ToArray();
            long bytes = 0;
            foreach (int id in ids) { using var process = Process.GetProcessById(id); bytes += process.PrivateMemorySize64; }
            var value = new { Phase = phase, WorkerCount = ids.Length, PrivateMB = Math.Round(bytes / 1048576d, 1) };
            measurements.Add(value); Console.WriteLine("Game workers: " + JsonSerializer.Serialize(value));
        }
        try
        {
            SettingsRegistry.Write(options, "NotificationGameQuietEnabled", true);
            SettingsRegistry.Write(options, "AssistantGameWrapEnabled", false);
            host.Discover(); SetAgentField(window, "_plugins", host);
            PumpUntilState(() => host.Entries.All(entry => entry.Running || entry.Error.Length > 0));
            Check(host.Entries.Count == 4 && host.Entries.All(entry => entry.Running), "真实独立进程建立空闲、草稿、请求中和旧插件四种场景；"
                + string.Join("；", host.Entries.Select(entry => $"{entry.Manifest?.Id ?? "manifest"}: {(entry.Running ? "ready" : entry.Error)}")));
            var idle = host.Entries.Single(entry => entry.Manifest!.Id == "fixture.idle");
            var draft = host.Entries.Single(entry => entry.Manifest!.Id == "fixture.draft");
            var busy = host.Entries.Single(entry => entry.Manifest!.Id == "fixture.busy");
            var legacy = host.Entries.Single(entry => entry.Manifest!.Id == "fixture.legacy");
            int idlePid = ((IsolatedPlugin)idle.Instance!).ProcessId;
            string pinnedPage = "plugin:fixture.idle/probe";
            string pinKey = (string)PrivateCall(window, "EnsureCardPinLayer", pinnedPage)!;
            PrivateCall(window, "SetLayerPinned", pinKey, true, false);
            var layers = AgentField<IslandLayerHost>(window, "_layers");
            Check(layers.IsPinned(pinKey), "通过正式主窗口路径置顶将要休眠的插件卡片");
            var draftProxy = (IsolatedPlugin)draft.Instance!; var busyProxy = (IsolatedPlugin)busy.Instance!;
            Check(AwaitPlugin(draftProxy.PrefillAsync("probe", "保留的草稿")), "草稿通过正式进程协议写入实际插件页面");
            string request = Guid.NewGuid().ToString("N");
            var pending = busyProxy.SubmitTextAsync("chat", request, "wait"); PumpAnimations(200);
            Measure("normal");
            PrivateCall(window, "ApplyGameActivity", new GameActivityState(1, true, "进程休眠回归", "fixture", 1));
            PumpUntilState(() => idle.GameSuspended && !AgentField<bool>(host, "_checkingGameSuspension"));
            AwaitPlugin(idle.Teardown.ContinueWith(_ => true));
            Check(!idle.Running && idle.Instance == null && draft.Running && busy.Running && legacy.Running && !pending.IsCompleted,
                "游戏时空闲插件正常退出，草稿、执行中任务和不支持休眠的插件保留");
            Check(layers.IsPinned(pinKey), "插件退出与页面注销不会清除原有置顶偏好");
            host.Sync(options);
            Check(!idle.Running && idle.Instance == null, "游戏中设置同步不会重启已休眠的插件");
            var frame = AwaitPlugin(draftProxy.PrefillAsync("probe", "保留的草稿"));
            Check(frame, "游戏期间保留草稿的进程仍可接收已有状态操作");
            Measure("game-with-draft-and-task");
            Check(AwaitPlugin(draftProxy.PrefillAsync("probe", "")), "清空测试草稿使插件重新符合空闲条件");
            AwaitPlugin(busyProxy.CancelTextAsync("chat", request));
            Check(AwaitPlugin(pending).Message == "cancelled", "原有任务由用户明确取消，休眠没有中断请求");
            AwaitPlugin(host.ReleaseIdleGameWorkersAsync().ContinueWith(_ => true));
            AwaitPlugin(host.ShutdownCompletion.ContinueWith(_ => true));
            Check(draft.GameSuspended && busy.GameSuspended && legacy.Running, "任务结束后自动具备释放条件，旧插件仍保持兼容");
            Measure("game-idle");
            PrivateCall(window, "ApplyGameActivity", GameActivityState.Inactive);
            PumpUntilState(() => host.Entries.All(entry => entry.Running || entry.Error.Length > 0));
            Check(host.Entries.All(entry => entry.Running) && ((IsolatedPlugin)idle.Instance!).ProcessId != idlePid,
                "退出游戏按原开关恢复独立进程，已退出的插件重新建立连接");
            Check(layers.IsPinned(pinKey), "异步重建页面后继续保留插件卡片原有置顶");
            Measure("restored");
            host.SetGameSuspended(true); host.SetGameSuspended(false);
            PumpUntilState(() => !AgentField<bool>(host, "_checkingGameSuspension") && host.Entries.All(entry => entry.Running));
            Check(host.Entries.All(entry => entry.Error.Length == 0 && entry.Running), "快速进入并退出游戏不会因过期休眠响应关闭已恢复插件");
            Directory.CreateDirectory("bin/GameBudget171");
            File.WriteAllText("bin/GameBudget171/worker-memory-measurements.json", JsonSerializer.Serialize(measurements));
        }
        finally
        {
            PrivateCall(window, "ApplyGameActivity", GameActivityState.Inactive);
            SetAgentField(window, "_plugins", original);
            host.Dispose(); AwaitPlugin(host.ShutdownCompletion.ContinueWith(_ => true)); options.Extras = saved;
        }
    }
}
