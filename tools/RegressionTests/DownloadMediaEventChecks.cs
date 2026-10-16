using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static (DownloadMonitorService Monitor, string Manifest, string Log) SteamFixture(string root, string suffix)
    {
        string steam = Path.Combine(root, "Steam-" + suffix), profile = Path.Combine(steam, "Browser");
        Directory.CreateDirectory(profile); Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        Directory.CreateDirectory(Path.Combine(steam, "logs"));
        string manifest = Path.Combine(steam, "steamapps", "appmanifest_1172470.acf");
        string log = Path.Combine(steam, "logs", "content_log.txt");
        WriteSteamManifest(manifest, 1030);
        return (new DownloadMonitorService(profile, steam), manifest, log);
    }

    private static void WriteSteamManifest(string path, long flags, long? updated = null) => File.WriteAllText(path,
        $"\"AppState\"\n{{\n\"appid\" \"1172470\"\n\"name\" \"Apex Legends\"\n\"StateFlags\" \"{flags}\"\n" +
        $"\"LastUpdated\" \"{updated ?? DateTimeOffset.Now.AddDays(-3).ToUnixTimeSeconds()}\"\n" +
        "\"BytesToDownload\" \"355410192\"\n\"BytesDownloaded\" \"36139312\"\n" +
        "\"BytesToStage\" \"728741758\"\n\"BytesStaged\" \"380996514\"\n}\n");

    private static string SteamEvent(string message, int seconds = -10) =>
        $"[{DateTime.Now.AddSeconds(seconds).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}] AppID 1172470 {message}\n";

    private static void SteamDownloadChecks(string root)
    {
        var (monitor, manifest, log) = SteamFixture(root, "state");
        monitor.Sample();
        Check(monitor.Current.Single().Status == DownloadState.Unknown && !monitor.Current.Single().IsRunning,
            "Steam UpdateStarted 标记没有实时日志时不会当作正在下载");
        File.WriteAllText(log, SteamEvent("App update changed : Running Update,Downloading,Staging,")); monitor.Sample();
        var task = monitor.Current.Single();
        Check(task.IsRunning && task.Status == DownloadState.Downloading && !task.HasReliableProgress && !task.CanMeasureSpeed,
            "Steam 日志确认下载阶段，清单快照不作为实时进度和速度");
        Check(task.TotalBytes == 355410192 && task.ReceivedBytes == 36139312, "Steam 网络字节与文件更新字节不再相加");
        File.AppendAllText(log, SteamEvent("update canceled : Disabled (Suspended)", -9)
            + SteamEvent("App update changed : Running Update,Downloading,Staging,Stopping,", -9)
            + SteamEvent("App update changed : None", -9)
            + SteamEvent("state changed : Update Required,Fully Installed,Update Queued,Update Started, (Suspended)", -9));
        monitor.Sample(); task = monitor.Current.Single();
        Check(task.Status == DownloadState.Paused && !task.IsRunning && !task.IsCompleted && !task.SourceRunning,
            "真实 Steam 暂停日志与残留 1030 标记识别为暂停，Stopping 和 None 不误改状态");
        monitor.Sample(); Check(monitor.Current.Single().Status == DownloadState.Paused, "长时间暂停且日志不变时保留暂停状态");
        File.AppendAllText(log, SteamEvent("state changed : Update Required,Fully Installed,Update Queued,Update Running,", -8)
            + SteamEvent("App update changed : Running Update,Verifying Installed,", -8)); monitor.Sample();
        Check(monitor.Current.Single().Status == DownloadState.Verifying, "Steam 校验与下载分开显示");
        File.AppendAllText(log, SteamEvent("App update changed : Running Update,Preallocating,", -7)); monitor.Sample();
        Check(monitor.Current.Single().Status == DownloadState.Preparing, "Steam 预分配识别为准备阶段");
        File.AppendAllText(log, SteamEvent("App update changed : Running Update,Staging,", -6)); monitor.Sample();
        Check(monitor.Current.Single().Status == DownloadState.Installing, "Steam 仅更新文件时不误报网络下载");
        File.AppendAllText(log, SteamEvent("App update changed : Running Update,Downloading,Staging,", -5)); monitor.Sample();
        Check(monitor.Current.Single().Status == DownloadState.Downloading, "Steam 恢复下载解除暂停状态");
        File.AppendAllText(log, SteamEvent("state changed : Update Required,Fully Installed,Update Queued, (Update delayed for 100 secs)", -4)); monitor.Sample();
        Check(monitor.Current.Single().Status == DownloadState.Queued && !monitor.Current.Single().IsCompleted, "排队任务不误报下载完成");
        WriteSteamManifest(manifest, 6); monitor.Sample();
        Check(monitor.Current.Count == 0, "仅有待更新标记的游戏不伪装为活跃下载");
        WriteSteamManifest(manifest, 4, DateTimeOffset.Now.AddSeconds(1).ToUnixTimeSeconds());
        File.AppendAllText(log, SteamEvent("state changed : Fully Installed,", 1)); monitor.Sample();
        Check(monitor.Current.Single().IsCompleted, "Steam 依据明确的安装完成事件提示完成，无需清单比例达到 100%");
        string residual = Path.Combine(Path.GetDirectoryName(manifest)!, "downloading", Path.GetFileName(manifest));
        Directory.CreateDirectory(Path.GetDirectoryName(residual)!); WriteSteamManifest(residual, 1030); monitor.Sample();
        Check(monitor.Current.Count == 1 && monitor.Current.Single().IsCompleted, "安装完成的库根清单优先于 downloading 目录残留，避免重复或幽灵下载");
        File.Delete(residual);
        File.WriteAllText(log, SteamEvent("update canceled : Disabled (Suspended)")); WriteSteamManifest(manifest, 1030); monitor.Sample();
        Check(monitor.Current.Single().Status == DownloadState.Paused, "日志截断或轮换后重新建立状态");
        File.Delete(log); monitor.Sample();
        Check(monitor.Current.Single().Status == DownloadState.Unknown, "日志不可用时丢弃旧状态，保守降级不猜测下载");
        var reader = new SteamDownloadLog(); File.WriteAllText(log, new string('x', 2 * 1024 * 1024 + 100) + "\n" + SteamEvent("App update changed : Running Update,Downloading,"));
        reader.Refresh(log); Check(reader.ForApp("1172470")?.State == DownloadState.Downloading, "大日志首次只读尾部仍能提取状态");
        string pause = SteamEvent("update canceled : Disabled (Suspended)", -5);
        File.AppendAllText(log, pause[..^1]); reader.Refresh(log);
        Check(reader.ForApp("1172470")?.State == DownloadState.Downloading, "日志正在写入的半行不会提前改变状态");
        File.AppendAllText(log, "\n"); reader.Refresh(log);
        Check(reader.ForApp("1172470")?.State == DownloadState.Paused, "下一次增量读取拼接半行后准确识别暂停");
    }

    private static void DownloadAndMediaEventUiChecks(alpha_island.MainWindow window, Grid root, string testRoot)
    {
        var type = window.GetType();
        object? Get(string name) => ReadWindowMember(window, name);
        void Set(string name, object? value) => WriteWindowMember(window, name, value);
        var workspace = (alpha_island.ViewModels.IslandWorkspaceViewModel)ReadWindowMember(window, "_workspace")!;
        var oldDownloads = Get("_downloads"); var oldSmtc = Get("_smtc"); var oldState = (IslandState)Get("_state")!;
        var pages = (IslandPageHost)Get("_pages")!; string oldPage = pages.ActiveId;
        var layers = (IslandLayerHost)Get("_layers")!; var options = (IslandOptions)Get("_options")!;
        bool oldPause = options.PauseNotifyEnabled;
        using var smtc = new SmtcService(window.Dispatcher);
        try
        {
            var (monitor, manifest, log) = SteamFixture(testRoot, "ui"); Set("_downloads", monitor); Set("_smtc", smtc);
            File.WriteAllText(log, SteamEvent("App update changed : Running Update,Downloading,Staging,")); monitor.Sample();
            var now = DateTime.UtcNow;
            PrivateCall(window, "ApplyDownloadSample", monitor.Current.ToList(), now);
            PrivateCall(window, "SetState", IslandState.Card, false, IslandPageIds.Download); root.UpdateLayout();
            Check(window.DlTrack1.Visibility == Visibility.Collapsed && !window.DlMeta1.Text.Contains('%')
                && !window.DlMeta1.Text.Contains("剩余") && window.DlMeta1.Text.Contains("正在下载"),
                "真实下载卡完整收起 Steam 轨道和填充，无虚假百分比与剩余时间");
            Check(!window.DownloadNotifyDetail.Text.Contains("B/s") && !window.DownloadNotifyDetail.Text.Contains("总大小"),
                "Steam 提示不借整机网速或合并下载与落盘大小");
            File.AppendAllText(log, SteamEvent("update canceled : Disabled (Suspended)", -9)); monitor.Sample();
            PrivateCall(window, "ApplyDownloadSample", monitor.Current.ToList(), now.AddSeconds(2));
            Check(window.DownloadNotifyTitle.Text == "Steam 已暂停" && window.DlMeta1.Text.StartsWith("已暂停")
                && window.DownloadCardSummaryText.Text.Contains("0 个进行中"),
                "暂停立即更新提示和卡片计数，不误报完成");
            Check(!window.DlMeta1.Text.Contains("B/s") && !window.DownloadNotifyDetail.Text.Contains("B/s"), "暂停清除之前的平滑速度显示");
            PrivateCall(window, "SelectComponent", IslandPageIds.System); PrivateCall(window, "UpdateSideBubbleHint");
            Check(window.SideBubbleText.Text == "Steam 已暂停" && window.SideBubbleIcon.Text == "\uE769"
                && window.SideBubbleDetail.Text.Contains("Apex Legends"), "旁侧下载缩略岛同步暂停标题、图标和游戏名");
            PrivateCall(window, "SelectComponent", IslandPageIds.Download); PumpAnimations(30); root.UpdateLayout();
            layers.Hide("download"); PrivateCall(window, "ApplyDownloadSample", monitor.Current.ToList(), now.AddSeconds(4));
            Check(!layers.IsActive("download"), "暂停状态重复采样不会再次弹通知或续期");
            root.UpdateLayout(); PumpAnimations(30);
            SaveThemePreview(window.DownloadCardLayer, "docs/screens/steam-download-paused.png");

            PrivateCall(window, "ApplyNoMedia");
            var media = new NowPlayingInfo { Title = "状态提醒测试", SourceApp = "msedge.exe", SessionId = "event-test",
                ContentKind = MediaContentKind.Video, State = PlaybackState.Playing, CanToggle = true, Duration = TimeSpan.FromMinutes(3) };
            smtc.SetDebugNowPlaying(media); PrivateCall(window, "ApplyMedia", media);
            layers.Request(IslandNotification.Refreshing("netspeed", 30)); layers.SetPinned("netspeed", true);
            PrivateCall(window, "SetState", IslandState.Pill, false, IslandPageIds.System); PrivateCall(window, "SwitchMainIsland", "netspeed");
            var paused = media with { State = PlaybackState.Paused };
            smtc.SetDebugNowPlaying(paused); PrivateCall(window, "ApplyMedia", paused);
            Check((string)Get("_activeMainIslandKey")! == "music" && layers.PreferredWinnerKey == "music"
                && layers.IsPinned("netspeed") && (string)Get("_upNextKind")! == "pause",
                "旁侧媒体暂停自动移到主岛，保留其他置顶岛并显示暂停提醒");
            PrivateCall(window, "SwitchMainIsland", "netspeed"); PrivateCall(window, "ApplyMedia", paused);
            Check((string)Get("_activeMainIslandKey")! == "netspeed", "重复媒体状态刷新不会抢回用户选中的主岛");
            smtc.SetDebugNowPlaying(media); PrivateCall(window, "ApplyMedia", media);
            Check((string)Get("_activeMainIslandKey")! == "music" && (string)Get("_upNextKind")! == "play", "后台媒体恢复播放也主动移到主岛提醒");
            PrivateCall(window, "SelectComponent", IslandPageIds.System); PrivateCall(window, "SwitchMainIsland", "netspeed");
            smtc.SetDebugNowPlaying(paused); PrivateCall(window, "ApplyMedia", paused);
            Check(pages.ActiveId == IslandPageIds.Music && (IslandState)Get("_state")! == IslandState.Card,
                "其他卡片展开时媒体暂停切到播放器详情，并保持卡片展开");
            PrivateCall(window, "SelectComponent", IslandPageIds.System); PrivateCall(window, "SwitchMainIsland", "netspeed");
            options.PauseNotifyEnabled = false; workspace.Media.Commit(media); PrivateCall(window, "ApplyMedia", paused);
            Check(pages.ActiveId == IslandPageIds.System && (string)Get("_activeMainIslandKey")! == "netspeed", "关闭暂停提醒后不主动切换媒体");
            options.PauseNotifyEnabled = true;
            layers.Request(IslandNotification.Seconds("approval", 30)); workspace.Media.Commit(media); PrivateCall(window, "ApplyMedia", paused);
            Check((string)Get("_activeMainIslandKey")! != "music" && layers.IsActive("approval"), "媒体状态变化不会抢占待审批操作");
            layers.Hide("approval"); Set("_clipboardLocked", true); workspace.Media.Commit(media); PrivateCall(window, "ApplyMedia", paused);
            Check((string)Get("_activeMainIslandKey")! != "music", "待粘贴剪切板的优先交互不会被媒体暂停打断"); Set("_clipboardLocked", false);
            Set("_fileDragActive", true); workspace.Media.Commit(media); PrivateCall(window, "ApplyMedia", paused);
            Check(pages.ActiveId == IslandPageIds.System, "文件拖入过程中媒体提醒不替换投递卡片"); Set("_fileDragActive", false);
            PrivateCall(window, "SetState", IslandState.Pill, false, IslandPageIds.System);
            var next = media with { Title = "下一段媒体" }; smtc.SetDebugNowPlaying(next); PrivateCall(window, "ApplyMedia", next);
            Check((string)Get("_activeMainIslandKey")! == "music" && (string)Get("_upNextKind")! == "upnext", "换曲或视频标题变化也能唤回后台媒体岛");
            PrivateCall(window, "DismissUpNext");
            Check((IslandState)Get("_state")! == IslandState.Pill && layers.IsPinned("netspeed"), "媒体提示结束不会连同其他正在显示的岛一起收回");
        }
        finally
        {
            options.PauseNotifyEnabled = oldPause; Set("_clipboardLocked", false); Set("_fileDragActive", false);
            PrivateCall(window, "DismissUpNext"); layers.SetPinned("netspeed", false); layers.HideAll();
            Set("_downloads", oldDownloads); Set("_smtc", oldSmtc);
            workspace.Downloads.Reset();
            PrivateCall(window, "SetState", oldState, false, oldPage); PumpAnimations(300);
        }
    }
}
