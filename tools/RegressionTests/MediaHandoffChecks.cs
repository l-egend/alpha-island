using System;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void MediaHandoffPolicyChecks()
    {
        Check(MediaHandoffPolicy.Source("cloudmusic.exe") == (MediaWindowKind.DesktopPlayer, "cloudmusic"), "网易云音乐匹配桌面媒体来源");
        Check(MediaHandoffPolicy.Source("CloudMusic.Desktop!App").Kind == MediaWindowKind.DesktopPlayer, "网易云来源匹配不区分大小写");
        Check(MediaHandoffPolicy.Source("Chrome.exe") == (MediaWindowKind.Browser, "chrome"), "Chrome 使用浏览器显隐策略");
        Check(MediaHandoffPolicy.Source("MicrosoftEdge_8weky!App") == (MediaWindowKind.Browser, "msedge"), "Edge 应用标识匹配正确的进程");
        foreach (var (source, process, name) in new[] {
            ("QQMusic.exe", "QQMusic", "QQ音乐"), ("Spotify.exe", "Spotify", "Spotify"),
            ("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify", "Spotify"),
            ("KuGou.exe", "KuGou", "酷狗音乐"), ("KwMusic.exe", "KwMusic", "酷我音乐"),
            ("AppleMusic.exe", "AppleMusic", "Apple Music"),
            ("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App", "AppleMusic", "Apple Music"),
            (@"D:\音乐软件\QQMusic\QQMusic.exe", "QQMusic", "QQ音乐") })
        {
            Check(MediaHandoffPolicy.Source(source) == (MediaWindowKind.DesktopPlayer, process), "播放器来源匹配 " + source);
            Check(MediaSourceNames.DisplayName(source) == name, "播放器名称与联动来源一致 " + source);
        }
        foreach (string source in new[] { "QQMusicUpdater.exe", "cloudmusic_reporter.exe", "SpotifyMigrator.exe",
            @"C:\Spotify\SpotifyStartupTask.exe", "unrecognized.exe", "", "SomeSpotifyClone.exe" })
            Check(MediaHandoffPolicy.Source(source).Kind == MediaWindowKind.None, "不接管辅助或未知进程 " + source);
        MediaGenieGeometryChecks();
        var options = new IslandOptions();
        Check(SettingsRegistry.GetBool(options, "MediaWindowHandoffEnabled", true), "媒体窗口联动试验默认启用");
        foreach (string retired in new[] { "线性", "正弦", "二次曲线", "三次曲线", "无效曲线" })
        {
            SettingsRegistry.SetExtra(options, "MediaWindowHandoffEasing", retired);
            SettingsRegistry.SetExtra(options, "plugin/vivo.office/enabled", true);
            var restored = JsonSerializer.Deserialize<IslandOptions>(JsonSerializer.Serialize(options))!.Normalized();
            Check(!restored.Extras.ContainsKey("MediaWindowHandoffEasing")
                && SettingsRegistry.GetBool(restored, "plugin/vivo.office/enabled", false),
                "旧试用曲线配置安全清理并保留插件开关 " + retired);
        }
        SettingsRegistry.Write(options, "MediaWindowHandoffEnabled", false);
        Check(!SettingsRegistry.GetBool(options.Normalized(), "MediaWindowHandoffEnabled", true), "试验开关关闭后可保存并恢复");
    }

    private static void MediaHandoffUiChecks(alpha_island.MainWindow window, System.Windows.Controls.Grid root)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        var extras = new Dictionary<string, JsonElement>(options.Extras);
        var oldMedia = AgentField<SmtcService?>(window, "_smtc");
        var oldState = AgentField<IslandState>(window, "_state");
        var pages = AgentField<IslandPageHost>(window, "_pages"); string oldPage = pages.ActiveId;
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        using var media = new SmtcService(window.Dispatcher);
        var info = new NowPlayingInfo { SourceApp = "cloudmusic.exe", Title = "窗口动画回归", State = PlaybackState.Playing, CanToggle = true };
        bool Active() => (bool)PrivateResult("IsIslandActive", "music")!;
        object? PrivateResult(string name, params object?[] args) => typeof(alpha_island.MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
        void Foreground(MediaWindowKind kind, bool foreground) => PrivateCall(window, "OnMediaWindowChanged", new MediaWindowState(new IntPtr(123), kind, new Rect(100, 100, 900, 700), foreground, !foreground));
        try
        {
            SetAgentField(window, "_smtc", media); layers.HideAll();
            SettingsRegistry.Write(options, "MediaWindowHandoffEnabled", true);
            SettingsRegistry.Write(options, "MediaHideWhenPaused", false);
            media.SetDebugNowPlaying(info); PrivateCall(window, "ApplyMedia", info);
            PrivateCall(window, "SwitchMainIsland", "music"); PrivateCall(window, "SetMusicPinned", true, true);
            PrivateCall(window, "SetState", IslandState.Pill);
            Check(Active(), "网易云在后台时媒体正常显示");
            Foreground(MediaWindowKind.DesktopPlayer, true); root.UpdateLayout();
            Check(!Active() && window.PillLayer.Visibility == Visibility.Collapsed && AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "播放器在前台时收回媒体，避免空白胶囊");
            Check(AgentField<bool>(window, "_isPinned") && media.Current?.State == PlaybackState.Playing && window.TitleText.Text == info.Title,
                "前台隐藏保留媒体置顶、曲目和播放状态");
            var changed = info with { Title = "前台切歌" }; media.SetDebugNowPlaying(changed); PrivateCall(window, "ApplyMedia", changed);
            Check(!AgentField<bool>(window, "_upNextShowing") && !Active(), "前台切歌提示不能重新弹出媒体岛");
            var paused = changed with { State = PlaybackState.Paused };
            media.SetDebugNowPlaying(paused); PrivateCall(window, "ApplyMedia", paused);
            media.SetDebugNowPlaying(changed); PrivateCall(window, "ApplyMedia", changed);
            Check(!AgentField<bool>(window, "_upNextShowing") && !Active(), "前台暂停和播放继续保持媒体岛隐藏");
            PrivateCall(window, "SetState", IslandState.Pill);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar, "前台隐藏时自动展开入口不会产生空胶囊");
            Foreground(MediaWindowKind.DesktopPlayer, false); root.UpdateLayout();
            Check(Active() && AgentField<IslandState>(window, "_state") == IslandState.Pill && AgentField<bool>(window, "_isPinned"),
                "播放器退到后台后恢复媒体岛与原置顶");
            PrivateCall(window, "SetMusicPinned", false);
            Foreground(MediaWindowKind.Browser, true);
            Check(!Active() && AgentField<IslandState>(window, "_state") == IslandState.Bar, "浏览器前台只隐藏媒体");
            Foreground(MediaWindowKind.Browser, false);
            Check(Active() && AgentField<IslandState>(window, "_state") == IslandState.Pill
                && AgentField<alpha_island.Animation.MediaHandoffOverlay?>(window, "_mediaHandoff") == null, "浏览器后台恢复媒体且不创建动画窗口");
            PrivateCall(window, "SelectComponent", "focus"); PrivateCall(window, "ToggleCardPin");
            Foreground(MediaWindowKind.DesktopPlayer, true);
            Check(layers.IsPinned("card/focus") && AgentField<IslandState>(window, "_state") == IslandState.Pill,
                "前台隐藏不取消其他卡片的置顶");
            Foreground(MediaWindowKind.DesktopPlayer, false);
            PrivateCall(window, "SetLayerPinned", "card/focus", false); layers.Hide("card/focus");

            using var tracker = new MediaWindowService(window.Dispatcher);
            typeof(MediaWindowService).GetProperty("Current", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(tracker,
                new MediaWindowState(new IntPtr(123), MediaWindowKind.DesktopPlayer, new Rect(100, 100, 900, 700), false, true));
            SetAgentField(window, "_mediaWindows", tracker);
            PrivateCall(window, "SelectComponent", IslandPageIds.Music); root.UpdateLayout();
            SetAgentField(window, "_mediaHandoffInFlight", true); SetAgentField(window, "_mediaOpening", true);
            SetAgentField(window, "_mediaHandoffSourceCovered", false);
            PrivateCall(window, "SyncForegroundMediaVisibility");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Card, "打开动画快照尚未呈现时保留原始媒体卡片");
            SetAgentField(window, "_mediaHandoffSourceCovered", true); PrivateCall(window, "SyncForegroundMediaVisibility");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar, "打开动画快照覆盖卡片后才收回底层媒体");
            SetAgentField(window, "_mediaHandoffInFlight", false); SetAgentField(window, "_mediaOpening", false);
            SetAgentField(window, "_mediaHandoffSourceCovered", false);
            PrivateCall(window, "SelectComponent", IslandPageIds.Music); root.UpdateLayout();
            Check((bool)PrivateResult("IsMediaOpenSurface", window.TitleText)!, "媒体卡片文字属于打开播放器的点击区域");
            Check(!(bool)PrivateResult("IsMediaOpenSurface", window.PlayPauseButton)!, "播放按钮不触发打开播放器");
            Check(!(bool)PrivateResult("IsMediaOpenSurface", window.NextButton)!, "切歌按钮不触发打开播放器");
            Check(!(bool)PrivateResult("IsMediaOpenSurface", window.ProgressTrack)!, "拖动进度条不触发打开播放器");
            Check(!(bool)PrivateResult("IsMediaOpenSurface", window.MediaSourcesButton)!, "媒体来源菜单不触发打开播放器");
            SettingsRegistry.Write(options, "MediaWindowHandoffEnabled", false);
            Check(!(bool)PrivateResult("IsMediaOpenSurface", window.TitleText)!, "关闭开关后恢复原卡片点击行为");
            Foreground(MediaWindowKind.DesktopPlayer, true);
            Check(Active(), "关闭试验开关后前台不再隐藏媒体");
            SetAgentField(window, "_mediaWindows", null);

            var visual = new System.Windows.Media.DrawingVisual();
            using (var context = visual.RenderOpen())
                context.DrawRectangle(System.Windows.Media.Brushes.SteelBlue, null, new Rect(0, 0, 64, 64));
            var art = new System.Windows.Media.Imaging.RenderTargetBitmap(64, 64, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            art.Render(visual); art.Freeze();
            MediaGenieRenderingChecks(art);
            var animation = new alpha_island.Animation.MediaHandoffOverlay(new Rect(200, 120, 800, 600), new Rect(740, 12, 344, 48), art, art,
                "动画生命周期回归", opening: false);
            try
            {
                var playing = animation.PlayAsync();
                Check(ReferenceEquals(playing, animation.PlayAsync()), "重复播放调用复用同一任务且不重复订阅渲染" );
                var handle = new System.Windows.Interop.WindowInteropHelper(animation).Handle;
                int style = alpha_island.Interop.NativeMethods.GetWindowLong(handle, alpha_island.Interop.NativeMethods.GWL_EXSTYLE);
                var transitionLease = alpha_island.Interop.DwmTransitionLease.TryAcquire(handle);
                Check(transitionLease?.Applied == true, "真实窗口成功提前关闭 DWM 最小化与还原过渡");
                Check(alpha_island.Interop.NativeMethods.GetProp(handle, alpha_island.Interop.DwmTransitionLease.OriginalProperty) != IntPtr.Zero,
                    "窗口标记保留系统过渡恢复信息");
                transitionLease!.Dispose(); transitionLease.Dispose();
                Check(!transitionLease.Applied && alpha_island.Interop.NativeMethods.GetProp(handle, alpha_island.Interop.DwmTransitionLease.OriginalProperty) == IntPtr.Zero,
                    "释放及重复释放恢复系统过渡并清除窗口标记");
                // Simulate a stale marker left by an island process that was terminated.
                alpha_island.Interop.NativeMethods.SetProp(handle, alpha_island.Interop.DwmTransitionLease.OriginalProperty, new IntPtr(1));
                using (var recovered = alpha_island.Interop.DwmTransitionLease.TryAcquire(handle))
                    Check(recovered?.Applied == true && alpha_island.Interop.NativeMethods.GetProp(handle, alpha_island.Interop.DwmTransitionLease.OriginalProperty) == new IntPtr(1),
                        "岛进程重启能接续窗口原有恢复标记");
                Check(alpha_island.Interop.NativeMethods.GetProp(handle, alpha_island.Interop.DwmTransitionLease.OriginalProperty) == IntPtr.Zero,
                    "重启接续后释放不遗留过渡禁用标记");
                typeof(MediaWindowService).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tracker, handle);
                typeof(MediaWindowService).GetField("_kind", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tracker, MediaWindowKind.Browser);
                typeof(MediaWindowService).GetMethod("SuppressTransitions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tracker, null);
                Check(!tracker.TransitionsSuppressed && alpha_island.Interop.NativeMethods.GetProp(handle, alpha_island.Interop.DwmTransitionLease.OriginalProperty) == IntPtr.Zero,
                    "浏览器显隐策略不修改系统窗口过渡");
                typeof(MediaWindowService).GetField("_kind", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tracker, MediaWindowKind.DesktopPlayer);
                typeof(MediaWindowService).GetMethod("SuppressTransitions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tracker, null);
                Check(tracker.TransitionsSuppressed, "网易云窗口识别完成后提前设置过渡禁用");
                tracker.SetSource("chrome.exe");
                Check(!tracker.TransitionsSuppressed && alpha_island.Interop.NativeMethods.GetProp(handle, alpha_island.Interop.DwmTransitionLease.OriginalProperty) == IntPtr.Zero,
                    "切换至浏览器来源及时恢复播放器系统过渡");
                Check((style & alpha_island.Interop.NativeMethods.WS_EX_NOACTIVATE) != 0
                    && (style & alpha_island.Interop.NativeMethods.WS_EX_TRANSPARENT) != 0, "过渡窗口点击穿透且不抢焦点");
                PumpAnimations(650);
                Check(playing.IsCompletedSuccessfully && playing.Result && animation.Progress == 1, "真实渲染循环完成动画并退出逐帧监听");
                foreach (string source in new[] { "cloudmusic.exe", "QQMusic.exe", "Spotify.exe", "KuGou.exe", "KwMusic.exe", "AppleMusic.exe" })
                    MediaMinimizeEntryChecks(window, root, media, handle, source);
                MediaWindowMinimizeEventChecks(window.Dispatcher, handle);
            }
            finally { tracker.Dispose(); animation.Close(); }
            Check(alpha_island.Interop.DwmTransitionLease.TryAcquire(new IntPtr(123)) == null, "无效窗口不能留下过渡控制记录");
            var canceled = new alpha_island.Animation.MediaHandoffOverlay(new Rect(740, 12, 344, 48), new Rect(200, 120, 800, 600), art, art,
                "动画取消回归", opening: true);
            var cancelTask = canceled.PlayAsync(); canceled.Close();
            Check(ReferenceEquals(cancelTask, canceled.PlayAsync()), "已关闭过渡窗口不能再次启动渲染" );
            Check(cancelTask.IsCompletedSuccessfully && !cancelTask.Result, "取消动画立即释放等待任务和过渡窗口");
        }
        finally
        {
            SetAgentField(window, "_mediaWindows", null);
            SetAgentField(window, "_mediaSourceForeground", false); SetAgentField(window, "_mediaHandoffInFlight", false);
            SetAgentField(window, "_mediaOpening", false); SetAgentField(window, "_mediaHandoffSourceCovered", false);
            PrivateCall(window, "SetMusicPinned", false); layers.HideAll();
            options.Extras = extras; SetAgentField(window, "_smtc", oldMedia);
            PrivateCall(window, "SetState", oldState, true, oldPage);
        }
    }

}
