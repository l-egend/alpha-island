using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using alpha_island.Core;
using alpha_island.Services;
using Button = System.Windows.Controls.Button;

internal static partial class Program
{
    private static T AgentField<T>(object window, string name)
        => (T)ReadWindowMember(window, name)!;
    private static void SetAgentField(object window, string name, object? value)
        => WriteWindowMember(window, name, value);

    private static void AgentApprovalInteractionChecks(alpha_island.MainWindow window, Grid root)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        var savedExtras = new Dictionary<string, JsonElement>(options.Extras);
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start();
        int port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
        SettingsRegistry.Write(options, "AgentBridgePort", port);
        SettingsRegistry.Write(options, "AgentApprovalNotifyEnabled", true);
        // Foreground visibility has its own native-window fixture; this checks approvals and status HTTP.
        SettingsRegistry.Write(options, "MediaWindowHandoffEnabled", false);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(20) };
        Task<HttpResponseMessage> Submit(string id) => http.PostAsync("approval", new StringContent(
            JsonSerializer.Serialize(new { id, tool = "connection_probe", args = new { operation = "none" }, reason = "隔离交互测试，不执行操作", timeoutSec = 15 }), Encoding.UTF8, "application/json"));
        void Until(Func<bool> condition)
        {
            var timer = Stopwatch.StartNew();
            while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(4)) { PumpAnimations(10); root.UpdateLayout(); }
            if (!condition()) throw new Exception("Approval interaction condition timed out");
        }
        MouseButtonEventArgs MouseArgs(RoutedEvent routed) => new(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = routed };
        void Down(UIElement target, bool preview = true)
        {
            if (preview) target.RaiseEvent(MouseArgs(System.Windows.Input.Mouse.PreviewMouseDownEvent));
            target.RaiseEvent(MouseArgs(System.Windows.Input.Mouse.MouseDownEvent));
        }
        JsonDocument Result(Task<HttpResponseMessage> task)
        {
            Until(() => task.IsCompleted);
            using var response = task.GetAwaiter().GetResult();
            return JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        }
        try
        {
            PrivateCall(window, "StartAgentBridge");
            layers.HideAll(); SetAgentField(window, "_clipboardLocked", false);
            PrivateCall(window, "SetState", IslandState.Card, false, IslandPageIds.System);
            var allowTask = Submit("interaction-allow");
            Until(() => AgentField<ApprovalRequest?>(window, "_approvalCurrent")?.Id == "interaction-allow");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill && layers.LastWinnerKey == "approval",
                "实际审批请求到达即从卡片进入审批岛，不依赖悬停计时器");
            PumpAnimations(30); root.UpdateLayout();
            var allow = (FrameworkElement)(window.FindName("ApprovalAllowButton") ?? window.FindName("ApprovalAllowBorder")!);
            Down(allow is Button allowControl && allowControl.Content is UIElement allowContent ? allowContent : allow);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill && window.ApprovalLayer.IsHitTestVisible,
                "审批按钮鼠标按下不会先把审批岛切入隐藏按钮的卡片");
            Check(!AgentField<bool>(window, "_isSwiping"), "审批按钮不触发岛体收缩手势");
            var point = allow.TranslatePoint(new System.Windows.Point(allow.ActualWidth / 2, allow.ActualHeight / 2), root);
            var hit = root.InputHitTest(point) as DependencyObject;
            Check((bool)typeof(alpha_island.MainWindow).GetMethod("IsDescendantOf", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object?[] { hit, allow })!, "允许按钮中心真实命中审批控件");
            if (allow is Button button) ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            else allow.RaiseEvent(MouseArgs(System.Windows.Input.Mouse.MouseUpEvent));
            using var granted = Result(allowTask);
            Check(granted.RootElement.GetProperty("approved").GetBoolean() && granted.RootElement.GetProperty("decidedBy").GetString() == "mouse",
                "允许按钮实际 Click 经桥接回传允许");

            SetAgentField(window, "_clipboardLocked", true);
            layers.ShowSeconds("clipboard", 60); PrivateCall(window, "SetState", IslandState.Card, false, IslandPageIds.Clipboard);
            var denyTask = Submit("interaction-deny");
            Until(() => AgentField<ApprovalRequest?>(window, "_approvalCurrent")?.Id == "interaction-deny");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill, "剪切板锁定期间新审批仍立即展开");
            PumpAnimations(30); root.UpdateLayout();
            var deny = (FrameworkElement)(window.FindName("ApprovalDenyButton") ?? window.FindName("ApprovalDenyBorder")!);
            Down(deny is Button denyControl && denyControl.Content is UIElement denyContent ? denyContent : deny);
            Check(!window.IslandBody.IsMouseCaptured && !AgentField<bool>(window, "_isSwiping"), "剪切板锁定不截获审批按钮鼠标");
            if (deny is Button denyButton) ((IInvokeProvider)new ButtonAutomationPeer(denyButton).GetPattern(PatternInterface.Invoke)).Invoke();
            else deny.RaiseEvent(MouseArgs(System.Windows.Input.Mouse.MouseUpEvent));
            using var denied = Result(denyTask);
            Check(!denied.RootElement.GetProperty("approved").GetBoolean(), "拒绝按钮实际 Click 经桥接回传拒绝");

            var reopenTask = Submit("interaction-reopen");
            Until(() => AgentField<ApprovalRequest?>(window, "_approvalCurrent")?.Id == "interaction-reopen");
            PrivateCall(window, "SetState", IslandState.Bar);
            Check(AgentField<ApprovalRequest?>(window, "_approvalCurrent")?.Id == "interaction-reopen", "审批缩回后仍保留原请求");
            Down(window.IslandBody);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill && layers.LastWinnerKey == "approval",
                "剪切板锁定时点击细条能重新打开待审批岛");
            PrivateCall(window, "SetState", IslandState.Card, false, IslandPageIds.Ai);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill, "普通选页入口不能隐藏待审批按钮");
            PrivateCall(window, "SetState", IslandState.Bar);
            PrivateCall(window, "IslandBody_MouseEnter", window.IslandBody,
                new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount));
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill, "审批细条悬停恢复不受剪切板锁定影响");
            PrivateCall(window, "SetState", IslandState.Bar);
            SetAgentField(window, "_sideBubbleComponentKey", "approval");
            window.SideBubble.Visibility = Visibility.Visible; root.UpdateLayout();
            ClickCompactIsland(window.SideBubble);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill && layers.LastWinnerKey == "approval",
                "点击审批缩略岛直接恢复审批，不依赖 AI 会话卡片");
            PrivateCall(window, "SetState", IslandState.Bar);
            var queuedTask = Submit("interaction-queued");
            Until(() => AgentField<Queue<ApprovalRequest>>(window, "_approvalQueue").Count == 1);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill && AgentField<ApprovalRequest?>(window, "_approvalCurrent")?.Id == "interaction-reopen",
                "新审批排队时重新提醒当前请求，保留队列顺序");
            SetAgentField(window, "_clipboardLocked", false);
            PrivateCall(window, "OnGlobalButtonDown", window, new alpha_island.Interop.MouseButtonDownEventArgs(0, 0, 0x0201));
            PumpAnimations(25);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill, "点击岛外不隐藏阻塞中的审批");
            PrivateCall(window, "DecideApproval", false, "mouse");
            using var reopenResult = Result(reopenTask);
            Until(() => AgentField<ApprovalRequest?>(window, "_approvalCurrent")?.Id == "interaction-queued");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill && window.ApprovalLayer.IsHitTestVisible, "处理当前审批后下一个请求立即可操作");
            window.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(root)!, Environment.TickCount, System.Windows.Input.Key.Y)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            using var queuedResult = Result(queuedTask);
            Check(queuedResult.RootElement.GetProperty("approved").GetBoolean()
                && queuedResult.RootElement.GetProperty("decidedBy").GetString() == "key", "键盘 Y 的允许结果使用 Harness 可识别的裁决标识");
            Until(() => AgentField<ApprovalRequest?>(window, "_approvalCurrent") == null);

            var escapeTask = Submit("interaction-escape");
            Until(() => AgentField<ApprovalRequest?>(window, "_approvalCurrent")?.Id == "interaction-escape");
            window.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(root)!, Environment.TickCount, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            using var escaped = Result(escapeTask);
            Check(!escaped.RootElement.GetProperty("approved").GetBoolean(), "键盘 Esc 通过相同桥接拒绝当前请求");
            Until(() => AgentField<ApprovalRequest?>(window, "_approvalCurrent") == null);
            async Task ReportStatus(string state, string detail = "Codex · 思考中")
            {
                using var response = await http.PostAsync("status", new StringContent(
                    JsonSerializer.Serialize(new { source = "codex", session = "thinking-ui", state, detail, ttlSec = 30 }), Encoding.UTF8, "application/json"));
                response.EnsureSuccessStatusCode();
            }
            void Status(string state, string detail = "Codex · 思考中")
            {
                var task = ReportStatus(state, detail);
                Until(() => task.IsCompleted); task.GetAwaiter().GetResult();
                PrivateCall(window, "UpdateAgentStatusUi"); root.UpdateLayout();
            }
            layers.HideAll(); Status("idle"); PrivateCall(window, "SetState", IslandState.Bar);
            Status("thinking");
            PumpAnimations(350); root.UpdateLayout();
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill
                && AgentField<string>(window, "_activeMainIslandKey") == "agentstatus"
                && window.AgentNotifyTitle.Text == "正在思考" && layers.IsVisible("agentstatus"),
                "真实 Codex 状态请求从细条主动展开思考通知");
            layers.ShowSeconds("volume", 30); PrivateCall(window, "SwitchMainIsland", "volume");
            Status("thinking", "Codex · 执行工具");
            Check(AgentField<string>(window, "_activeMainIslandKey") == "volume", "思考中的状态续期不抢回用户切换的主岛");
            PrivateCall(window, "SetState", IslandState.Bar); PrivateCall(window, "UpdateAgentStatusUi");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar, "手动收回思考岛后每秒刷新不会重复弹出");
            Status("tool-wait", "Codex · 请在对话中审批");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill
                && AgentField<string>(window, "_activeMainIslandKey") == "agentstatus"
                && window.AgentNotifyTitle.Text == "等待工具授权"
                && window.AgentNotifyDetail.Text == "Codex · 请在对话中审批"
                && AgentField<AgentBridgeService>(window, "_bridge").PendingCount == 0
                && AgentField<ApprovalRequest?>(window, "_approvalCurrent") == null,
                "Codex 原生审批主动展开已收回的思考岛，不创建岛端裁决队列");
            PrivateCall(window, "SetState", IslandState.Bar);
            Status("tool-wait", "Codex · 请在对话中审批");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Bar, "同一次原生审批刷新不重复弹出");
            Status("thinking");
            Status("tool-wait", "Codex · 请在对话中审批");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill, "下一次原生审批重新提醒");
            layers.SetPinned("agentstatus", true);
            Status("idle");
            Check(layers.IsPinned("agentstatus") && layers.IsVisible("agentstatus")
                && window.AgentNotifyTitle.Text == "空闲"
                && window.AgentNotifyDetail.Text == "当前没有运行中的任务"
                && window.BarAgentDot.Visibility == Visibility.Collapsed,
                "置顶 Codex 岛结束后保留置顶并同步为空闲，不能冻结在思考或等待授权");
            Check(window.AgentNotifyValue.Text == "—", "置顶空闲 Agent 岛停止显示上一任务的运行计时");
            Status("thinking");
            Check(layers.IsPinned("agentstatus") && window.AgentNotifyTitle.Text == "正在思考"
                && window.AgentNotifyValue.Text.EndsWith("s"), "置顶 Agent 岛在下次任务开始时恢复实时状态与计时");
            layers.SetPinned("agentstatus", false);
            Status("idle");
            Check(!layers.IsActive("agentstatus") && window.BarAgentDot.Visibility == Visibility.Collapsed, "Codex 结束立即撤下状态与色点");
            layers.HideAll(); Status("thinking");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill, "下次 Codex 开始思考仍会主动提醒");
            Status("idle"); layers.HideAll();
            PrivateCall(window, "OnClipboardContentCopied", null, ("隔离剪切板测试", false));
            Status("thinking");
            Check(AgentField<string>(window, "_activeMainIslandKey") == "clipboard", "思考通知不抢占待粘贴的剪切板");
            Status("tool-wait", "Codex · 请在对话中审批");
            Check(AgentField<string>(window, "_activeMainIslandKey") == "clipboard", "原生审批提醒保留待粘贴剪切板");
            Status("idle"); SetAgentField(window, "_clipboardLocked", false);
            layers.HideAll();
            PrivateCall(window, "SetState", IslandState.Card, false, IslandPageIds.System); Status("thinking");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Card
                && AgentField<string?>(window, "_sideBubbleComponentKey") == "agentstatus"
                && window.SideBubble.Visibility == Visibility.Visible,
                "详情卡片展开时 Codex 思考通过缩略岛提醒并保留当前页面");
            Status("idle");
            SettingsRegistry.Write(options, "AiEnabled", false);
            AgentStatus.Set(AgentRunState.Thinking, "外部 agent", "interaction");
            PrivateCall(window, "UpdateAgentStatusUi");
            PrivateCall(window, "SetState", IslandState.Card, false, IslandPageIds.System);
            SetAgentField(window, "_sideBubbleComponentKey", "agentstatus");
            window.SideBubble.Visibility = Visibility.Visible; root.UpdateLayout();
            ClickCompactIsland(window.SideBubble);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Pill && AgentField<string>(window, "_activeMainIslandKey") == "agentstatus",
                "AI 会话关闭时点击外部 agent 小岛仍能恢复状态详情");
        }
        finally
        {
            PrivateCall(window, "StopAgentBridge"); options.Extras = savedExtras;
            SetAgentField(window, "_clipboardLocked", false);
            AgentStatus.Reset(owner: "interaction");
            AgentStatus.Reset(owner: "approval"); layers.HideAll(); PrivateCall(window, "SetState", IslandState.Bar);
        }
    }
}
