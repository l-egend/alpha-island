using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using CheckBox = System.Windows.Controls.CheckBox;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;

internal static partial class Program
{
    private sealed class GameLeaseFixture : IGameProcessLease
    {
        internal bool Alive = true;
        internal bool Disposed;
        public bool IsAlive => Alive;
        public void Dispose() => Disposed = true;
    }
    private sealed class GameProbeFixture : IGameActivityProbe
    {
        internal GameForeground? Foreground;
        internal bool Throw;
        internal readonly Dictionary<uint, GameLeaseFixture> Leases = new();
        public GameForeground? ReadForeground() => Throw ? throw new InvalidOperationException("probe unavailable") : Foreground;
        public IGameProcessLease? Track(GameIdentity identity) => Leases[identity.ProcessId] = new();
    }
    private static async Task GameActivityChecks()
    {
        var game = new GameForeground(7, @"E:\Games\Example\example.exe", "隔离游戏");
        var policy = new GameIdentityPolicy([game.Path, "relative.exe"]);
        Check(policy.Identify(game)?.Source == "Windows 游戏记录", "具体游戏路径采用 Windows 已识别记录");
        Check(policy.Identify(game with { Path = @"C:\Other\example.exe" }) == null, "相同文件名的其他路径不冒用系统游戏记录");
        Check(policy.Identify(game with { Path = "relative.exe" }) == null, "不可信相对路径不进入游戏名单");
        Check(policy.Identify(game with { ProcessId = 0 }) == null, "无效前台进程不触发免打扰");
        Check(policy.Identify(game with { Path = @"E:\Games\cs2.exe" })?.Source == "已知游戏进程", "已知游戏无需全屏也能识别");
        foreach (string excluded in new[] { "chrome.exe", "msedge.exe", "vlc.exe", "steam.exe", "LeagueClient.exe", "CrosshairX.exe", "UnrealEditor.exe", "blender.exe" })
            Check(policy.Identify(new(8, @"C:\Apps\" + excluded, "全屏应用", true)) == null, "全屏应用或启动器不会当成游戏：" + excluded);
        Check(policy.Identify(game with { Path = @"E:\Unknown\render-game.exe", ExclusiveDirect3D = true })?.Source == "独占 Direct3D", "Direct3D 独占全屏作为未记录游戏的后备信号");
        var custom = new GameIdentityPolicy([], @"custom.exe;C:\Games\special.exe;新游戏");
        Check(custom.Identify(game with { Path = @"E:\Custom\CUSTOM.exe" })?.Source == "用户指定", "自定义程序名忽略大小写并精确匹配");
        Check(custom.Identify(game with { Path = @"C:\Games\special.exe" }) != null && custom.Identify(game with { Path = @"D:\Games\special.exe" }) == null, "完整路径补充项保持路径精度");
        Check(custom.Identify(game with { Path = @"E:\Custom\新游戏.exe" }) != null && custom.Identify(game with { Path = @"E:\Custom\custom-helper.exe" }) == null, "补充项兼容中文和省略扩展名，不匹配辅助进程");
        var probe = new GameProbeFixture { Foreground = game };
        int transitions = 0;
        using var monitor = new GameActivityMonitor(probe: probe, catalog: () => [game.Path, @"E:\Games\second.exe"]);
        monitor.Changed += _ => transitions++;
        monitor.Refresh();
        Check(monitor.Current.Active && monitor.Current.Count == 1 && transitions == 1, "进入已确认游戏时启动一个会话");
        var first = probe.Leases[game.ProcessId];
        probe.Foreground = new(9, @"C:\Apps\chrome.exe", "浏览器"); monitor.Refresh();
        Check(monitor.Current.Active && transitions == 1 && !first.Disposed, "Alt+Tab 到浏览器仍保持游戏免打扰且不重发提示");
        probe.Foreground = null; monitor.Refresh();
        Check(monitor.Current.Active && transitions == 1, "暂时没有前台窗口不等于游戏退出");
        probe.Throw = true; monitor.Refresh(); probe.Throw = false;
        Check(monitor.Current.Active && transitions == 1, "读取失败保留已确认的游戏会话");
        probe.Foreground = new(10, @"E:\Games\second.exe", "第二个游戏"); monitor.Refresh();
        Check(monitor.Current.Count == 2 && monitor.Current.Active, "同时运行两个已进入前台的游戏");
        first.Alive = false; probe.Foreground = null; monitor.Refresh();
        Check(monitor.Current.Active && monitor.Current.Count == 1 && first.Disposed, "退出一个游戏不提前取消免打扰且释放其句柄");
        probe.Leases[10].Alive = false; monitor.Refresh();
        Check(!monitor.Current.Active && probe.Leases[10].Disposed, "全部游戏退出后结束会话并释放句柄");
        probe.Foreground = game; monitor.Refresh();
        var reused = probe.Leases[game.ProcessId]; reused.Alive = false;
        probe.Foreground = new(game.ProcessId, @"E:\Games\second.exe", "复用进程号"); monitor.Refresh();
        Check(monitor.Current.Active && reused.Disposed && monitor.Current.Name == "复用进程号", "PID 复用重新确认身份，不沿用旧进程生命周期");
        monitor.Dispose();
        Check(probe.Leases[game.ProcessId].Disposed && !monitor.Current.Active, "关闭监测释放全部跟踪资源");
        monitor.Refresh(); Check(!monitor.Current.Active, "释放后的监测不会复活免打扰状态");
        var native = new GameActivityProbe();
        var snapshot = native.ReadForeground();
        Check(snapshot == null || GameIdentityPolicy.IsAbsoluteExe(snapshot.Path), "正式前台读取路径使用受限只读句柄");
        string[] catalog = GameActivityProbe.ReadWindowsGames();
        Check(catalog.All(GameIdentityPolicy.IsAbsoluteExe), "本机 Windows 游戏目录只返回完整 exe 路径");
        string childPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        using var child = Process.Start(new ProcessStartInfo(childPath, "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"")
            { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            using var lease = native.Track(new((uint)child.Id, childPath, "生命周期测试", "用户指定"));
            Check(lease?.IsAlive == true, "正式进程句柄能够确认后台进程仍在运行");
            Check(native.Track(new((uint)child.Id, @"C:\wrong\wrong.exe", "错误身份", "用户指定")) == null, "取得句柄时再次核验程序路径");
            child.Kill(); await child.WaitForExitAsync();
            var ended = Stopwatch.StartNew(); while (lease?.IsAlive == true && ended.ElapsedMilliseconds < 1000) await Task.Delay(20);
            Check(lease?.IsAlive == false, "进程退出时内核句柄发出结束信号");
        }
        finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
    }
    [DllImport("user32.dll", EntryPoint = "NotifyWinEvent")]
    private static extern void GameNotifyForeground(uint evt, IntPtr window, int objectId, int childId);
    private static void GameQuietUiChecks(alpha_island.MainWindow window, Grid root)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        var inbox = AgentField<DesktopNotificationInbox>(window, "_desktopInbox");
        var notice = AgentField<DesktopNotificationPill>(window, "_gameQuietNotice");
        const string owner = "game-quiet-fixture";
        SettingsRegistry.Write(options, "NotificationGameQuietEnabled", true);
        SettingsRegistry.Write(options, "NotificationQuietMode", false);
        SettingsRegistry.Write(options, "MediaWindowHandoffEnabled", false);
        options.ClipboardNotifyEnabled = options.VolumeNotifyEnabled = options.BatteryNotifyEnabled = options.BluetoothNotifyEnabled = true;
        void State(bool active, long revision = 1, bool finishIntro = true)
        {
            PrivateCall(window, "ApplyGameActivity", new GameActivityState(revision, active, active ? "隔离测试游戏" : "", "用户指定", active ? 1 : 0));
            if (active && finishIntro) PrivateCall(window, "FinishGameQuietIntro");
        }
        void Reset()
        {
            SetAgentField(window, "_approvalCurrent", null); AgentStatus.Reset(owner: "approval"); AgentStatus.Reset(owner: owner);
            State(false); layers.HideAll(); inbox.Clear(); SetAgentField(window, "_clipboardLocked", false);
            PrivateCall(window, "SetState", IslandState.Bar, true);
        }
        try
        {
            Reset(); State(true, finishIntro: false); PumpAnimations(120); root.UpdateLayout();
            SaveThemePreview(window.IslandBody, "bin/game-quiet-preview.png");
            Check(AgentField<bool>(window, "_gameQuietActive") && notice.Title.Text == "检测到游戏开启" && notice.Detail.Text.Contains("免打扰模式已开启"), "游戏进入时显示一次清晰的免打扰提示");
            Check(layers.IsActive("gamequiet") && AgentField<IslandState>(window, "_state") == IslandState.Pill, "进入游戏先显示一次提示，期间不会展开其他内容");
            Check(!SettingsRegistry.GetBool(options, "NotificationQuietMode", false), "自动免打扰不会写入手动静默设置");
            var quiet = AgentField<CheckBox>(window, "_notificationQuiet");
            Check(quiet.IsChecked == true && !quiet.IsEnabled, "通知中心显示自动静默并解释退出后恢复");
            Check(PrivateCall(window, "SetLayerPinned", "gamequiet", true) is false && !layers.IsPinned("gamequiet"),
                "游戏开启提示不会被误置顶成永久弹窗");
            long introUntil = AgentField<long>(window, "_gameQuietIntroUntil");
            State(true, 2, finishIntro: false);
            Check(layers.IsShowing("gamequiet") && AgentField<long>(window, "_gameQuietIntroUntil") == introUntil
                && inbox.History.Count(x => x.Title == "检测到游戏开启") == 1, "同一游戏后台续期不重复弹出或延长进入提示");
            PrivateCall(window, "FinishGameQuietIntro");
            PrivateCall(window, "OnVolumeChanged", null, new VolumeInfo(48, false));
            Check(!layers.IsActive("volume") && inbox.History.Any(x => x.Kind == DesktopEventKind.Volume), "游戏中后台音量变化只记录事件而不弹岛");
            PrivateCall(window, "OnBatteryStatusChanged", null, new BatteryStatusInfo(48, true, BatteryChargeType.StandardAC, BatteryPowerEvent.PluggedIn, null, IsOnAcPower: true));
            Check(!layers.IsActive("battery") && inbox.History.Any(x => x.Kind == DesktopEventKind.PowerConnected), "游戏中插拔电源仍更新和记录，但不弹充电动画");
            PrivateCall(window, "OnBluetoothDeviceConnected", null, new BluetoothDeviceInfo("game-headphones", "测试耳机", BluetoothDeviceCategory.Audio, 80, true));
            Check(!layers.IsActive("bluetooth"), "游戏中连接蓝牙耳机不抢占主岛");
            PrivateCall(window, "OnClipboardContentCopied", null, ("游戏内复制", false));
            Check(!layers.IsActive("clipboard") && !AgentField<bool>(window, "_clipboardLocked"), "游戏内复制不创建持续提醒");
            var suppressed = new List<uint>(); SetAgentField(window, "_suppressSystemPopup", new Action<DesktopNotification>(item => suppressed.Add(item.WindowsId!.Value)));
            var message = new DesktopNotification("game-message", "test.qq", "QQ", "联系人", "游戏中收到的正文", DateTimeOffset.Now, WindowsId: 90210);
            PrivateCall(window, "OnSystemToastsReceived", new[] { message }, false);
            Check(inbox.History.Any(x => x.Key == message.Key && x.Body == message.Body) && inbox.PendingCount == 0, "QQ 等通知的正文保留在通知中心，不排队补弹");
            Check(suppressed.SequenceEqual(new uint[] { 90210 }), "免打扰同步撤下已接管的新系统横幅");
            Check(AgentField<DesktopNotification?>(window, "_desktopCurrent") == null, "游戏期间消息不会打开通知小岛");
            Check(PrivateCall(window, "ShowPillNotification", IslandNotification.Seconds("system", 30)) is false && !layers.IsShowing("system"), "系统和插件的统一弹窗入口遵守游戏免打扰");
            AgentStatus.Set(AgentRunState.Thinking, "游戏后台任务", owner: owner); PrivateCall(window, "UpdateAgentStatusUi");
            Check(layers.IsShowing("agentstatus") && !layers.IsActive("agentstatus"), "助手保留运行状态但不自动显示卫星岛");
            var approval = new ApprovalRequest("game-approval", "read", "{}", "隔离请求", 60, DateTime.UtcNow);
            SetAgentField(window, "_approvalCurrent", approval); PrivateCall(window, "PresentCurrentApproval");
            Check(AgentField<ApprovalRequest>(window, "_approvalCurrent").Id == approval.Id && !layers.IsActive("approval"), "审批仍等待原有裁决且不自动弹出，不代替用户授权");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar, "审批不会突破游戏最小态锁定");
            SetAgentField(window, "_approvalCurrent", null); AgentStatus.Reset(owner: "approval"); AgentStatus.Reset(owner: owner); layers.Hide("approval"); layers.Hide("agentstatus");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar && !layers.IsActive("gamequiet"), "游戏持续运行时保持最小态，不常驻游戏通知");
            PrivateCall(window, "SetState", IslandState.Pill, true);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar, "游戏期间助手或媒体不能从其他入口重新展开空胶囊");
            State(false, 3);
            Check(!AgentField<bool>(window, "_gameQuietActive") && !SettingsRegistry.GetBool(options, "NotificationQuietMode", false) && quiet.IsEnabled && quiet.IsChecked == false, "游戏退出恢复原来的非静默状态");
            Check(inbox.PendingCount == 0 && AgentField<DesktopNotification?>(window, "_desktopCurrent")?.Kind != DesktopEventKind.Message
                && !AgentField<alpha_island.ViewModels.IslandWorkspaceViewModel>(window, "_workspace").Notifications.Burst.Items.Any(n => n.Key == message.Key), "退出后不补弹旧消息，允许新的合并摘要");
            PrivateCall(window, "OnVolumeChanged", null, new VolumeInfo(49, false));
            Check(layers.IsActive("volume"), "游戏退出后新音量事件正常弹出");
            Reset(); State(true);
            using (var playing = new SmtcService(window.Dispatcher))
            {
                SetAgentField(window, "_smtc", playing);
                var track = new NowPlayingInfo { Title = "游戏中播放", SourceApp = "cloudmusic.exe", State = PlaybackState.Playing };
                playing.SetDebugNowPlaying(track); PrivateCall(window, "ApplyMedia", track);
                Check(AgentField<IslandState>(window, "_state") == IslandState.Bar && !AgentField<bool>(window, "_upNextShowing"),
                    "游戏中媒体播放和换曲不会重新弹出音乐岛");
                State(false, 4);
                Check(AgentField<IslandState>(window, "_state") == IslandState.Bar,
                    "退出游戏后，未置顶播放不会恢复为永久音乐岛");
                PrivateCall(window, "SetMusicPinned", true, true);
                Check(AgentField<bool>(window, "_isPinned"), "进入游戏前的音乐手动置顶已生效");
                State(true, 5);
                Check(AgentField<bool>(window, "_isPinned"), "游戏收起只隐藏音乐，不取消手动置顶");
                State(false, 6);
                Check(AgentField<IslandState>(window, "_state") == IslandState.Pill
                    && AgentField<string>(window, "_activeMainIslandKey") == "music" && AgentField<bool>(window, "_isPinned"),
                    "退出游戏后仍恢复手动置顶的音乐岛");
                PrivateCall(window, "SetMusicPinned", false, true);
                SetAgentField(window, "_smtc", null);
            }
            Reset(); State(true); SetAgentField(window, "_approvalCurrent", approval); PrivateCall(window, "PresentCurrentApproval");
            State(false, 4);
            Check(layers.IsActive("approval") && AgentField<IslandState>(window, "_state") == IslandState.Pill
                && AgentField<ApprovalRequest>(window, "_approvalCurrent").Id == approval.Id,
                "退出游戏恢复仍未过期的审批提示，并保留原审批请求");
            Reset(); SettingsRegistry.Write(options, "NotificationQuietMode", true); State(true); State(false, 4);
            Check(SettingsRegistry.GetBool(options, "NotificationQuietMode", false) && quiet.IsChecked == true, "原来手动静默已开启时，退出游戏继续保持手动静默");
            Reset(); SettingsRegistry.Write(options, "NotificationQuietMode", false); State(true);
            PrivateCall(window, "SetNotificationQuiet", true); State(false, 5);
            Check(SettingsRegistry.GetBool(options, "NotificationQuietMode", false), "游戏期间手动设置变化按最新值恢复，不被旧快照覆盖");
            Reset(); SettingsRegistry.Write(options, "NotificationQuietMode", false);
            PrivateCall(window, "SetLayerPinned", "status", true, false); State(true);
            Check(layers.IsPinned("status") && !layers.IsActive("status") && AgentField<IslandState>(window, "_state") == IslandState.Bar, "游戏期间保留置顶偏好但隐藏其展示，不能撑开最小态");
            State(false, 6); Check(layers.IsPinned("status") && layers.IsActive("status"), "游戏退出恢复手动置顶，不删除或重复创建");
            Reset(); State(true); SettingsRegistry.Write(options, "NotificationGameQuietEnabled", false); PrivateCall(window, "StopGameActivity");
            Check(!AgentField<bool>(window, "_gameQuietActive") && !SettingsRegistry.GetBool(options, "NotificationQuietMode", false), "关闭游戏免打扰选项立即恢复原手动设置");
            SettingsRegistry.Write(options, "NotificationGameQuietEnabled", true); Reset();
            SetAgentField(window, "_sessionLocked", true); State(true);
            Check(!layers.IsShowing("gamequiet") && AgentField<IslandState>(window, "_state") == IslandState.Bar, "锁屏期间不显示游戏名称或开启提示");
            SetAgentField(window, "_sessionLocked", false); State(false, 7);
            var probe = new GameProbeFixture();
            using var events = new GameActivityMonitor("event-game.exe", probe, () => []);
            events.Start(); PumpAnimations(80);
            Check(events.EventsRunning, "正式前台 WinEvent 钩子在独立消息线程安装");
            probe.Foreground = new(55, @"C:\Fixture\event-game.exe", "事件测试游戏");
            var source = (System.Windows.Interop.HwndSource)System.Windows.PresentationSource.FromVisual(root)!;
            GameNotifyForeground(3, source.Handle, 0, 0);
            var wait = Stopwatch.StartNew(); while (!events.Current.Active && wait.ElapsedMilliseconds < 450) PumpAnimations(10);
            Check(events.Current.Active, "原生前台事件触发即时检测，无需等一秒轮询");
        }
        finally
        {
            SetAgentField(window, "_suppressSystemPopup", null); SetAgentField(window, "_sessionLocked", false);
            SettingsRegistry.Write(options, "NotificationGameQuietEnabled", true); Reset();
        }
    }
}
