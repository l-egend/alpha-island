using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Services;
using Windows.Media;
using Point = System.Windows.Point;
using Image = System.Windows.Controls.Image;
using Size = System.Windows.Size;

internal static partial class Program
{
#if DEBUG
    private const string TestBuildConfiguration = "Debug";
#else
    private const string TestBuildConfiguration = "Release";
#endif

    private static void V41FeatureChecks(alpha_island.MainWindow window, Grid root)
    {
        var xml = "<toast><visual><binding template='ToastGeneric'><text>OPPO Enco Clip2</text>"
            + "<image placement='appLogoOverride' src='data:image/png;base64,AQ=='/>"
            + "<group><subgroup><text>Bluetooth device battery is fully charged.</text>"
            + "<text>Additional complete content.</text></subgroup></group></binding></visual></toast>";
        var payload = NotificationPayloadReader.Parse(xml)!;
        Check(payload.Text.Length == 3 && payload.Text[^1] == "Additional complete content." && !payload.Hero,
            "通知正文包含分组内所有文本，正文图标与应用标识独立解析");
        Check(NotificationPayloadReader.Parse("<!DOCTYPE toast [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]><toast>&x;</toast>") == null,
            "通知解析禁止外部实体，不读取 XML 指定的外部文件");
        Check(NotificationPayloadReader.DecodePayload(Encoding.Unicode.GetBytes(xml)) == xml
            && NotificationPayloadReader.DecodePayload(Encoding.UTF8.GetBytes(xml)) == xml,
            "Windows 通知数据库 UTF-8 / UTF-16 内容均可只读解析");
        Check(NotificationPayloadReader.ReadImage("https://example.invalid/tracker.png", "Test") == null
            && NotificationPayloadReader.ReadImage("file://server/share/image.png", "Test") == null,
            "通知图片不发起远程或网络共享读取，无法读取时安全回退");
        byte[] bodyIcon = TestNotificationImage(System.Windows.Media.Colors.OrangeRed);
        byte[] appIcon = TestNotificationImage(System.Windows.Media.Colors.Cyan);
        var message = new DesktopNotification("v41-image", "AlphaIsland.Test", "Python", "Game Exited",
            "Full notification content\n" + new string('文', 900) + "正文末尾", DateTimeOffset.UtcNow,
            WindowsId: 98765, Logo: appIcon, ContentImage: bodyIcon);
        var pill = new alpha_island.Controls.DesktopNotificationPill(); pill.SetNotification(message);
        Check(ReferenceEquals(pill.Icon.Source, alpha_island.Controls.DesktopNotificationIcons.GetContentImage(message)),
            "小岛优先显示原通知的正文图标，应用图标仍用于通知记录的应用栏");
        var card = new alpha_island.Controls.DesktopNotificationCard(message);
        card.SetFullContentExpanded(true);
        card.Measure(new Size(375, double.PositiveInfinity)); card.Arrange(new Rect(new Point(), card.DesiredSize)); card.UpdateLayout();
        Check(FullContentDescendants(card).OfType<Image>().Count(i => i.Source != null) == 2
            && FullContentDescendants(card).OfType<System.Windows.Controls.TextBox>().Single().Text.EndsWith("正文末尾"),
            "卡片保留应用图标与正文配图，全文可滚动读取到原始末尾");
        SaveNotificationContentPreview(card, "v41-notification-full-image.png");
        PrivateCall(window, "ClearDesktopInbox");
        PrivateCall(window, "SetState", IslandState.Pill, true);
        PrivateCall(window, "OnSystemToastsReceived", new[] { message }, false);
        PrivateCall(window, "OpenCardFromPill"); root.UpdateLayout();
        var list = AgentField<StackPanel>(window, "_notificationList");
        var currentCard = list.Children.OfType<alpha_island.Controls.DesktopNotificationCard>().First();
        Check(currentCard.FullContentExpanded,
            "点击当前通知直接进入完整正文，不再次隐藏在摘要后");
        var notificationView = AgentField<Grid>(window, "_notificationsView");
        double actionBottom = currentCard.OpenButton!.TranslatePoint(new Point(0, currentCard.OpenButton.ActualHeight), notificationView).Y;
        Check(actionBottom <= notificationView.ActualHeight,
            $"当前通知的全文和正文图标不会把打开应用按钮挤出首屏（bottom={actionBottom:0.00}, height={notificationView.ActualHeight:0.00}）");
        SetAgentField(window, "_componentCardAwaitingPointer", false);
        SetAgentField(window, "_hoverOutTicks", 50);
        PrivateCall(window, "HoverTimer_Tick", null, EventArgs.Empty);
        Check(AgentField<IslandState>(window, "_state") != IslandState.Card,
            "通知仍在展示时，离开卡片仍可缩回小岛，游戏吞掉点击不会无限停留");
        PrivateCall(window, "ClearDesktopInbox");

        var bluetooth = new BluetoothLowBatteryPolicy();
        var device = new BluetoothDeviceInfo("v41-bt", "OPPO Enco Clip2", BluetoothDeviceCategory.Audio, 20, true);
        Check(bluetooth.ShouldNotify(device) && !bluetooth.ShouldNotify(device with { BatteryPercent = 19 })
            && bluetooth.ShouldNotify(device with { BatteryPercent = 10 }) && !bluetooth.ShouldNotify(device with { BatteryPercent = 9 }),
            "蓝牙 20% 和 10% 各提醒一次，连续轮询不重复弹窗");
        Check(!bluetooth.ShouldNotify(device with { BatteryPercent = null })
            && !bluetooth.ShouldNotify(device with { BatteryPercent = 5, IsConnected = false }) && bluetooth.ShouldNotify(device),
            "未知电量与断联旧读数不提醒，重新连接允许低电量提醒");
        bluetooth.ShouldNotify(device with { BatteryPercent = 26 });
        Check(bluetooth.ShouldNotify(device), "充电超过 25% 后恢复下一轮低电量提醒");
        PrivateCall(window, "OnBluetoothBatteryUpdated", null, device with { Id = "v41-live-bt", BatteryPercent = 8 });
        Check(AgentField<DesktopNotificationInbox>(window, "_desktopInbox").History.Any(x => x.Kind == DesktopEventKind.BluetoothLowBattery
            && x.Title.Contains(device.Name) && x.Body.Contains("8%")), "蓝牙电量事件实际进入小岛通知中心");
        PrivateCall(window, "ClearDesktopInbox");

        var parts = AlphaIsland.Plugins.OppoPods.OppoPodsPlugin.BatteryReports(new AlphaIsland.Plugins.OppoPods.EarbudSnapshot
        {
            DeviceName = "private-protocol-fixture", Connected = true,
            LeftPower = new(8), RightPower = new(60, true), CasePower = new(null)
        });
        Check(parts.Length == 3 && parts[0].Connected && parts[0].Percent == 8
            && parts[1].Charging && !parts[2].Connected,
            "私有耳机插件向宿主报告分路电量，未知或离线的盒电量不触发提醒");
        var music = new NowPlayingInfo { Title = "Fixture", ContentKind = MediaContentKind.Music,
            SourceApp = "cloudmusic.exe", SessionId = "v41", State = PlaybackState.Playing,
            Position = TimeSpan.FromSeconds(10), Duration = TimeSpan.FromMinutes(3), CanShuffle = true, CanRepeat = true };
        Check(MediaPlaybackOrders.Supported(music).Length == 3
            && MediaPlaybackOrders.Next(music) == MediaPlaybackOrder.Shuffle
            && MediaPlaybackOrders.Next(music with { PlaybackOrder = MediaPlaybackOrder.Shuffle }) == MediaPlaybackOrder.RepeatTrack
            && MediaPlaybackOrders.Next(music with { PlaybackOrder = MediaPlaybackOrder.RepeatTrack }) == MediaPlaybackOrder.Sequential,
            "支持的音乐播放器按顺序、随机、单曲循环切换");
        Check(MediaPlaybackOrders.Supported(music with { ContentKind = MediaContentKind.Video }).Length == 0
            && MediaPlaybackOrders.Supported(music with { CanShuffle = false, CanRepeat = false }).Length == 0,
            "视频和未提供控制能力的播放器不显示模式按钮");
        var requests = new List<string>();
        bool changed = MediaPlaybackOrders.ChangeAsync(music, MediaPlaybackOrder.RepeatTrack,
            value => { requests.Add("shuffle:" + value); return Task.FromResult(true); },
            value => { requests.Add("repeat:" + value); return Task.FromResult(true); }, () => true).GetAwaiter().GetResult();
        Check(changed && requests.SequenceEqual(new[] { "shuffle:False", "repeat:Track" }),
            "切入单曲循环先关闭随机，不把相互冲突的模式同时启用");
        requests.Clear();
        changed = MediaPlaybackOrders.ChangeAsync(music, MediaPlaybackOrder.Shuffle,
            value => { requests.Add("shuffle"); return Task.FromResult(true); },
            value => { requests.Add("repeat"); return Task.FromResult(false); }, () => true).GetAwaiter().GetResult();
        Check(!changed && requests.SequenceEqual(new[] { "repeat" }), "播放器拒绝第一步时不继续发送请求或假装切换成功");
        requests.Clear();
        changed = MediaPlaybackOrders.ChangeAsync(music with { CanShuffle = false }, MediaPlaybackOrder.Shuffle,
            value => { requests.Add("shuffle"); return Task.FromResult(true); },
            value => { requests.Add("repeat"); return Task.FromResult(true); }, () => true).GetAwaiter().GetResult();
        Check(!changed && requests.Count == 0, "不支持的模式不会发送控制命令");
        PrivateCall(window, "ApplyMedia", music);
        Check(window.PlaybackOrderButton.Visibility == Visibility.Visible, "媒体卡片实际显示支持的播放模式按钮");
        PrivateCall(window, "ApplyMedia", music with { CanShuffle = false, CanRepeat = false });
        Check(window.PlaybackOrderButton.Visibility == Visibility.Collapsed, "切换到不支持的媒体后模式按钮消失");

        using var smtc = new SmtcService(Dispatcher.CurrentDispatcher);
        SetAgentField(window, "_smtc", smtc);
        smtc.ObserveTransportState(PlaybackState.Playing, true); smtc.UpdateTrack(music, "v41-lyrics", null);
        var lyrics = LrcParser.Parse("[00:00.00]第一行\n[00:05.00]第二行\n[00:20.00]第三行", null);
        PrivateCall(window, "ApplyLyrics", lyrics);
        PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.Music); root.UpdateLayout();
        PrivateCall(window, "AnimateLyricChange", true); PumpAnimations(40);
        smtc.ObserveTransportState(PlaybackState.Paused, true);
        smtc.UpdateTrack(music with { State = PlaybackState.Paused, Position = TimeSpan.FromSeconds(10) }, "v41-lyrics", null);
        PrivateCall(window, "ApplyMedia", smtc.Current!);
        int pausedIndex = AgentField<int>(window, "_lyricIndex");
        PrivateCall(window, "OnKaraokeTick", null, EventArgs.Empty);
        Check(!AgentField<bool>(window, "_karaokeHooked") && !window.LyricCurrentHost.HasAnimatedProperties
            && ((TranslateTransform)window.LyricCurrentHost.RenderTransform).Y == 0 && pausedIndex == AgentField<int>(window, "_lyricIndex"),
            "暂停取消逐帧歌词及未完成的行过渡，排队到达的刷新也不会继续换句");
        Check(LyricsService.CacheKey("Title", "Artist", TimeSpan.FromSeconds(180), "Studio")
            != LyricsService.CacheKey("Title", "Artist", TimeSpan.FromSeconds(180), "Live"), "不同专辑版本不共用歌词缓存");
        Check(LyricsService.ScoreCandidate("Title", "Artist", TimeSpan.FromSeconds(180), "Title", "Artist", 260000) < 42,
            "时长相差过大的同名同歌手版本也不会显示错误歌词");
        Check(LyricsService.ScoreCandidate("Title A", "Artist", TimeSpan.FromSeconds(180), "Completely unrelated song", "Artist", 180000) < 42
            && LyricsService.ScoreCandidate("爱", "Artist", TimeSpan.FromSeconds(180), "爱如潮水", "Artist", 180000) < 42,
            "同歌手同长度不能抵消曲名不匹配，单字子串不会误认另一首歌");
        using var coarse = new SmtcService(Dispatcher.CurrentDispatcher) { AudioProbeEnabled = true };
        coarse.ObserveTransportState(PlaybackState.Playing, true);
        coarse.UpdateTrack(music with { SessionId = "coarse-timeline-fixture" }, "coarse", null);
        SetAgentField(coarse, "_transportEventUntilMs", 0L);
        coarse.ReportAudioEvidence(0, 0, false, 3, true);
        Check(coarse.Current?.State == PlaybackState.Paused, "低频进度复现前置：确认旧静音已经锁存暂停");
        var oldStamp = DateTimeOffset.UtcNow.AddSeconds(-5);
        coarse.ObserveTimelineEvidence(TimeSpan.FromSeconds(10), oldStamp);
        coarse.ObserveTimelineEvidence(TimeSpan.FromSeconds(10), oldStamp);
        coarse.ObserveTimelineEvidence(TimeSpan.FromSeconds(10), oldStamp);
        coarse.ObserveTimelineEvidence(TimeSpan.FromSeconds(15), oldStamp.AddSeconds(5));
        PrivateCall(coarse, "PublishResolvedState");
        Check(coarse.Current?.IsPlaying == true,
            "播放器五秒才更新一次原始进度时，重复轮询不能丢失连续播放证据或一直误报暂停");
        SetAgentField(window, "_smtc", null);
        PrivateCall(window, "SetState", IslandState.Bar, true);
    }

    private static byte[] TestNotificationImage(System.Windows.Media.Color color)
    {
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen()) dc.DrawRoundedRectangle(new SolidColorBrush(color), null, new Rect(0, 0, 48, 48), 9, 9);
        var bitmap = new RenderTargetBitmap(48, 48, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var memory = new MemoryStream(); encoder.Save(memory); return memory.ToArray();
    }
}
