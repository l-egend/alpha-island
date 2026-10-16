using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void BackgroundVisibilityChecks(alpha_island.MainWindow window, Grid root, bool music)
    {
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        var options = AgentField<IslandOptions>(window, "_options");
        var inbox = AgentField<DesktopNotificationInbox>(window, "_desktopInbox");
        var oldMedia = AgentField<SmtcService?>(window, "_smtc");
        using var media = new SmtcService(window.Dispatcher);
        const string owner = "background-visibility-fixture";
        int notificationNumber = 0;
        void Reset()
        {
            PrivateCall(window, "FinishDesktopPopup");
            inbox.Clear();
            AgentStatus.Reset(owner: owner);
            SetAgentField(window, "_smtc", null);
            SetAgentField(window, "_mediaSourceForeground", false);
            SetAgentField(window, "_musicIslandDismissed", false);
            SetAgentField(window, "_isPinned", false);
            SetAgentField(window, "_upNextShowing", false);
            SetAgentField(window, "_upNextUntil", DateTime.MinValue);
            layers.HideAll();
            PrivateCall(window, "SetState", IslandState.Bar, true);
        }
        void StartActivity()
        {
            if (music)
            {
                SetAgentField(window, "_smtc", media);
                var info = new NowPlayingInfo { Title = "后台播放", SourceApp = "cloudmusic.exe", State = PlaybackState.Playing };
                media.SetDebugNowPlaying(info);
                PrivateCall(window, "ApplyMedia", info);
            }
            else
            {
                AgentStatus.Set(AgentRunState.Thinking, "后台思考", owner);
                PrivateCall(window, "UpdateAgentStatusUi");
            }
        }
        void Popup()
        {
            var toast = new DesktopNotification("background-fixture-" + ++notificationNumber, "fixture", "Fixture", "临时通知", "后台状态恢复测试", DateTimeOffset.Now);
            inbox.Add(toast, true);
            PrivateCall(window, "TickDesktopNotifications");
            Check(AgentField<DesktopNotification?>(window, "_desktopCurrent")?.Key == toast.Key,
                "临时通知通过正式队列展开");
        }
        void Expire()
        {
            SetAgentField(window, "_desktopExpires", DateTimeOffset.Now.AddSeconds(-1));
            PrivateCall(window, "TickDesktopNotifications");
            PumpAnimations(100); root.UpdateLayout();
        }
        bool Shown() => AgentField<IslandState>(window, "_state") == IslandState.Pill
            && (music ? window.PillLayer.Visibility == Visibility.Visible : window.AgentNotifyLayer.Visibility == Visibility.Visible);
        try
        {
            options.LyricsEnabled = false;
            options.KeepCompactWhilePlaying = false;
            SettingsRegistry.Write(options, "MediaHideWhenPaused", true);
            for (int cycle = 0; cycle < 3; cycle++)
            {
                Reset(); Popup();
                Check(AgentField<IslandState>(window, "_notificationReturnState") == IslandState.Bar,
                    "临时通知记住弹出前的细条形态");
                StartActivity(); Expire();
                Check(Shown(), music ? "临时通知结束后后台音乐直接恢复，不需要前台最小化" : "临时通知结束后后台助手直接恢复，不需要前台最小化");
                SetAgentField(window, "_hoverOutTicks", 1000);
                PrivateCall(window, "HoverTimer_Tick", null, EventArgs.Empty);
                Check(Shown(), "后台活动恢复后离开鼠标热区仍保留小岛");
                PrivateCall(window, "HandleOutsideIslandClick");
                Check(Shown(), "操作其他软件不会收掉仍在后台运行的岛");
                Check(!AgentField<bool>(window, "_isPinned") && !layers.AnyPinned,
                    "后台活动恢复不擅自更改用户置顶选择");
                Popup(); Expire();
                Check(Shown(), "连续通知结束仍恢复当前活动内容");
            }
            Reset(); Popup(); StartActivity();
            if (music)
            {
                media.SetDebugNowPlaying(new NowPlayingInfo());
                PrivateCall(window, "ApplyNoMedia");
            }
            else
            {
                AgentStatus.Reset(owner: owner);
                PrivateCall(window, "UpdateAgentStatusUi");
            }
            Expire();
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "后台活动已经结束时不复活旧岛");
            Reset(); Popup(); StartActivity();
            if (music)
            {
                SetAgentField(window, "_mediaSourceForeground", true);
                PrivateCall(window, "SyncForegroundMediaVisibility");
            }
            else layers.SetPresentationSuppressed("agentstatus", true);
            Expire();
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "通知结束保留前台应用隐藏规则");
            Reset(); Popup(); StartActivity();
            if (music) SetAgentField(window, "_musicIslandDismissed", true);
            else layers.Hide("agentstatus");
            Expire();
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "通知结束不强行恢复用户已撤下的活动图层");
            if (music)
            {
                Reset(); Popup(); StartActivity();
                var paused = media.Current! with { State = PlaybackState.Paused };
                media.SetDebugNowPlaying(paused); PrivateCall(window, "ApplyMedia", paused);
                SetAgentField(window, "_upNextShowing", false); SetAgentField(window, "_upNextUntil", DateTime.MinValue);
                PrivateCall(window, "SyncPausedMediaVisibility"); Expire();
                Check(AgentField<IslandState>(window, "_state") == IslandState.Bar,
                    "开启暂停隐藏时通知结束不显示已暂停媒体");
            }
            Reset(); Popup(); StartActivity(); Expire();
            PrivateCall(window, "CollapseToBar", true);
            SetAgentField(window, "_hoverOutTicks", 1000);
            PrivateCall(window, "HoverTimer_Tick", null, EventArgs.Empty);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "明确手动收起后不被后台保持规则强行展开");
            if (music)
            {
                Reset(); SettingsRegistry.Write(options, "MediaWindowHandoffEnabled", false);
                Popup(); StartActivity(); Expire();
                SetAgentField(window, "_hoverOutTicks", 1000);
                PrivateCall(window, "HoverTimer_Tick", null, EventArgs.Empty);
                Check(AgentField<IslandState>(window, "_state") == IslandState.Bar,
                    "关闭窗口联动后仍遵从原来的自动收起设置");
                SettingsRegistry.Write(options, "MediaWindowHandoffEnabled", true);
            }
            Reset(); Popup(); Expire();
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar,
                "没有后台活动的普通通知按时收回");
        }
        finally
        {
            AgentStatus.Reset(owner: owner);
            PrivateCall(window, "FinishDesktopPopup");
            SetAgentField(window, "_smtc", oldMedia);
            layers.HideAll();
        }
    }
}