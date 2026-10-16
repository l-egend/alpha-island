using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Animation;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;
using Windows.Devices.Radios;

internal static partial class Program
{
    private static void SemanticIconEntryUiChecks(alpha_island.MainWindow window, Grid root)
    {
        bool reduced = MotionPolicy.ReduceMotion, saved = MotionPolicy.PowerSave;
        int tier = MotionPolicy.RenderTier;
        var options = AgentField<IslandOptions>(window, "_options");
        var extras = new Dictionary<string, JsonElement>(options.Extras);
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        var pages = AgentField<IslandPageHost>(window, "_pages");
        var oldState = AgentField<IslandState>(window, "_state"); string oldPage = pages.ActiveId;
        var oldSmtc = AgentField<SmtcService?>(window, "_smtc");
        var oldVolume = AgentField<VolumeService?>(window, "_volumeService");
        bool oldLyrics = options.LyricsEnabled, oldPause = options.PauseNotifyEnabled,
            oldPlay = options.PlayNotifyEnabled, oldNext = options.UpNextEnabled;
        bool oldAirplane = AgentField<bool>(window, "_airplaneOn"), oldAirplaneKnown = AgentField<bool>(window, "_airplaneKnown"),
            oldBarAirVisible = AgentField<bool>(window, "_barAirVisible");
        var transitions = AgentField<WirelessRadioTransitions>(window, "_radioTransitions");
        var previousRadios = AgentField<Dictionary<RadioKind, bool?>>(transitions, "_previous");
        var savedRadios = new Dictionary<RadioKind, bool?>(previousRadios);
        var summary = AgentField<DesktopNotificationPill>(window, "_notificationSummary");
        using var smtc = new SmtcService(window.Dispatcher);
        using var volume = new VolumeService(window.Dispatcher);
        var media = new NowPlayingInfo { Title = "语义图标离线检查", SourceApp = "semantic-icon-test.exe",
            SessionId = "semantic-icon-entry", State = PlaybackState.Playing, Duration = TimeSpan.FromMinutes(3) };
        void Apply(NowPlayingInfo info) { smtc.SetDebugNowPlaying(info); PrivateCall(window, "ApplyMedia", info); root.UpdateLayout(); }
        void ShowPage(string id)
        {
            layers.HideAll(); PrivateCall(window, "SetState", IslandState.Card, true, id); root.UpdateLayout(); PumpAnimations(200);
        }
        void Radios(RadioState wifi, RadioState bluetooth) => PrivateCall(window, "ApplyWirelessRadioStates",
            new[] { (RadioKind.WiFi, wifi), (RadioKind.Bluetooth, bluetooth) });
        void ClearRadioNotice()
        {
            PrivateCall(window, "ClearDesktopInbox"); layers.HideAll(); PrivateCall(window, "SetState", IslandState.Pill, true);
        }
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(false, false);
            options.LyricsEnabled = options.PauseNotifyEnabled = options.PlayNotifyEnabled = options.UpNextEnabled = false;
            SettingsRegistry.Write(options, "MediaHideWhenPaused", false);
            SettingsRegistry.Write(options, "MediaHideWhenForeground", false);
            SettingsRegistry.Write(options, "NotificationQuietMode", false);
            SettingsRegistry.Write(options, "NotificationWifiRadio", true);
            SettingsRegistry.Write(options, "NotificationBluetoothRadio", true);
            SetAgentField(window, "_smtc", smtc); SetAgentField(window, "_volumeService", volume);
            PrivateCall(window, "ClearDesktopInbox");

            Apply(media); ShowPage(IslandPageIds.Music);
            Check(window.PlayPauseGlyph.Shape == IconShape.PlayPause && window.PlayPauseGlyph.Active,
                "真实播放快照把媒体按钮接到播放/暂停内部形变，并设为播放状态");
            ActionIconMotion.Stop(window.PlayPauseGlyph);
            Apply(media with { State = PlaybackState.Paused }); PumpAnimations(50);
            Check(!window.PlayPauseGlyph.Active && window.PlayPauseGlyph.Text == "\uE768"
                && ActionIconMotion.GetIsAnimating(window.PlayPauseGlyph), "暂停快照改变内部播放目标并触发有限过渡");
            ActionIconMotion.Stop(window.PlayPauseGlyph);
            Apply(media with { State = PlaybackState.Paused, Position = TimeSpan.FromSeconds(12) });
            Check(!window.PlayPauseGlyph.Active && !ActionIconMotion.GetIsAnimating(window.PlayPauseGlyph),
                "暂停后的时间轴刷新不会重播语义过渡");
            Apply(media); PumpAnimations(50);
            Check(window.PlayPauseGlyph.Active && ActionIconMotion.GetIsAnimating(window.PlayPauseGlyph), "恢复播放使内部形状回到准确播放状态");

            volume.SetVolume(60); ShowPage(IslandPageIds.Volume); PrivateCall(window, "SyncVolumeCardContent");
            Check(window.CardVolumeIcon.Shape == IconShape.Volume && window.CardVolumeIcon.Active
                && Math.Abs(window.CardVolumeIcon.Level - .6) < .0001,
                "音量卡的内部声波使用实际百分比，60% 对应 0.6 级别");
            Check(window.VolumeIcon.Shape == IconShape.Volume && window.VolumeIcon.Active && Math.Abs(window.VolumeIcon.Level - .6) < .0001,
                "音量胶囊与音量卡共享准确的声波状态");
            volume.ToggleMute(); PrivateCall(window, "SyncVolumeCardContent"); PumpAnimations(50);
            Check(!window.CardVolumeIcon.Active && !window.VolumeIcon.Active
                && Math.Abs(window.CardVolumeIcon.Level - .6) < .0001,
                "静音使用内部关闭状态，同时保留真实系统音量百分比");
            ActionIconMotion.Stop(window.CardVolumeIcon); ActionIconMotion.Stop(window.VolumeIcon);
            PrivateCall(window, "SyncVolumeCardContent");
            Check(!ActionIconMotion.GetIsAnimating(window.CardVolumeIcon) && !ActionIconMotion.GetIsAnimating(window.VolumeIcon),
                "相同音量和静音快照不会重播内部声波动画");
            volume.SetVolume(0); PrivateCall(window, "SyncVolumeCardContent");
            Check(!window.CardVolumeIcon.Active && window.CardVolumeIcon.Level == 0 && window.VolumeIcon.Level == 0,
                "零音量把内部声波目标归零");

            layers.HideAll(); PrivateCall(window, "SetState", IslandState.Pill, true);
            var battery = new BatteryStatusInfo(76, true, BatteryChargeType.PD, BatteryPowerEvent.PluggedIn, null);
            PrivateCall(window, "ShowBatteryNotify", battery); PumpAnimations(100);
            Check(window.BatteryIcon.Shape == IconShape.Battery && window.BatteryIcon.Active
                && Math.Abs(window.BatteryIcon.Level - .76) < .0001,
                "充电提示将电池内部填充设为实际 76%，并启用充电目标");
            PrivateCall(window, "ShowBatteryNotify", battery with { IsCharging = false, EventType = BatteryPowerEvent.Unplugged });
            Check(!window.BatteryIcon.Active && Math.Abs(window.BatteryIcon.Level - .76) < .0001,
                "拔电源关闭电池内部充电目标，保留真实电量");
            ActionIconMotion.Stop(window.BatteryIcon); PrivateCall(window, "SyncBatteryNotifyContent");
            Check(!ActionIconMotion.GetIsAnimating(window.BatteryIcon), "电池提示普通刷新不重播内部形变");
            PrivateCall(window, "ShowBatteryNotify", battery with { Percent = -1, IsCharging = false, EventType = BatteryPowerEvent.LowBattery });
            Check(window.BatteryIcon.Level == 0 && window.BatteryPercentText.Text == "未知", "未知电量保持未知文字，不虚构内部满格电池");

            ClearRadioNotice(); transitions.Reset(); Radios(RadioState.On, RadioState.On);
            Radios(RadioState.Off, RadioState.On); PumpAnimations(100);
            Check(AgentField<DesktopNotification?>(window, "_desktopCurrent")?.RadioEnabled == false
                && summary.Icon.Shape == IconShape.Wifi && !summary.Icon.Active,
                "真实无线状态枚举驱动 Wi-Fi 关闭形态，不解析通知标题文字");
            int historyCount = AgentField<DesktopNotificationInbox>(window, "_desktopInbox").History.Count;
            ActionIconMotion.Stop(summary.IconContent); Radios(RadioState.Off, RadioState.On);
            Check(!ActionIconMotion.GetIsAnimating(summary.IconContent)
                && AgentField<DesktopNotificationInbox>(window, "_desktopInbox").History.Count == historyCount,
                "重复无线采样既不生成新事件，也不重播内部无线描画");
            ClearRadioNotice(); Radios(RadioState.Off, RadioState.Off); PumpAnimations(100);
            Check(summary.Icon.Shape == IconShape.Bluetooth && !summary.Icon.Active,
                "真实蓝牙关闭状态选择蓝牙内部断开目标");
            ClearRadioNotice(); Radios(RadioState.On, RadioState.Off); PumpAnimations(100);
            Check(summary.Icon.Shape == IconShape.Wifi && summary.Icon.Active, "真实 Wi-Fi 开启状态选择无线内部启用目标");
            ClearRadioNotice(); Radios(RadioState.On, RadioState.On); PumpAnimations(100);
            Check(summary.Icon.Shape == IconShape.Bluetooth && summary.Icon.Active, "真实蓝牙开启状态选择内部启用目标");

            ClearRadioNotice(); SettingsRegistry.Write(options, "AirplaneNotifyEnabled", true);
            SetAgentField(window, "_airplaneKnown", true); SetAgentField(window, "_airplaneOn", false);
            PrivateCall(window, "ApplyAirplaneState", true);
            Check(window.SysNotifyGlyph.Active, "飞行模式开启由明确状态参数传给通用提示图标");
            PrivateCall(window, "ApplyAirplaneState", false);
            Check(!window.SysNotifyGlyph.Active, "飞行模式关闭通过状态参数更新内部目标，不猜测开关文案");
        }
        finally
        {
            foreach (var icon in new FrameworkElement[] { window.PlayPauseGlyph, window.CardVolumeIcon,
                window.VolumeIcon, window.SysNotifyGlyph, window.BatteryIcon, summary.IconContent }) ActionIconMotion.Stop(icon);
            PrivateCall(window, "DismissUpNext"); layers.HideAll(); PrivateCall(window, "ClearDesktopInbox");
            previousRadios.Clear(); foreach (var pair in savedRadios) previousRadios.Add(pair.Key, pair.Value);
            SetAgentField(window, "_airplaneOn", oldAirplane); SetAgentField(window, "_airplaneKnown", oldAirplaneKnown);
            SetAgentField(window, "_barAirVisible", oldBarAirVisible);
            SetAgentField(window, "_smtc", oldSmtc); SetAgentField(window, "_volumeService", oldVolume);
            if (oldSmtc?.Current is { } original) PrivateCall(window, "ApplyMedia", original);
            else PrivateCall(window, "ApplyNoMedia");
            options.LyricsEnabled = oldLyrics; options.PauseNotifyEnabled = oldPause;
            options.PlayNotifyEnabled = oldPlay; options.UpNextEnabled = oldNext;
            options.Extras = extras;
            PrivateCall(window, "SetState", oldState, true, oldPage); PrivateCall(window, "SyncBarIndicators");
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduced, saved);
        }
    }
}
