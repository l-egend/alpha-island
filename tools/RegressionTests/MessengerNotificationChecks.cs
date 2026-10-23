using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Controls;
using alpha_island.Interop;

internal static partial class Program
{
    private static DesktopNotification MessengerTestNotice(MessengerClient client, string key, string title = "收到新消息", string body = "点击打开查看") =>
        new(key, MessengerIdentity.AppId(client), MessengerIdentity.Name(client), title, body, DateTimeOffset.UtcNow);
    private static void MessengerNotificationStateChecks()
    {
        Check(MessengerIdentity.FromProcess("QQ") == MessengerClient.QQ && MessengerIdentity.FromProcess("Weixin") == MessengerClient.WeChat
            && MessengerIdentity.FromProcess("WeChat") == MessengerClient.WeChat && MessengerIdentity.FromProcess("QQMusic") == null,
            "识别 QQ、微信新旧进程名并排除 QQ 音乐");
        foreach (var (text, count) in new[] { ("微信(12)", 12), ("QQ：5条未读消息", 5), ("WeChat: 3 new messages", 3), ("微信", 0), ("QQ ", 0) })
            Check(MessengerIdentity.UnreadCount(text) == count, "读取消息计数：" + text);
        Check(MessengerIdentity.UnreadCount("QQ，有 1 个正在运行的窗口") == null
            && MessengerIdentity.FromTrayName("QQ音乐") == null && MessengerIdentity.FromTrayName("某应用：微信") == null,
            "不把任务栏窗口数量或其他应用提示当作未读消息");
        var counts = new MessengerUnreadTracker();
        Check(!counts.Observe(MessengerClient.QQ, 18, true) && !counts.Observe(MessengerClient.QQ, 18, false), "启动只建立基线，不补弹旧未读消息");
        Check(counts.Observe(MessengerClient.QQ, 19, false) && !counts.Observe(MessengerClient.QQ, 19, false), "只有新增未读消息触发一次提醒");
        Check(!counts.Observe(MessengerClient.QQ, 3, false) && !counts.Observe(MessengerClient.QQ, 0, false)
            && counts.Observe(MessengerClient.QQ, 1, false), "读取消息导致数量下降不会弹窗，清零后下一条仍提醒");
        counts.Forget(MessengerClient.QQ);
        Check(!counts.Observe(MessengerClient.QQ, 60, false) && !counts.Observe(MessengerClient.WeChat, 7, false), "进程重启和另一客户端分别建立基线");
        var now = DateTimeOffset.UtcNow;
        var dedup = new MessengerNotificationDeduplicator();
        var fallback = MessengerTestNotice(MessengerClient.QQ, "fallback");
        var detailed = MessengerTestNotice(MessengerClient.QQ, "windows", "测试联系人", "测试新消息正文") with { WindowsId = 44001 };
        Check(dedup.Resolve(fallback, MessengerNoticeSource.Unread, now)?.Key == "fallback", "只读到新消息信号时允许通用提醒");
        var enriched = dedup.Resolve(detailed, MessengerNoticeSource.Windows, now.AddSeconds(1));
        Check(enriched?.Key == "fallback" && enriched.Body == detailed.Body && enriched.WindowsId == detailed.WindowsId,
            "系统正文原位补全已有提醒，保留 Windows 通知标识");
        Check(dedup.Resolve(fallback with { Key = "tray" }, MessengerNoticeSource.Attention, now.AddSeconds(2)) == null, "Windows 通知之后到达的托盘闪烁不重复弹出");
        Check(dedup.Resolve(detailed with { Key = "another" }, MessengerNoticeSource.Windows, now.AddSeconds(2))?.Key == "another", "同一接收渠道的两条相同消息仍分别保留");
        Check(dedup.Resolve(MessengerTestNotice(MessengerClient.WeChat, "wechat"), MessengerNoticeSource.Attention, now.AddSeconds(2))?.Key == "wechat", "QQ 与微信不会相互去重");
        dedup.Clear();
        var popup = detailed with { Key = "client-popup", WindowsId = null };
        dedup.Resolve(popup, MessengerNoticeSource.Popup, now);
        Check(dedup.Resolve(detailed, MessengerNoticeSource.Windows, now.AddSeconds(1))?.Key == popup.Key, "客户端弹窗与 Windows 横幅合为一条记录");
        var repeatedPopup = popup with { Key = "second-identical-popup" };
        Check(dedup.Resolve(repeatedPopup, MessengerNoticeSource.Popup, now.AddSeconds(2))?.Key == repeatedPopup.Key
            && dedup.Resolve(detailed with { Key = "second-identical-windows" }, MessengerNoticeSource.Windows, now.AddSeconds(3))?.Key == repeatedPopup.Key,
            "两次相同内容的消息各自配对，不因两个接收渠道反复合并而丢消息");
        Check(dedup.Resolve(fallback, MessengerNoticeSource.Attention, now.AddSeconds(10)) != null, "过期的上一条消息不会吞掉后续提醒");
        var blink = new MessengerTrayBlinkTracker();
        byte[] a = Enumerable.Repeat((byte)12, 64).ToArray(), b = Enumerable.Repeat((byte)99, 64).ToArray();
        Check(!blink.Observe("tray-1", a, now) && !blink.Observe("tray-1", b, now.AddMilliseconds(350)), "单次图标或主题变化不认为有新消息");
        Check(!blink.Observe("tray-1", a, now.AddMilliseconds(700)) && !blink.Observe("tray-1", b, now.AddMilliseconds(1050))
            && blink.Observe("tray-1", a, now.AddMilliseconds(1400)), "连续往返闪烁后才产生消息提醒");
        Check(!blink.Observe("tray-1", b, now.AddMilliseconds(1750)) && !blink.Observe("tray-1", a, now.AddMilliseconds(2100)), "未读图标持续闪烁只提醒一次");
        Check(!blink.Observe("tray-2", a, now.AddMilliseconds(2450)) && !blink.Observe("tray-2", b, now.AddSeconds(7)), "图标移动或监测中断后清除旧闪烁样本");
        blink.Suspend();
        bool repeated = false;
        for (int index = 0; index < 6; index++) repeated |= blink.Observe("tray-2", index % 2 == 0 ? a : b, now.AddSeconds(10).AddMilliseconds(index * 350));
        Check(!repeated, "托盘被遮挡、鼠标经过或位置移动后，不重弹原来的未读提醒");
        for (int index = 0; index < 18; index++) blink.Observe("tray-2", a, now.AddSeconds(12).AddMilliseconds(index * 350));
        bool nextBlink = false;
        for (int index = 0; index < 6; index++) nextBlink |= blink.Observe("tray-2", index % 2 == 0 ? b : a, now.AddSeconds(19).AddMilliseconds(index * 350));
        Check(nextBlink, "观察到图标恢复稳定后，下一次新闪烁仍能提醒");
    }
    private static void MessengerNotificationUiChecks(alpha_island.MainWindow window, Grid root)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        var inbox = AgentField<DesktopNotificationInbox>(window, "_desktopInbox");
        var dedup = AgentField<MessengerNotificationDeduplicator>(window, "_messengerNoticeDedup");
        void Reset() { PrivateCall(window, "ClearDesktopInbox"); dedup.Clear(); PrivateCall(window, "SetState", IslandState.Bar, true); }
        Reset();
        var fallback = MessengerTestNotice(MessengerClient.WeChat, "ui-fallback");
        PrivateCall(window, "OnMessengerNotificationsReceived", new[] { fallback }, MessengerNoticeSource.Unread);
        PumpAnimations(100); root.UpdateLayout();
        var summary = AgentField<DesktopNotificationPill>(window, "_notificationSummary");
        Check(AgentField<DesktopNotification?>(window, "_desktopCurrent")?.Key == fallback.Key && summary.Title.Text == "微信"
            && summary.Detail.Text == "收到新消息", "微信新消息通过正常小岛提示，无需 Windows 通知读取权限");
        var expires = AgentField<DateTimeOffset>(window, "_desktopExpires");
        var detail = fallback with { Key = "ui-system", Title = "测试联系人", Body = "完整测试正文", WindowsId = 44501 };
        var removed = new List<uint>();
        SetAgentField(window, "_suppressSystemPopup", new Action<DesktopNotification>(item => removed.Add(item.WindowsId!.Value)));
        PrivateCall(window, "OnSystemToastsReceived", new[] { detail }, false);
        Check(inbox.History.Count == 1 && inbox.PendingCount == 0 && summary.Detail.Text == "测试联系人"
            && AgentField<DesktopNotification>(window, "_desktopCurrent").Body == detail.Body
            && AgentField<DateTimeOffset>(window, "_desktopExpires") == expires && removed.SequenceEqual(new uint[] { 44501 }),
            "正文补全不重新展开、不延长提示，并撤下已接管的原生横幅");
        SetAgentField(window, "_suppressSystemPopup", null);
        Reset(); SettingsRegistry.Write(options, "NotificationQuietMode", true);
        PrivateCall(window, "OnMessengerNotificationsReceived", new[] { fallback }, MessengerNoticeSource.Popup);
        Check(AgentField<DesktopNotification?>(window, "_desktopCurrent") == null && inbox.History.Count == 1, "静默时只记录 QQ/微信消息");
        SettingsRegistry.Write(options, "NotificationQuietMode", false); Reset();
        SetAgentField(window, "_sessionLocked", true);
        PrivateCall(window, "OnMessengerNotificationsReceived", new[] { fallback }, MessengerNoticeSource.Unread);
        Check(AgentField<DesktopNotification?>(window, "_desktopCurrent") == null && inbox.History.Count == 1, "锁屏时不弹出聊天消息内容");
        SetAgentField(window, "_sessionLocked", false); Reset();
        SettingsRegistry.Write(options, "NotificationBlockedApps", "微信");
        PrivateCall(window, "OnMessengerNotificationsReceived", new[] { fallback }, MessengerNoticeSource.Popup);
        Check(inbox.History.Count == 0, "客户端通知沿用应用屏蔽规则");
        SettingsRegistry.Write(options, "NotificationBlockedApps", ""); SettingsRegistry.Write(options, "NotificationMessengerEnabled", false);
        var qq = MessengerTestNotice(MessengerClient.QQ, "qq-disabled");
        PrivateCall(window, "OnMessengerNotificationsReceived", new[] { qq }, MessengerNoticeSource.Unread);
        PrivateCall(window, "OnSystemToastsReceived", new[] { qq with { Key = "qq-disabled-system" } }, false);
        Check(inbox.History.Count == 0, "关闭消息提醒同时过滤 QQ 的 Windows 通知");
        PrivateCall(window, "OnMessengerNotificationsReceived", new[] { fallback }, MessengerNoticeSource.Unread);
        Check(inbox.History.Count == 0, "同一个消息开关也过滤微信通知");
        SettingsRegistry.Write(options, "NotificationMessengerEnabled", true);
        PrivateCall(window, "OnMessengerNotificationsReceived", new[] { fallback }, MessengerNoticeSource.Unread);
        Check(inbox.History.Count == 1 && fallback.CanOpenApp, "重新开启消息提醒立即恢复微信及应用入口");
        Reset();
        QQNotificationPresentationChecks(window, root);
        MessengerNotificationOpeningUiChecks(window);
        NativeMessengerPopupReadChecks();
        NativeMessengerTaskbarChecks();
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MessengerFlashInfo { public uint Size; public IntPtr Window; public uint Flags, Count, Timeout; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref MessengerFlashInfo info);
    private static void NativeMessengerTaskbarChecks()
    {
        var target = new Window { Title = "QQ", Width = 900, Height = 650, ShowActivated = false };
        alpha_island.Services.MessengerNotificationService? service = null;
        try
        {
            target.Show();
            using var process = Process.GetCurrentProcess();
            var reader = new MessengerWindowReader(() => new[] { new MessengerProcess(MessengerClient.QQ, process.Id, process.MainModule!.FileName!, process.StartTime.Ticks) });
            service = new(System.Windows.Threading.Dispatcher.CurrentDispatcher, reader);
            var notices = new List<DesktopNotification>();
            service.Received += (items, _) => notices.AddRange(items);
            service.SetEnabled(true, false); service.Start();
            PumpAnimations(9300);
            Check(notices.Count == 0 && service.Status.Contains("QQ：监听中"), "启动真实监听仅建立基线，不把已有窗口当作新消息");
            var flash = new MessengerFlashInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MessengerFlashInfo>(),
                Window = new System.Windows.Interop.WindowInteropHelper(target).Handle, Flags = 3, Count = 3, Timeout = 350 };
            FlashWindowEx(ref flash);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (notices.Count == 0 && DateTime.UtcNow < deadline) PumpAnimations(40);
            Check(notices.Count == 1 && notices[0].AppId == MessengerIdentity.AppId(MessengerClient.QQ)
                && notices[0].Title == "收到新消息", "真实 Windows 任务栏闪烁事件触发 QQ 提醒");
            FlashWindowEx(ref flash); PumpAnimations(1300);
            Check(notices.Count == 1, "连续任务栏闪烁不会反复生成提醒");
            flash.Flags = 0; FlashWindowEx(ref flash);
            service.Dispose();
            Check(!AgentField<IslandTimer>(service, "_timer").IsEnabled,
                "停止提醒后卸载监听并停止采样计时器");
        }
        finally { service?.Dispose(); target.Close(); }
    }
    private static void NativeMessengerPopupReadChecks()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "测试联系人" }); panel.Children.Add(new TextBlock { Text = "测试通知正文" });
        var popup = new Window { Title = "新消息", Content = panel, Width = 350, Height = 140, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false, ShowActivated = false };
        var main = new Window { Title = "QQ", Content = new System.Windows.Controls.TextBox { Text = "不可作为通知读取的聊天内容" }, Width = 900, Height = 650, ShowInTaskbar = false, ShowActivated = false };
        var unreadable = new Window { Title = "新消息", Content = new Border(), Width = 350, Height = 140, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false, ShowActivated = false };
        var menu = new Menu(); menu.Items.Add(new MenuItem { Header = "测试菜单" });
        var menuWindow = new Window { Title = "新消息", Content = menu, Width = 350, Height = 140, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            popup.Show(); main.Show(); unreadable.Show(); menuWindow.Show();
            using var process = Process.GetCurrentProcess();
            var reader = new MessengerWindowReader(() => new[] { new MessengerProcess(MessengerClient.QQ, process.Id, process.MainModule!.FileName!, process.StartTime.Ticks) });
            var read = Task.Run(() => reader.Read(new HashSet<MessengerClient> { MessengerClient.QQ }));
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!read.IsCompleted && DateTime.UtcNow < deadline) PumpAnimations(40);
            Check(read.IsCompleted, "原生通知表面读取在后台完成，不阻塞 WPF");
            var snapshot = read.GetAwaiter().GetResult();
            Check(snapshot.Popups.Count == 2 && snapshot.Popups.Any(p => p.Title == "测试联系人" && p.Body == "测试通知正文" && p.HasContent),
                "真实 UI Automation 提取通知正文，跳过普通聊天窗口");
            Check(snapshot.Popups.Any(p => p.Window == new System.Windows.Interop.WindowInteropHelper(unreadable).Handle && !p.HasContent && p.Title == "收到新消息"),
                "客户端弹窗不提供可读取文字时，仍生成新消息信号");
            Check(snapshot.Popups.All(p => p.Window != new System.Windows.Interop.WindowInteropHelper(menuWindow).Handle),
                "不把客户端菜单当作新消息通知");
        }
        finally { popup.Close(); main.Close(); unreadable.Close(); menuWindow.Close(); }
    }
}
