using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    // Explicit opt-in: sends real Windows Toasts, never run by the offline regression suite.
    private static void RealWindowsNotificationChecks()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                string title = "Alpha Island live check " + Guid.NewGuid().ToString("N")[..8];
                using var service = new SystemToastService(Dispatcher.CurrentDispatcher);
                var received = new System.Collections.Generic.HashSet<string>();
                DesktopNotification? captured = null;
                bool nativeRemoved=false;
                var frame = new DispatcherFrame();
                service.Received += (items, initial) =>
                {
                    foreach (var item in items)
                        if (!initial && item.Title.StartsWith(title, StringComparison.Ordinal)) { received.Add(item.Key); captured = item;nativeRemoved=service.SuppressPopup(item); }
                    if (received.Count >= 2) frame.Continue = false;
                };
                service.Start();
                Check(service.Allowed, "本机普通 EXE 获得 Windows 实际通知读取权限");
                // Let the first API read finish before sending new notifications.
                PumpAnimations(2000);
                var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
                foreach (string argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                    Path.GetFullPath("tools/Test-WindowsNotification.ps1"), "-ImagePath", Path.GetFullPath("Assets/alpha-island-256.png"), "-Title", title, "-Count", "2", "-IntervalSeconds", "1" })
                    start.ArgumentList.Add(argument);
                using var sender = Process.Start(start)!;
                var deadline = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
                deadline.Tick += (_, _) => { deadline.Stop(); frame.Continue = false; };
                deadline.Start(); Dispatcher.PushFrame(frame); deadline.Stop();
                Check(received.Count == 2, "测试脚本发出的两条真实 Windows Toast 均由正式监听服务捕获，无重复或首轮误判");
                Check(sender.WaitForExit(3000) && sender.ExitCode == 0, "Windows PowerShell 通知发送脚本正常退出");
                Check(captured?.ContentImage is { Length: > 0 }, "真实 Windows 通知的正文图片通过原始 XML 读取并交给小岛显示");
                Check(captured?.Logo is { Length: > 0 }, "真实 Windows 通知携带通知中心提供的应用图标");
                var remaining=Windows.UI.Notifications.Management.UserNotificationListener.Current.GetNotificationsAsync(Windows.UI.Notifications.NotificationKinds.Toast).AsTask();
                var checkDeadline=DateTime.UtcNow.AddSeconds(4);while(!remaining.IsCompleted&&DateTime.UtcNow<checkDeadline)PumpAnimations(20);
                Check(nativeRemoved&&remaining.IsCompleted&&!remaining.GetAwaiter().GetResult().Any(n=>n.Id==captured!.WindowsId),"正式 Windows 接口成功撤下岛已接管的原系统通知");
                var pill = new alpha_island.Controls.DesktopNotificationPill();
                pill.SetNotification(captured!);
                Check(pill.IconImage.Source is System.Windows.Media.Imaging.BitmapSource { IsFrozen: true }
                    && pill.IconImage.Visibility == System.Windows.Visibility.Collapsed && pill.Icon.Visibility == System.Windows.Visibility.Visible
                    && ReferenceEquals(pill.Icon.Source, pill.IconImage.Source),
                    "真实系统图标可解码并显示在通知小岛，替代固定图标");
                Console.WriteLine($"All {_checks} real Windows notification checks passed.");
            }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void NotificationStartupUiChecks(alpha_island.MainWindow window, Grid root)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        var suppressed = new System.Collections.Generic.List<uint>();
        SetAgentField(window, "_suppressSystemPopup", new Action<DesktopNotification>(item => suppressed.Add(item.WindowsId!.Value)));
        var replacement = new DesktopNotification("replacement-test", "test.app", "测试应用", "通知替换", "本次测试", DateTimeOffset.UtcNow, WindowsId: 41001);
        PrivateCall(window, "OnSystemToastsReceived", new[] { replacement }, false);
        Check(suppressed.SequenceEqual(new uint[] { 41001 }), "岛接管新通知后屏蔽原系统横幅，避免双重弹窗");
        PrivateCall(window, "OnSystemToastsReceived", new[] { replacement }, false);
        PrivateCall(window, "OnSystemToastsReceived", new[] { replacement with { Key="baseline",WindowsId=41002 } }, true);
        SettingsRegistry.Write(options,"NotificationQuietMode",true);
        PrivateCall(window,"OnSystemToastsReceived",new[]{replacement with { Key="quiet",WindowsId=41003 }},false);
        SettingsRegistry.Write(options,"NotificationQuietMode",false);
        SettingsRegistry.Write(options,"NotificationReplaceSystemPopup",false);
        PrivateCall(window,"OnSystemToastsReceived",new[]{replacement with { Key="keep-native",WindowsId=41004 }},false);
        Check(suppressed.SequenceEqual(new uint[]{41001}), "不撤下重复、启动基线、静默或未启用接管的系统通知");
        SettingsRegistry.Write(options,"NotificationReplaceSystemPopup",true);
        SetAgentField(window, "_suppressSystemPopup", null);
        PrivateCall(window, "ClearDesktopInbox");
        PrivateCall(window, "SetState", IslandState.Bar, true);
        var pending = new BootTimingReading(null, null, "", "test", Waiting: true);
        PrivateCall(window, "PresentBootTiming", pending);
        var pill = AgentField<alpha_island.Controls.DesktopNotificationPill>(window, "_notificationSummary");
        var inbox = AgentField<DesktopNotificationInbox>(window, "_desktopInbox");
        Check(pill.Title.Text == "本次开机" && pill.Detail.Text.Contains("正在等待")
            && AgentField<DesktopNotification>(window, "_desktopCurrent").Kind == DesktopEventKind.BootTiming,
            "自启动在等待日志时立即显示提示，不等待 Event 100 才响应");
        Check(AgentField<IslandTimer>(window, "_notificationUiTimer").IsEnabled,
            "关闭其他通知监听时，开机提示仍有独立计时器消费队列");
        var exact = new BootTimingReading(DateTimeOffset.UtcNow.AddMinutes(-2), TimeSpan.FromMilliseconds(12345), "", "test");
        PrivateCall(window, "PresentBootTiming", exact);
        Check(pill.Detail.Text.Contains("12.3 秒") && inbox.History.Count == 1 && inbox.PendingCount == 0
            && AgentField<DateTimeOffset>(window, "_desktopExpires") > DateTimeOffset.Now.AddSeconds(14),
            "日志及时到达时原位更新等待提示，保留一条记录且展示 15 秒");
        SetAgentField(window, "_desktopExpires", DateTimeOffset.Now.AddSeconds(-1));
        PrivateCall(window, "TickDesktopNotifications");
        Check(AgentField<DesktopNotification?>(window, "_desktopCurrent") == null,
            "等待提示到期能正常收起，不无限占据主岛");
        PrivateCall(window, "PresentBootTiming", exact with { DesktopDuration = TimeSpan.FromSeconds(39.3) });
        Check(pill.Detail.Text.Contains("39.3 秒") && AgentField<DesktopNotification?>(window, "_desktopCurrent") != null
            && inbox.History.Count == 1, "日志晚到时重新提示最终精确耗时，不被相同通知键去重吞掉");
        PrivateCall(window, "ShowPillNotification", IslandNotification.Seconds("agentstatus", 3));
        Check(AgentField<string>(window, "_activeMainIslandKey") == "desktoptoast",
            "新的助手活动不会提前抢走仍在展示的开机结果");
        PrivateCall(window, "ShowPillNotification", IslandNotification.Seconds("approval", 3));
        Check(AgentField<string>(window, "_activeMainIslandKey") == "approval",
            "真正的工具审批仍可打断开机提示");
        AgentField<IslandLayerHost>(window, "_layers").Hide("approval");
        PrivateCall(window, "SwitchMainIsland", "desktoptoast");
        PumpAnimations(400); root.UpdateLayout();
        SaveThemePreview(window.IslandBody, "bin/launch-previews/boot-time-delayed.png");
        PrivateCall(window, "ClearDesktopInbox");
        SettingsRegistry.Write(options, "NotificationQuietMode", true);
        SettingsRegistry.Write(options, "NotificationQuietAllowPriority", false);
        PrivateCall(window, "PresentBootTiming", exact);
        Check(AgentField<DesktopNotification?>(window, "_desktopCurrent") == null && inbox.History.Count == 1,
            "完全静默仍保留开机记录，尊重用户的禁止弹出选择");
        SettingsRegistry.Write(options, "NotificationQuietMode", false);
        PrivateCall(window, "ClearDesktopInbox");
        CompactDesktopNotificationChecks(window, root);
        PinnedNotificationSequenceChecks(window, root);
        DesktopNotificationIconChecks(window);
        using var service = new SystemToastService(Dispatcher.CurrentDispatcher);
        service.Start();
        Check(!service.Status.Contains("MSIX"), "EXE 按 Windows API 权限决定监听，不再提前退出");
    }

    private static void CompactDesktopNotificationChecks(alpha_island.MainWindow window, Grid root)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        var pages = AgentField<IslandPageHost>(window, "_pages");
        var pill = AgentField<alpha_island.Controls.DesktopNotificationPill>(window, "_notificationSummary");
        var inbox = AgentField<DesktopNotificationInbox>(window, "_desktopInbox");
        layers.HideAll();
        PrivateCall(window, "SetState", IslandState.Bar, true);
        var message = new DesktopNotification("compact-message", "Contoso.Chat!App", "聊天", "项目更新",
            "这里是完整消息正文，点击小岛后可查看。", DateTimeOffset.Now);
        PrivateCall(window, "OnSystemToastsReceived", new[] { message with { Key = "compact-history" } }, true);
        Check(AgentField<DesktopNotification?>(window, "_desktopCurrent") == null
            && AgentField<IslandState>(window, "_state") == IslandState.Bar, "首轮历史通知不会打开小岛");
        PrivateCall(window, "OnSystemToastsReceived", new[] { message }, false);
        PumpAnimations(400); root.UpdateLayout();
        Check(AgentField<IslandState>(window, "_state") == IslandState.Pill
            && Math.Abs(window.IslandBody.ActualHeight - IslandMetrics.PillHeight) < .5
            && pill.Title.Text == message.AppName && pill.Detail.Text == message.Title,
            "新消息使用 48 高度的小岛展示应用和标题，不自动展开完整卡片");
        SaveThemePreview(window.IslandBody, "bin/launch-previews/windows-notification-pill.png");
        bool twoStepExpand = options.TwoStepExpand;
        options.TwoStepExpand = false;
        PrivateCall(window, "IslandBody_MouseEnter", window.IslandBody,
            new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0));
        options.TwoStepExpand = twoStepExpand;
        Check(AgentField<IslandState>(window, "_state") == IslandState.Pill, "即使关闭两步展开，移入通知小岛也不会自动放大");
        SetAgentField(window, "_desktopExpires", DateTimeOffset.Now.AddSeconds(-1));
        PrivateCall(window, "TickDesktopNotifications");
        Check(AgentField<IslandState>(window, "_state") == IslandState.Bar
            && AgentField<string>(window, "_activeMainIslandKey") == "music", "小岛提示结束恢复待机细条并清理消息主岛键");

        PrivateCall(window, "SetLayerPinned", "netspeed", true);
        PrivateCall(window, "SetState", IslandState.Pill, true);
        PrivateCall(window, "OnSystemToastsReceived", new[] { message with { Key = "compact-pinned" } }, false);
        SetAgentField(window, "_desktopExpires", DateTimeOffset.Now.AddSeconds(-1));
        PrivateCall(window, "TickDesktopNotifications");
        Check(AgentField<IslandState>(window, "_state") == IslandState.Pill && layers.IsPinned("netspeed")
            && AgentField<string>(window, "_activeMainIslandKey") == "netspeed", "通知结束恢复之前置顶的小岛，保留置顶状态");
        PrivateCall(window, "SetLayerPinned", "netspeed", false);
        PrivateCall(window, "OnSystemToastsReceived", new[] { message with { Key = "compact-open" } }, false);
        PrivateCall(window, "IslandBody_MouseLeftButtonDown", window.IslandBody,
            new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left));
        root.UpdateLayout();
        var stack = AgentField<StackPanel>(window, "_notificationList").Children.OfType<alpha_island.Controls.DesktopNotificationStackCard>()
            .Single(group => group.Group.Items.Any(item => item.Key == "compact-open"));
        Check(AgentField<IslandState>(window, "_state") == IslandState.Card && pages.ActiveId == IslandPageIds.Notifications
            && !stack.IsExpanded, "点击通知小岛进入通知中心并显示收起的真实卡片堆");
        stack.ToggleButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        var card = stack.Cards.Single(item => item.Notification.Key == "compact-open");
        Check(card.TitleText.Text == message.Title && card.BodyText.Text == message.Body,
            "展开卡片堆后可查看原始消息正文与操作按钮");
        SetAgentField(window, "_desktopExpires", DateTimeOffset.Now.AddSeconds(-1));
        PrivateCall(window, "TickDesktopNotifications");
        Check(AgentField<IslandState>(window, "_state") == IslandState.Card && pages.ActiveId == IslandPageIds.Notifications,
            "计时结束不会强行关闭用户正在查看的通知中心");
        PrivateCall(window, "OnSystemToastsReceived", new[] { message with { Key = "compact-queued" } }, false);
        Check(AgentField<DesktopNotification?>(window, "_desktopCurrent") == null && inbox.PendingCount == 1
            && AgentField<IslandState>(window, "_state") == IslandState.Card, "阅读通知时后续消息排队，不打断卡片");
        PrivateCall(window, "SetState", IslandState.Pill);
        PrivateCall(window, "TickDesktopNotifications");
        Check(AgentField<DesktopNotification?>(window, "_desktopCurrent")?.Key == "compact-queued"
            && AgentField<IslandState>(window, "_state") == IslandState.Pill, "离开完整卡片后排队消息仍以小岛提示");
        PrivateCall(window, "ClearDesktopInbox");
    }

    private static void PinnedNotificationSequenceChecks(alpha_island.MainWindow window, Grid root)
    {
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        using var media = new SmtcService(window.Dispatcher);
        var previousMedia = AgentField<SmtcService?>(window, "_smtc");
        bool previousReduceMotion = alpha_island.Animation.MotionPolicy.ReduceMotion;
        bool previousPowerSave = alpha_island.Animation.MotionPolicy.PowerSave;
        alpha_island.Animation.MotionPolicy.Apply(false, false);
        SetAgentField(window, "_smtc", media);
        media.SetDebugNowPlaying(new NowPlayingInfo { Title = "通知顶置回归", SourceApp = "cloudmusic.exe", State = PlaybackState.Playing });
        try
        {
            foreach (string[] pins in new[] { new[] { "netspeed" }, new[] { "card/focus" }, new[] { "music" }, new[] { "music", "netspeed", "card/focus" } })
            {
                PrivateCall(window, "ClearDesktopInbox"); layers.HideAll();
                PrivateCall(window, "SetMusicPinned", false);
                PrivateCall(window, "SetState", IslandState.Bar, true);
                PrivateCall(window, "EnsureCardPinLayer", "focus");
                foreach (string key in pins)
                    PrivateCall(window, key == "music" ? "SetMusicPinned" : "SetLayerPinned", key == "music" ? new object?[] { true } : new object?[] { key, true });
                PumpAnimations(400); root.UpdateLayout();
                string returning = AgentField<string>(window, "_activeMainIslandKey");
                var messages = Enumerable.Range(0, 2).Select(i => new DesktopNotification(
                    "pin-sequence-" + string.Join('-', pins) + i, "Microsoft.Windows.PowerShell", "Windows PowerShell", "测试通知 " + i, "", DateTimeOffset.Now)).ToArray();
                PrivateCall(window, "OnSystemToastsReceived", messages, false);
                Check(AgentField<DesktopNotification?>(window, "_desktopCurrent")?.Key == messages[1].Key
                    && AgentField<alpha_island.ViewModels.IslandWorkspaceViewModel>(window, "_workspace").Notifications.Burst.Count == 2
                    && AgentField<DesktopNotificationInbox>(window, "_desktopInbox").PendingCount == 0,
                    "连续消息在当前小岛堆叠为两条并显示最新消息，不重复轮播");
                SetAgentField(window, "_desktopExpires", DateTimeOffset.Now.AddSeconds(-1));
                PrivateCall(window, "TickDesktopNotifications");
                PumpAnimations(400); root.UpdateLayout();
                for (int i = 0; i < 10; i++) PrivateCall(window, "HoverTimer_Tick", null, EventArgs.Empty);
                PumpAnimations(400); root.UpdateLayout();
                Check(pins.All(key => key == "music" ? AgentField<bool>(window, "_isPinned") : layers.IsPinned(key) && layers.IsActive(key))
                    && AgentField<IslandState>(window, "_state") == IslandState.Pill
                    && AgentField<string>(window, "_activeMainIslandKey") == returning,
                    "两条通知结束并完成悬停维护后保留顶置岛 " + string.Join(',', pins));
                Check(Math.Abs(window.IslandBody.ActualHeight - IslandMetrics.PillHeight) < 1,
                    "连续通知收回后实际岛体保持胶囊高度 " + string.Join(',', pins));
                if (returning == "music")
                    Check(window.PillLayer.IsVisible && window.PillLayer.Opacity > .9,
                        "连续通知收回后音乐内容实际恢复可见，不只保留逻辑顶置");
            }
        }
        finally
        {
            PrivateCall(window, "ClearDesktopInbox"); layers.HideAll(); PrivateCall(window, "SetMusicPinned", false);
            SetAgentField(window, "_smtc", previousMedia);
            alpha_island.Animation.MotionPolicy.Apply(previousReduceMotion, previousPowerSave);
        }
    }

    private static void DesktopNotificationIconChecks(alpha_island.MainWindow window)
    {
        var pill = AgentField<alpha_island.Controls.DesktopNotificationPill>(window, "_notificationSummary");
        var message = new DesktopNotification("logo-message", "Contoso.Chat!App", "聊天", "图标测试", "", DateTimeOffset.Now,
            Logo: File.ReadAllBytes("Assets/alpha-island-256.png"));
        pill.SetNotification(message);
        Check(pill.IconImage.Source is System.Windows.Media.Imaging.BitmapSource { IsFrozen: true }
            && pill.IconImage.Visibility == System.Windows.Visibility.Collapsed && pill.Icon.Visibility == System.Windows.Visibility.Visible
            && ReferenceEquals(pill.Icon.Source, pill.IconImage.Source),
            "有应用图标的通知小岛显示图像，隐藏固定字形");
        var orb = (System.Windows.Controls.Border)PrivateCall(window, "EnsureSatelliteOrb", "desktoptoast")!;
        PrivateCall(window, "UpdateSatelliteOrbVisual", "desktoptoast", orb);
        var content = (Grid)orb.Child;
        var orbLogo = content.Children.OfType<System.Windows.Controls.Image>().First(i => i.Name == "OrbAppLogo");
        var glyph = content.Children.OfType<alpha_island.Controls.SemanticIcon>().First(i => i.Name == "OrbGlyph");
        Check(ReferenceEquals(orbLogo.Source, pill.IconImage.Source) && orbLogo.Visibility == System.Windows.Visibility.Collapsed
            && glyph.Visibility == System.Windows.Visibility.Visible && ReferenceEquals(glyph.Source, orbLogo.Source), "左侧通知小圆岛复用相同应用图标，不重复解码");
        pill.SetNotification(message with { Logo = null, AppId = "Contoso.Other!App" });
        PrivateCall(window, "UpdateSatelliteOrbVisual", "desktoptoast", orb);
        Check(pill.IconImage.Source == null && orbLogo.Source == null && pill.Icon.Visibility == System.Windows.Visibility.Visible
            && glyph.Visibility == System.Windows.Visibility.Visible, "切换到没有图标的应用时清除旧图标，主岛与小圆岛均正常回退");
        pill.SetNotification(message with { Logo = new byte[] { 1, 2, 3, 4 } });
        Check(pill.IconImage.Source != null && pill.Icon.Visibility == System.Windows.Visibility.Visible,
            "损坏的通知图标复用相同应用的已验证 Logo，不导致岛崩溃或空白");
        pill.SetNotification(message with { Kind = DesktopEventKind.BluetoothRadio, RadioEnabled = true });
        Check(pill.IconImage.Source == null && pill.Icon.Text == "\uE702" && pill.Title.Text == "蓝牙",
            "系统状态事件继续使用对应图标，不沿用前一个应用图标");
    }
}
