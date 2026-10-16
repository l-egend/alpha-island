using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Services;
using alpha_island.ViewModels;

internal static partial class Program
{
    private static object? ReadWindowMember(object window, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        if (window.GetType().GetField(name, flags) is { } field) return field.GetValue(window);
        if (window.GetType().GetProperty(name, flags) is { } property) return property.GetValue(window);
        throw new MissingMemberException(window.GetType().FullName, name);
    }
    private static void WriteWindowMember(object window, string name, object? value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        if (window.GetType().GetField(name, flags) is { } field) { field.SetValue(window, value); return; }
        if (window.GetType().GetProperty(name, flags) is { CanWrite: true } property) { property.SetValue(window, value); return; }
        throw new MissingMemberException(window.GetType().FullName, name);
    }
    private static void PumpUntilState(Func<bool> ready, int timeout = 20000)
    {
        var timer = Stopwatch.StartNew(); while (!ready() && timer.ElapsedMilliseconds < timeout) PumpAnimations(20);
        if (!ready()) throw new TimeoutException("异步 UI 状态未在截止时间内完成");
    }
    private static void MaintenanceChecks(alpha_island.MainWindow window, string root)
    {
        PluginSurfaceRasterChecks(); ClockLifetimeChecks(); MaintenancePresentationChecks(window);
        var events = new List<alpha_island.Plugins.Isolation.RemoteEvent>();
        var worker = new alpha_island.Plugins.Isolation.PluginWorkerContext(new("", "", "", "fixture", root, new()),
            window.Dispatcher, events.Add, CancellationToken.None);
        string longPreview = new('x', 20000);
        worker.ReportActivity("thinking", longPreview); worker.ReportActivity("thinking", longPreview);
        Check(events.Count == 1 && events[0].Text.Length == 4096, "长活动摘要有界且相同内容合并，不触发插件容量故障");
        worker.Pages.Add(new("chat", "聊天", () => new System.Windows.Controls.Grid()));
        worker.NotifyPage("chat", longPreview, longPreview);
        Check(events[^1].Text.Length == 4096 && events[^1].Detail.Length == 8192, "长通知只裁剪展示摘要，保持 IPC 容量限制");
        worker.ReportActivity("thinking", new string('x', 4095) + "😀more");
        Check(!char.IsHighSurrogate(events[^1].Text[^1]), "截断展示文字不会遗留半个代理字符");
        var ledger = new ProductivityLedger(TimeZoneInfo.Utc);
        var start = new DateTimeOffset(2026, 10, 14, 23, 59, 30, TimeSpan.Zero);
        ledger.SetState(ProductivityActivity.Music | ProductivityActivity.Agent, true, false, start, 0);
        ledger.Advance(start.AddSeconds(60), 60_000);
        var days = ledger.Snapshot();
        Check(days.Length == 2 && days.All(day => day.MusicSeconds == 30 && day.AgentSeconds == 30), "跨午夜按日期拆分活动，音乐与助手分别累计");
        ledger.SetState(ProductivityActivity.Focus, true, true, start.AddSeconds(60), 60_000);
        ledger.Advance(start.AddHours(1), 3_600_000);
        Check(ledger.Snapshot().Sum(day => day.FocusSeconds) == 0, "锁屏或休眠不累计专注时长");
        ledger.SetState(ProductivityActivity.Focus, true, false, start.AddHours(1), 3_600_000);
        ledger.Advance(start.AddHours(1).AddSeconds(30), 3_630_000);
        Check(ledger.Snapshot().Sum(day => day.FocusSeconds) == 30, "恢复后从新的时间点开始记录");
        ledger.Advance(start.AddHours(3), 3_660_000);
        Check(ledger.Snapshot().Sum(day => day.FocusSeconds) == 30, "系统时间跳变不会制造统计时长");
        ledger.Advance(start.AddHours(4), 7_260_000);
        Check(ledger.Snapshot().Sum(day => day.FocusSeconds) == 30, "超过 90 秒的采样缺口不冒充持续使用");
        ledger.Advance(start.AddDays(100), 7_260_001);
        Check(ledger.Snapshot().Length == 0, "本机活动记录保留 90 天");
        ledger.Restore([new("2026-10-14", double.NaN), new("invalid-date"), new("2026-10-15", 42)]);
        Check(ledger.Snapshot().Length == 1 && ledger.Snapshot()[0].MusicSeconds == 42, "损坏日期与非有限统计值不会进入看板");
        ledger.Clear(start, 1); Check(ledger.Snapshot().Length == 0, "清除统计不会重播旧活动");

        string archive = Path.Combine(root, "notifications.dat");
        using (var ticker = new IslandTicker(window.Dispatcher))
        {
            var notifications = new NotificationsViewModel(); notifications.AttachArchive(archive, true, ticker);
            notifications.Inbox.Add(new("private", "qq", "QQ", "测试联系人", "private-message-content", DateTimeOffset.Now), true);
            notifications.Dispose();
            Check(File.Exists(archive) && !Encoding.UTF8.GetString(File.ReadAllBytes(archive)).Contains("private-message-content"), "通知正文以当前 Windows 用户加密落盘");
            var restored = new NotificationsViewModel(); restored.AttachArchive(archive, true, ticker);
            Check(restored.Inbox.History.Single().Body == "private-message-content" && restored.Inbox.PendingCount == 0, "重启恢复历史且不补弹旧消息");
            restored.SetRemember(false); restored.Dispose();
            var cleared = new NotificationsViewModel(); cleared.AttachArchive(archive, true, ticker);
            Check(cleared.Inbox.History.Count == 0, "关闭保留历史会清空持久记录"); cleared.Dispose();
        }
        var options = new IslandOptions(); SettingsRegistry.Write(options, "NotificationPriorityApps", "QQ");
        var policy = new DesktopNotificationPolicy(options);
        var normal = new DesktopNotification("n", "wechat", "微信", "普通联系人", "普通内容", DateTimeOffset.Now);
        Check(policy.Level(normal) == NotificationImportance.Normal && policy.Level(normal with { AppName = "QQ" }) == NotificationImportance.Important,
            "通知分级承接原有优先应用设置");
        Check(policy.Level(normal with { Kind = DesktopEventKind.CriticalBattery }) == NotificationImportance.Critical, "电池紧急事件自动归为紧急级别");
        var filter = new NotificationsViewModel(); filter.Inbox.Add(normal, false); filter.Inbox.Add(normal with { Key = "important", AppName = "QQ", Title = "elliot" }, false);
        filter.MinimumImportance = NotificationImportance.Important; filter.Search = "elliot";
        Check(filter.Filter(policy).Single().Key == "important", "通知重要程度和联系人搜索可组合筛选");
        SettingsRegistry.Write(options, "NotificationQuietMode", true);
        Check(!policy.CanPopup(normal, false) && policy.CanPopup(normal with { AppName = "QQ" }, false), "手动免打扰遵循重要通知例外");
        Check(!new DesktopNotificationPolicy(options, () => true).CanPopup(normal with { Importance = NotificationImportance.Critical }, false), "游戏免打扰保留全部消息而不弹出");

        var entries = new[] { new LauncherEntry("codex", "Codex", "助手", LauncherKind.Agent, "codex"),
            new LauncherEntry("x", "Codex 组件", "岛内组件", LauncherKind.Component, "codex-page"),
            new LauncherEntry("focus", "番茄钟", "专注", LauncherKind.Component, "focus") };
        Check(LauncherSearch.Search(entries.Concat(entries), "codex").Select(item => item.Id).SequenceEqual(new[] { "codex", "x" }), "启动器去重且精确名称优先于部分匹配");
        Check(LauncherSearch.Search(entries, "番茄").Single().Id == "focus" && LauncherSearch.Search(entries, "none").Length == 0, "启动器支持中文搜索与空结果");
        int executed = 0; var launcher = new LauncherViewModel(_ => { executed++; return Task.CompletedTask; }); launcher.SetEntries(entries); launcher.Query = "测试提问";
        Check(executed == 0 && launcher.Results.Single().Kind == LauncherKind.AiPrompt, "搜索只产生提问入口，不自动调用 Agent");
        AwaitPlugin(launcher.RunCommand.ExecuteAsync(null).ContinueWith(_ => true)); Check(executed == 1, "显式执行选择项才调用对应操作");
        var combo = new HotkeyCombo(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key.F23);
        Check(HotkeyService.TryRegisterSystem(combo, () => { }, out var first, out _), "启动器使用原生全局快捷键注册");
        try { Check(!HotkeyService.TryRegisterSystem(combo, () => { }, out var conflict, out string message) && conflict == null && message.Contains("占用"), "快捷键冲突可检测并向用户说明"); }
        finally { first?.Dispose(); }
        Check(HotkeyService.TryRegisterSystem(combo, () => { }, out var again, out _), "释放快捷键后可以重新注册"); again?.Dispose();

        var media = new MediaViewModel(); var song = new NowPlayingInfo { Title = "song", SourceApp = "player", State = PlaybackState.Playing };
        Check(!media.Preview(song).HadMedia, "媒体首个快照不会当成换曲提示"); media.Commit(song);
        Check(!media.Preview(song with { Position = TimeSpan.FromSeconds(1) }).TrackChanged, "进度更新不误判成换曲");
        Check(media.Preview(song with { State = PlaybackState.Paused }).PlaybackChanged, "播放切换独立于曲目识别"); media.Reset(); Check(media.Current == null && !media.Hidden, "媒体清空只重置唯一状态来源");
        var focus = new FocusViewModel(); focus.ToggleCommand.Execute(null); Check(focus.Running, "番茄钟命令控制独立业务状态"); focus.ResetCommand.Execute(null); Check(!focus.Running, "重置番茄钟命令可停止计时");

        var pages = (IslandPageHost)ReadWindowMember(window, "_pages")!;
        Check(pages.IndexOf("productivity") >= 0, "生产力看板已注册到实际组件列表");
        string previous = pages.ActiveId;
        PrivateCall(window, "SelectComponent", "productivity"); PumpAnimations(100); window.IslandBody.UpdateLayout();
        SaveThemePreview(window.IslandBody, "docs/screens/v105-productivity.png");
        PrivateCall(window, "SelectComponent", previous);
    }
}
