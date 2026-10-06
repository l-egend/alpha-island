using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void PausedMediaVisibilityChecks(alpha_island.MainWindow window, Grid root)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        var savedExtras = new Dictionary<string, JsonElement>(options.Extras);
        var oldSmtc = AgentField<SmtcService?>(window, "_smtc");
        var oldState = AgentField<IslandState>(window, "_state");
        var pages = AgentField<IslandPageHost>(window, "_pages"); string oldPage = pages.ActiveId;
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        bool oldPlay = options.PlayNotifyEnabled;
        bool oldPause = options.PauseNotifyEnabled;
        using var smtc = new SmtcService(window.Dispatcher);
        var media = new NowPlayingInfo { Title = "保留的后台媒体", SourceApp = "msedge.exe", SessionId = "pause-hide-ui",
            State = PlaybackState.Playing, CanToggle = true, Duration = TimeSpan.FromMinutes(3) };
        var paused = media with { State = PlaybackState.Paused };
        void Apply(NowPlayingInfo info) { smtc.SetDebugNowPlaying(info); PrivateCall(window, "ApplyMedia", info); root.UpdateLayout(); }
        bool Active() => (bool)typeof(alpha_island.MainWindow).GetMethod("IsIslandActive", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { "music" })!;
        try
        {
            SetAgentField(window, "_smtc", smtc); layers.HideAll();
            options.PauseNotifyEnabled = false;
            Check(!SettingsRegistry.GetBool(options, "MediaHideWhenPaused", false), "暂停隐藏默认关闭，旧配置保持可见");
            Apply(paused); PrivateCall(window, "SetMusicPinned", true, true); PrivateCall(window, "SetState", IslandState.Pill);
            Check(Active(), "关闭暂停隐藏时后台暂停的媒体仍可置顶");
            layers.SetPinned("netspeed", true); PrivateCall(window, "SwitchMainIsland", "netspeed");
            SettingsRegistry.Write(options, "MediaHideWhenPaused", true); PrivateCall(window, "SyncPausedMediaVisibility");
            Check(Active() && AgentField<List<string>>(window, "_activeSatelliteOrder").Contains("music"), "手动置顶优先于暂停隐藏，音乐卫星继续显示");
            Check(layers.IsPinned("netspeed") && AgentField<string>(window, "_activeMainIslandKey") == "netspeed"
                && AgentField<IslandState>(window, "_state") == IslandState.Pill && AgentField<bool>(window, "_isPinned"),
                "暂停隐藏保留其他主岛与媒体置顶");
            Apply(paused); Check(Active(), "置顶的暂停会话持续刷新仍显示");
            PrivateCall(window, "SetMusicPinned", false);
            Check(!Active() && !AgentField<List<string>>(window, "_activeSatelliteOrder").Contains("music")
                && layers.IsPinned("netspeed"), "取消媒体置顶后恢复暂停隐藏，并保留其他置顶岛");
            SettingsRegistry.Write(options, "MediaHideWhenPaused", false); PrivateCall(window, "SyncPausedMediaVisibility");
            SetAgentField(window, "_musicIslandDismissed", false); PrivateCall(window, "SyncNotificationLayers");
            Check(Active() && AgentField<List<string>>(window, "_activeSatelliteOrder").Contains("music"), "关闭隐藏开关立即恢复暂停媒体卫星");

            layers.SetPinned("netspeed", false); layers.HideAll();
            SettingsRegistry.Write(options, "MediaHideWhenPaused", true); Apply(media);
            PrivateCall(window, "SwitchMainIsland", "music"); PrivateCall(window, "SetState", IslandState.Pill);
            Apply(paused);
            PumpAnimations(450); root.UpdateLayout();
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar && window.PillLayer.Visibility == Visibility.Collapsed,
                "未置顶媒体暂停自动收回，不留下空胶囊");
            Check(!AgentField<bool>(window, "_isPinned") && smtc.Current?.State == PlaybackState.Paused
                && window.TitleText.Text == media.Title && window.PlayPauseButton.IsEnabled,
                "暂时隐藏不清空后台会话、媒体元数据或播放控制");
            PrivateCall(window, "UpdateSideBubbleHint"); Apply(paused);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar && !AgentField<bool>(window, "_upNextShowing"),
                "暂停通知与重复刷新不能将隐藏的媒体再次展开");
            PrivateCall(window, "SetState", IslandState.Pill);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "暂停隐藏后再次请求一级展开不能产生空白胶囊");
            PrivateCall(window, "IslandBody_MouseEnter", window.IslandBody,
                new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0));
            PumpAnimations(450); root.UpdateLayout();
            Check(AgentField<IslandState>(window, "_state") == IslandState.Card && pages.ActiveId == IslandPageIds.Music
                && window.CardCarouselHost.Opacity > .9 && window.TitleText.Text == media.Title && window.PlayPauseButton.IsEnabled,
                "暂停隐藏后手动悬停直接显示可操作的播放器，不经过空白一级胶囊");
            Apply(paused); PumpAnimations(200);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Card && window.CardCarouselHost.Opacity > .9,
                "手动打开的暂停播放器在持续刷新后仍正常显示");
            PrivateCall(window, "ToggleCardPin"); PumpAnimations(450); root.UpdateLayout();
            Check(AgentField<bool>(window, "_isPinned") && Active() && AgentField<IslandState>(window, "_state") == IslandState.Pill
                && window.PillLayer.Visibility == Visibility.Visible, "开启暂停隐藏后右键播放器卡片能置顶并显示胶囊");
            PrivateCall(window, "CollapseToBar");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill, "置顶暂停播放器不会被自动收回");
            Apply(media); Apply(paused); PumpAnimations(200);
            Check(AgentField<bool>(window, "_isPinned") && Active() && AgentField<IslandState>(window, "_state") != IslandState.Bar,
                "置顶播放器经历继续播放和再次暂停仍保持显示");
            PrivateCall(window, "SelectComponent", IslandPageIds.Music); PrivateCall(window, "ToggleCardPin"); PumpAnimations(450);
            Check(!AgentField<bool>(window, "_isPinned") && !Active() && AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "再次右键取消置顶后暂停隐藏立即生效");
            PrivateCall(window, "SetState", IslandState.Pill); PumpAnimations(450);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar && !AgentField<bool>(window, "_isPinned"),
                "未置顶时离开暂停播放器直接收回细条");
            PrivateCall(window, "IslandBody_MouseLeftButtonDown", window.IslandBody,
                new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left));
            Check(AgentField<IslandState>(window, "_state") == IslandState.Card && pages.ActiveId == IslandPageIds.Music,
                "暂停隐藏后点击细条也可以直接打开播放器卡片");
            PrivateCall(window, "CollapseToBar");
            PrivateCall(window, "SelectComponent", "focus"); PrivateCall(window, "ToggleCardPin");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill && layers.IsPinned("card/focus")
                && layers.IsVisible("card/focus"), "媒体暂停隐藏期间仍可从其他卡片新建置顶小岛");
            PrivateCall(window, "SetLayerPinned", "card/focus", false); layers.Hide("card/focus");
            SettingsRegistry.Write(options, "MediaHideWhenPaused", false); PrivateCall(window, "SyncPausedMediaVisibility");
            PrivateCall(window, "SetMusicPinned", true, true); PrivateCall(window, "SetState", IslandState.Pill);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill && Active(), "关闭开关后仍可置顶暂停主岛");
            SettingsRegistry.Write(options, "MediaHideWhenPaused", true); PrivateCall(window, "SyncPausedMediaVisibility");
            options.PlayNotifyEnabled = false; Apply(media);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill && Active()
                && AgentField<bool>(window, "_isPinned"), "即使恢复播放提示关闭，继续播放也会恢复原置顶媒体");
            Apply(paused); PrivateCall(window, "SelectComponent", IslandPageIds.Music);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Card && pages.ActiveId == IslandPageIds.Music
                && window.PlayPauseButton.IsEnabled, "暂停隐藏后仍可从组件菜单手动打开播放器继续播放");
            SettingsRegistry.Write(options, "MediaHideWhenPaused", false);
            options.PlayNotifyEnabled = true; options.PauseNotifyEnabled = false;
            Apply(paused); Apply(media with { PlaybackStateInferred = true });
            Check(AgentField<bool>(window, "_upNextShowing") && AgentField<string>(window, "_upNextKind") == "play",
                "经过音频仲裁确认的恢复播放仍会提醒");
            Apply(paused); Apply(media);
            Check(AgentField<bool>(window, "_upNextShowing") && AgentField<string>(window, "_upNextKind") == "play",
                "明确恢复播放仍保留正在播放提醒");
            Apply(paused);
            Check(!AgentField<bool>(window, "_upNextShowing"), "暂停时立即撤销旧播放提示，即使暂停提醒关闭");
            PrivateCall(window, "SetMusicPinned", false);
            PrivateCall(window, "CollapseToBar");
            SettingsRegistry.Write(options, "MediaHideWhenPaused", true);
            options.PauseNotifyEnabled = true;
            Apply(media); Apply(paused); PumpAnimations(450); root.UpdateLayout();
            Check(AgentField<bool>(window, "_upNextShowing") && AgentField<string>(window, "_upNextKind") == "pause"
                && AgentField<IslandState>(window, "_state") == IslandState.Pill && Active()
                && window.PillUpNext.Visibility == Visibility.Visible && window.PillUpNextText.Text == "已暂停",
                "开启暂停隐藏时先展开并显示已暂停提醒");
            var pauseUntil = AgentField<DateTime>(window, "_upNextUntil");
            Apply(paused);
            Check(AgentField<DateTime>(window, "_upNextUntil") == pauseUntil, "重复暂停刷新不会延长提醒时间");
            PrivateCall(window, "DismissUpNext"); PumpAnimations(450); root.UpdateLayout();
            Check(!Active() && AgentField<IslandState>(window, "_state") == IslandState.Bar
                && window.PillLayer.Visibility == Visibility.Collapsed, "暂停提醒结束后自动隐藏，不留下空胶囊");
            Apply(media); Apply(paused); Apply(media); root.UpdateLayout();
            Check(AgentField<bool>(window, "_upNextShowing") && window.PillUpNextText.Text == "正在播放"
                && Active(), "快速暂停后播放立即替换提醒，不被暂停隐藏吞掉");
            var playUntil = AgentField<DateTime>(window, "_upNextUntil");
            Apply(media);
            Check(AgentField<DateTime>(window, "_upNextUntil") == playUntil, "重复播放刷新不会重弹或延长提醒");
            options.PlayNotifyEnabled = false; Apply(paused); Apply(media);
            Check(!AgentField<bool>(window, "_upNextShowing"), "关闭恢复播放提醒后播放仍会清除旧暂停提醒");
            options.PlayNotifyEnabled = true;
            MediaAudioPromptChecks(window, root, options);
            SetAgentField(window, "_smtc", smtc); Apply(paused);
            PrivateCall(window, "DismissUpNext");
            SettingsRegistry.Write(options, "MediaHideWhenPaused", true);
            PrivateCall(window, "OnClipboardContentCopied", null, ("隔离剪切板测试", false));
            PrivateCall(window, "SyncPausedMediaVisibility");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill && layers.IsActive("clipboard"), "暂停隐藏不会连带收回待粘贴剪切板");
            var restored = JsonSerializer.Deserialize<IslandOptions>(JsonSerializer.Serialize(options))!;
            Check(SettingsRegistry.GetBool(restored, "MediaHideWhenPaused", false), "暂停隐藏开关可持久保存与读取");
        }
        finally
        {
            options.PlayNotifyEnabled = oldPlay; options.PauseNotifyEnabled = oldPause; options.Extras = savedExtras;
            SetAgentField(window, "_clipboardLocked", false); SetAgentField(window, "_isPinned", false);
            PrivateCall(window, "ApplyNoMedia"); layers.SetPinned("netspeed", false); layers.HideAll();
            SetAgentField(window, "_smtc", oldSmtc); PrivateCall(window, "SetState", oldState, false, oldPage);
        }
    }

    private static void MediaAudioPromptChecks(alpha_island.MainWindow window, Grid root, IslandOptions options)
    {
        using var service = new SmtcService(window.Dispatcher) { AudioProbeEnabled = true };
        SetAgentField(window, "_smtc", service);
        service.NowPlayingChanged += (_, info) => { if (info != null) PrivateCall(window, "ApplyMedia", info); };
        var info = new NowPlayingInfo { Title = "状态提示证据", SourceApp = "cloudmusic.exe", SessionId = "prompt-audio",
            State = PlaybackState.Paused, Position = TimeSpan.FromSeconds(10), Duration = TimeSpan.Zero };
        PrivateCall(service, "ObserveReportedState", PlaybackState.Paused);
        service.UpdateTrack(info, "prompt-audio", null);
        PrivateCall(window, "DismissUpNext");
        service.ReportAudioEvidence(0, 2, true, 0, true);
        Check(service.Current?.State == PlaybackState.Paused && !AgentField<bool>(window, "_upNextShowing"),
            "暂停尾音既不恢复播放也不误弹正在播放");
        service.ObserveTimelineEvidence(TimeSpan.FromSeconds(11), DateTimeOffset.UtcNow);
        service.ReportAudioEvidence(0, .5, true, 0, true); root.UpdateLayout();
        Check(service.Current?.IsPlaying == true && service.Current.PlaybackStateInferred
            && AgentField<bool>(window, "_upNextShowing") && window.PillUpNextText.Text == "正在播放",
            "自身音频与新进度确认恢复播放时真正触发界面提醒");
        var until = AgentField<DateTime>(window, "_upNextUntil");
        PrivateCall(service, "ObserveReportedState", PlaybackState.Playing);
        service.UpdateTrack(service.Current! with { Duration = TimeSpan.FromMinutes(3) }, "prompt-audio", null);
        Check(AgentField<DateTime>(window, "_upNextUntil") == until, "迟到原生播放确认和时长更新不重复提醒");
        service.ReportAudioEvidence(2, 0, true, 0, true); root.UpdateLayout();
        Check(service.Current?.State == PlaybackState.Paused && AgentField<bool>(window, "_upNextShowing")
            && window.PillUpNextText.Text == "已暂停", "播放器漏报暂停时自身持续静音仍触发已暂停提醒");
        PrivateCall(window, "DismissUpNext");
        options.PauseNotifyEnabled = false;
        service.ObserveTimelineEvidence(TimeSpan.FromSeconds(12), DateTimeOffset.UtcNow);
        service.ReportAudioEvidence(0, .5, true, 0, true);
        PrivateCall(window, "DismissUpNext");
        PrivateCall(service, "ObserveReportedState", PlaybackState.Paused);
        PrivateCall(service, "PublishResolvedState");
        service.UpdateTrack(service.Current! with { Duration = TimeSpan.Zero }, "prompt-audio", null);
        SetAgentField(window, "_lastFetchDuration", TimeSpan.Zero);
        PrivateCall(service, "ObserveReportedState", PlaybackState.Playing);
        service.UpdateTrack(service.Current! with { Duration = TimeSpan.FromMinutes(3) }, "prompt-audio", null);
        Check(AgentField<bool>(window, "_upNextShowing") && window.PillUpNextText.Text == "正在播放",
            "时长补齐和恢复播放同时到达不会吞掉提醒");
        options.PauseNotifyEnabled = true;
    }
}
