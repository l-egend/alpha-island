using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Animation;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void IconActionEntryUiChecks(alpha_island.MainWindow window, Grid root)
    {
        bool reduced = MotionPolicy.ReduceMotion, saved = MotionPolicy.PowerSave;
        int tier = MotionPolicy.RenderTier;
        var options = AgentField<IslandOptions>(window, "_options");
        var extras = new Dictionary<string, JsonElement>(options.Extras);
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        var pages = AgentField<IslandPageHost>(window, "_pages");
        var oldState = AgentField<IslandState>(window, "_state");
        string oldPage = pages.ActiveId;
        var oldSmtc = AgentField<SmtcService?>(window, "_smtc");
        var oldVolume = AgentField<VolumeService?>(window, "_volumeService");
        bool oldLyrics = options.LyricsEnabled, oldPause = options.PauseNotifyEnabled,
            oldPlay = options.PlayNotifyEnabled, oldNext = options.UpNextEnabled;
        using var smtc = new SmtcService(window.Dispatcher);
        using var volume = new VolumeService(window.Dispatcher);
        var media = new NowPlayingInfo { Title = "图标动作离线检查", SourceApp = "icon-motion-test.exe",
            SessionId = "icon-action-entries", State = PlaybackState.Playing, Duration = TimeSpan.FromMinutes(3) };
        void Apply(NowPlayingInfo info)
        {
            smtc.SetDebugNowPlaying(info); PrivateCall(window, "ApplyMedia", info); root.UpdateLayout();
        }
        void ShowPage(string id)
        {
            layers.HideAll(); PrivateCall(window, "SetState", IslandState.Card, true, id);
            root.UpdateLayout(); PumpAnimations(250);
        }
        void ExpectStopped(FrameworkElement icon, string name)
        {
            PumpAnimations(1850); Check(!ActionIconMotion.GetIsAnimating(icon), name);
        }
        try
        {
            // Services remain unstarted: the fixture neither controls system audio nor makes lyric/API requests.
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            options.LyricsEnabled = options.PauseNotifyEnabled = options.PlayNotifyEnabled = options.UpNextEnabled = false;
            SettingsRegistry.Write(options, "MediaHideWhenPaused", false);
            SettingsRegistry.Write(options, "MediaHideWhenForeground", false);
            SettingsRegistry.Write(options, "NotificationQuietMode", false);
            SetAgentField(window, "_smtc", smtc); SetAgentField(window, "_volumeService", volume);
            PrivateCall(window, "ClearDesktopInbox");
            Apply(media); ShowPage(IslandPageIds.Music); ActionIconMotion.Stop(window.PlayPauseGlyph);
            Apply(media with { State = PlaybackState.Paused }); PumpAnimations(80);
            Check(window.PlayPauseGlyph.IsVisible && window.PlayPauseGlyph.Text == "\uE768"
                && ActionIconMotion.GetIsAnimating(window.PlayPauseGlyph),
                "音乐卡持续可见时，真实暂停状态变化触发一次播放按钮动画");
            var pausedMotion = window.PlayPauseGlyph.RenderTransform;
            Apply(media with { State = PlaybackState.Paused, Position = TimeSpan.FromSeconds(8) });
            Check(ReferenceEquals(pausedMotion, window.PlayPauseGlyph.RenderTransform),
                "相同暂停状态的时间轴更新不重启动画");
            ExpectStopped(window.PlayPauseGlyph, "音乐按钮动画有限结束，不留下持续渲染时钟");
            Apply(media with { State = PlaybackState.Paused });
            Check(!ActionIconMotion.GetIsAnimating(window.PlayPauseGlyph), "重复媒体快照不重播已结束的按钮动画");
            Apply(media); PumpAnimations(80);
            Check(window.PlayPauseGlyph.Text == "\uE769" && ActionIconMotion.GetIsAnimating(window.PlayPauseGlyph),
                "真实恢复播放也会触发按钮动作，不依赖卡片重新打开");

            volume.SetVolume(22); ShowPage(IslandPageIds.Volume); PrivateCall(window, "SyncVolumeCardContent");
            ActionIconMotion.Stop(window.CardVolumeIcon);
            volume.SetVolume(25); PrivateCall(window, "SyncVolumeCardContent"); PumpAnimations(80);
            Check(window.CardVolumeIcon.IsVisible && window.CardVolumePercentText.Text == "25%"
                && ActionIconMotion.GetIsAnimating(window.CardVolumeIcon),
                "同一音量档位内的真实音量变化，也会触发可见音量卡图标动画");
            var volumeMotion = window.CardVolumeIcon.RenderTransform;
            PrivateCall(window, "SyncVolumeCardContent");
            Check(ReferenceEquals(volumeMotion, window.CardVolumeIcon.RenderTransform), "重复音量卡同步不重启动画");
            ExpectStopped(window.CardVolumeIcon, "音量卡图标短动画结束后释放时钟");
            PrivateCall(window, "SyncVolumeCardContent");
            Check(!ActionIconMotion.GetIsAnimating(window.CardVolumeIcon), "相同音量读数刷新保持静态");

            layers.HideAll(); PrivateCall(window, "SetState", IslandState.Pill, true);
            PrivateCall(window, "ShowVolumeNotify", 25, false); PumpAnimations(250);
            ActionIconMotion.Stop(window.VolumeIcon);
            volume.SetVolume(28); PrivateCall(window, "ShowVolumeNotify", 28, false); PumpAnimations(80);
            Check(window.VolumeIcon.IsVisible && window.VolumeText.Text == "28%"
                && ActionIconMotion.GetIsAnimating(window.VolumeIcon), "音量胶囊保持展开时，第二次真实调节仍触发动作提示");
            var pillMotion = window.VolumeIcon.RenderTransform;
            PrivateCall(window, "SyncVolumeContent"); PrivateCall(window, "SyncVolumeCardContent");
            Check(ReferenceEquals(pillMotion, window.VolumeIcon.RenderTransform), "音量提示显隐和读数同步不重复播放图标动画");

            layers.HideAll(); PrivateCall(window, "ShowSystemNotify", "\uE701", "Wi-Fi 已开启", "图标离线检查", "", 10.0);
            PumpAnimations(250); ActionIconMotion.Stop(window.SysNotifyGlyph);
            PrivateCall(window, "ShowSystemNotify", "\uE701", "Wi-Fi 已关闭", "图标离线检查", "", 10.0); PumpAnimations(80);
            Check(window.SysNotifyGlyph.IsVisible && window.SysNotifyTitle.Text == "Wi-Fi 已关闭"
                && ActionIconMotion.GetIsAnimating(window.SysNotifyGlyph), "系统状态层持续可见时，下一次状态通知仍触发图标动画");
            var systemMotion = window.SysNotifyGlyph.RenderTransform;
            PrivateCall(window, "SyncNotificationLayers");
            Check(ReferenceEquals(systemMotion, window.SysNotifyGlyph.RenderTransform), "普通系统提示图层同步不重启动画");

            layers.HideAll();
            var battery = new BatteryStatusInfo(76, true, BatteryChargeType.StandardAC, BatteryPowerEvent.PluggedIn, null);
            PrivateCall(window, "ShowBatteryNotify", battery); PumpAnimations(250); ActionIconMotion.Stop(window.BatteryIcon);
            PrivateCall(window, "ShowBatteryNotify", battery with { IsCharging = false, EventType = BatteryPowerEvent.Unplugged });
            PumpAnimations(80);
            Check(window.BatteryIcon.IsVisible && window.BatteryStatusText.Text.Contains("已拔出电源")
                && ActionIconMotion.GetIsAnimating(window.BatteryIcon), "电池层持续可见时，拔出电源会重新触发电池图标动作");
            var batteryMotion = window.BatteryIcon.RenderTransform;
            PrivateCall(window, "SyncBatteryNotifyContent");
            Check(ReferenceEquals(batteryMotion, window.BatteryIcon.RenderTransform), "重复电池提示同步不重启动画");
            ExpectStopped(window.BatteryIcon, "电池状态动作有限结束，普通刷新不保留动画时钟");
            PrivateCall(window, "SyncBatteryNotifyContent");
            Check(!ActionIconMotion.GetIsAnimating(window.BatteryIcon), "电池提示保持展开也不会循环播放");
        }
        finally
        {
            foreach (var icon in new FrameworkElement[] { window.PlayPauseGlyph, window.CardVolumeIcon,
                window.VolumeIcon, window.SysNotifyGlyph, window.BatteryIcon }) ActionIconMotion.Stop(icon);
            PrivateCall(window, "DismissUpNext"); layers.HideAll(); PrivateCall(window, "ClearDesktopInbox");
            SetAgentField(window, "_smtc", oldSmtc); SetAgentField(window, "_volumeService", oldVolume);
            if (oldSmtc?.Current is { } original) PrivateCall(window, "ApplyMedia", original);
            else PrivateCall(window, "ApplyNoMedia");
            options.LyricsEnabled = oldLyrics; options.PauseNotifyEnabled = oldPause;
            options.PlayNotifyEnabled = oldPlay; options.UpNextEnabled = oldNext;
            options.Extras = extras;
            PrivateCall(window, "SetState", oldState, true, oldPage);
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduced, saved);
        }
    }
}
