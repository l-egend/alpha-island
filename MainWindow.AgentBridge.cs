using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

// B 组④：agent 工具权限审批（界面侧）。
//
// 分工：
//   · Services\AgentBridgeService.cs —— 回环 HTTP 服务端，收请求、等裁决、回传结果（不碰 WPF）；
//   · 本文件 —— 把请求排成队列弹到一级胶囊上（允许 Y / 拒绝 N），把裁决交回服务，
//     顺手驱动 Core\AgentStatus.cs（WaitingForTool → 归位）与 %LOCALAPPDATA%\alpha-island\ai\approvals.log。
//
// 队列而不是"后来的顶掉前面的"：审批是安全动作，丢一个请求就等于偷偷拒绝/允许了一次工具调用，
// 所以并发的请求排队逐个裁决（界面上永远只显示队首那一个）。
namespace alpha_island
{
    public partial class MainWindow
    {
        private const string ApprovalLayerKey = "approval";

        private AgentBridgeService? _bridge;
        private readonly Queue<ApprovalRequest> _approvalQueue = new();

        /// <summary>当前正在给用户看的那一条（null = 没在等裁决）。图层 ForceActive 直接读它。</summary>
        private ApprovalRequest? _approvalCurrent;

        /// <summary>是否已经挂过键盘 Y/N 处理器。</summary>
        private bool _approvalHooked;

        /// <summary>调试钩子用：自动裁决 allow / deny，延迟秒数。</summary>
        private string _approvalFakeMode = "";
        private double _approvalFakeDelay;

        private DispatcherTimer? _approvalAutoTimer;

        // ================= 服务生命周期（由 IslandServiceHost 调用） =================

        private void StartAgentBridge()
        {
            int port = SettingsRegistry.GetInt(_options, "AgentBridgePort", AgentBridgeService.DefaultPort);
            port = Math.Clamp(port, 1024, 65535);

            // 端口被占用等失败直接抛：宿主会把这条服务标成 Degraded 并在诊断页说明原因。
            var service = new AgentBridgeService(port, OnApprovalRequested);
            try
            {
                service.Start();
            }
            catch
            {
                service.Dispose();
                throw;
            }

            _bridge = service;
            service.Decided += OnApprovalDecided;

            if (!_approvalHooked)
            {
                _approvalHooked = true;
                PreviewKeyDown += MainWindow_ApprovalKeyDown;
            }

            bool ui = SettingsRegistry.GetBool(_options, "AgentApprovalNotifyEnabled", true);
            IslandLog.Debug("AgentBridge", $"桥接已就绪：{service.Endpoint}（审批界面 {(ui ? "开启" : "关闭")}）");
        }

        private void StopAgentBridge()
        {
            var bridge = _bridge;
            _bridge = null;

            bool hadPending = _approvalCurrent != null;
            _approvalQueue.Clear();
            _approvalCurrent = null;
            _approvalAutoTimer?.Stop();
            _approvalAutoTimer = null;

            // 顺序讲究：先让服务把还在等的请求按拒绝回传（网络线程），再收界面。
            // 注意 81-82 行已经先把队首清掉了，所以 Stop 里那些"桥接已关闭"的裁决回调不会再来收一遍界面。
            if (bridge != null)
            {
                try { bridge.Stop(); }
                catch (Exception ex) { IslandLog.Warn("AgentBridge", "桥接停止失败: " + ex.Message); }

                bridge.Decided -= OnApprovalDecided;
            }

            _layers.Hide(ApprovalLayerKey);

            if (_approvalHooked)
            {
                _approvalHooked = false;
                PreviewKeyDown -= MainWindow_ApprovalKeyDown;
            }

            // 只有确实是我们把 agent 状态顶成"等待授权"时才归位，别把 AI 卡片页正在跑的对话状态抹了。
            if (hadPending) AgentStatus.Reset(owner: "approval");

            SyncNotificationLayers();

            if (hadPending)
                IslandLog.Info("AgentBridge", "桥接已停止：未裁决的审批全部按拒绝回传");
        }

        /// <summary>设置页改了开关/端口：关了收尾，端口变了换端口重开。</summary>
        private void OnAgentBridgeOptionsChanged()
        {
            if (_options == null) return;

            bool enabled = SettingsRegistry.GetBool(_options, "AgentBridgeEnabled", false);
            if (!enabled)
            {
                StopAgentBridge();
                return;
            }

            int port = Math.Clamp(SettingsRegistry.GetInt(_options, "AgentBridgePort", AgentBridgeService.DefaultPort), 1024, 65535);
            if (_bridge != null && _bridge.Port == port) return;

            StopAgentBridge();
            StartAgentBridge();
        }

        // ================= 请求 → 界面 =================

        /// <summary>
        /// 服务那边（网络线程）调过来。返回 false = 岛上不接这个请求（审批界面关了），
        /// 服务会立刻按"拒绝"回传给 agent，不让它干等满超时。
        /// </summary>
        private bool OnApprovalRequested(ApprovalRequest request)
        {
            try
            {
                if (Dispatcher.CheckAccess()) return EnqueueApprovalRequest(request);
                return Dispatcher.Invoke(() => EnqueueApprovalRequest(request));
            }
            catch (Exception ex)
            {
                IslandLog.Warn("AgentBridge", "审批请求入队失败: " + ex.Message);
                return false;
            }
        }

        private bool EnqueueApprovalRequest(ApprovalRequest request)
        {
            if (_options == null) return false;
            if (!SettingsRegistry.GetBool(_options, "AgentApprovalNotifyEnabled", true))
            {
                IslandLog.Info("AgentBridge", $"审批 {request.Id}（{request.Tool}）因「审批通知」开关关闭被拒绝");
                return false;
            }

            _approvalQueue.Enqueue(request);
            IslandLog.Info("AgentBridge",
                $"审批请求 {request.Id} tool={request.Tool} timeout={request.TimeoutSec}s args={Shorten(request.Args, 160)}（队列 {_approvalQueue.Count}）");

            if (_approvalCurrent == null) ShowNextApproval();
            else PresentCurrentApproval(); // 新请求排队时也提醒用户，恢复此前收起的审批岛。
            return true;
        }

        private void ShowNextApproval()
        {
            if (_approvalCurrent != null) return;
            if (_approvalQueue.Count == 0) return;

            ApprovalRequest request;
            do
            {
                if (_approvalQueue.Count == 0) return;
                request = _approvalQueue.Dequeue();
            }
            while ((_bridge != null && !_bridge.IsPending(request.Id))
                || DateTime.UtcNow >= request.RequestedUtc.AddSeconds(request.TimeoutSec));
            _approvalCurrent = request;
            PresentCurrentApproval();
            StartApprovalAutoTimerIfNeeded();
        }

        /// <summary>请求到达、点击或悬停恢复都走同一入口；不重置服务端的审批截止时间。</summary>
        private bool PresentCurrentApproval()
        {
            var request = _approvalCurrent;
            if (request == null) return false;
            if (_bridge != null && !_bridge.IsPending(request.Id))
            {
                if (_bridge.TryGetDecision(request.Id, out var decision) && decision != null)
                    HandleApprovalDecided(decision);
                return false;
            }
            CloseComponentSwitcher();
            if (_greetingShowing || GreetingActive) DismissGreeting();

            // 跟音量/电池通知一个套路：细条态来了东西就先把岛体弹成一级胶囊，
            // 否则审批层（PillOnly）在 Bar 态根本不画，用户看不到"允许/拒绝"。
            // 审批是"必须回应的阻塞动作"：细条态与卡片态都要把它弹出来 —— 所以请求声明
            // deferredInCard: false（形态判断统一收口在 ShowPillNotification）。
            // 旧写法只在 Bar 态切形态 → 用户停在卡片页时审批层被 Scope(PillOnly) 挡掉，
            // 界面从不出现、倒计时照走，agent 那边只能等到超时。

            if (ApprovalTitle != null)
                ApprovalTitle.Text = "agent 请求 " + request.Tool + (_approvalQueue.Count > 0 ? $" · 待处理 {_approvalQueue.Count + 1}" : "");
            if (ApprovalDetail != null)
            {
                string detail = Shorten(request.Args, 120);
                if (string.IsNullOrWhiteSpace(detail)) detail = request.Reason;
                else if (!string.IsNullOrWhiteSpace(request.Reason)) detail = Shorten(request.Reason, 40) + " · " + detail;
                ApprovalDetail.Text = detail;
                ApprovalDetail.ToolTip = string.IsNullOrWhiteSpace(request.Reason) ? request.Args
                    : request.Reason + (string.IsNullOrWhiteSpace(request.Args) ? "" : "\n" + request.Args);
            }

            // 细条色点 / 状态胶囊都会被这一句带到「等待授权」；审批层本身也在同一拍占位。
            AgentStatus.Set(AgentRunState.ToolWait, request.Tool, owner: "approval");

            // ForceActive 已经能一直占住，这里给个 UntilUtc 只是为了让"超时未裁决"也有个自然过期兜底。
            ShowPillNotification(IslandNotification.Seconds(
                ApprovalLayerKey,
                Math.Max(0.1, (request.RequestedUtc.AddSeconds(request.TimeoutSec) - DateTime.UtcNow).TotalSeconds),
                deferredInCard: false));
            SyncNotificationLayers();

            IslandLog.Debug("AgentBridge",
                $"审批界面已弹出：{request.Id} tool={request.Tool} 图层可见={_layers.IsVisible(ApprovalLayerKey)}"
                + $" 透明度={ApprovalLayer?.Opacity:0.00} 状态={_state}");

            return true;
        }

        /// <summary>
        /// 服务侧任何一次裁决（界面点的 / 外部 API 裁的 / 等到超时的）都会回调到这里。
        /// 界面收尾只走这一条路 —— 否则用 API 裁决时审批层会一直弹着。
        /// </summary>
        private void OnApprovalDecided(ApprovalDecision decision)
        {
            try
            {
                if (Dispatcher.CheckAccess()) HandleApprovalDecided(decision);
                else Dispatcher.BeginInvoke(new Action(() => HandleApprovalDecided(decision)), DispatcherPriority.Normal);
            }
            catch (Exception ex)
            {
                IslandLog.Warn("AgentBridge", "审批裁决回调失败: " + ex.Message);
            }
        }

        private void HandleApprovalDecided(ApprovalDecision decision)
        {
            // 排队中的请求也可能已被服务超时/外部裁决，不能留到之后再弹。
            int queued = _approvalQueue.Count;
            for (int i = 0; i < queued; i++)
            {
                var waiting = _approvalQueue.Dequeue();
                if (waiting.Id != decision.Id) _approvalQueue.Enqueue(waiting);
            }
            var request = _approvalCurrent;
            if (request == null || !string.Equals(request.Id, decision.Id, StringComparison.Ordinal)) return;

            _approvalCurrent = null;
            _approvalAutoTimer?.Stop();
            _approvalAutoTimer = null;

            bool alive = _bridge != null;
            AppendApprovalLog(request, decision.Approved, decision.DecidedBy, alive);

            _layers.Hide(ApprovalLayerKey);
            IslandLog.Info("AgentBridge",
                $"审批结果 {request.Id}（{request.Tool}）= {(decision.Approved ? "允许" : "拒绝")}"
                + $"（{decision.DecidedBy}{(decision.TimedOut ? "，超时" : "")}，回传={(alive ? "已投递" : "已失效")}）");

            // 归位；若队里还有请求，ShowNextApproval 会立刻再设回 ToolWait。
            AgentStatus.Reset(owner: "approval");
            SyncNotificationLayers();

            if (_approvalQueue.Count > 0) ShowNextApproval();
        }

        /// <summary>
        /// 裁决队首请求：只负责"把决定投给服务"，界面收尾统一由 HandleApprovalDecided 做
        /// （服务侧裁决成功就会回调过来，这里不必再收一遍）。
        /// </summary>
        private void DecideApproval(bool approved, string decidedBy)
        {
            var request = _approvalCurrent;
            if (request == null) return;

            string note = approved ? "用户允许" : "用户拒绝";
            var bridge = _bridge;

            if (bridge != null && bridge.Resolve(request.Id, approved, decidedBy, note)) return;

            // 本地按钮不能改写已经生效的超时/外部裁决。
            if (bridge != null && bridge.TryGetDecision(request.Id, out var actual) && actual != null)
                HandleApprovalDecided(actual);
            else
                HandleApprovalDecided(new ApprovalDecision(request.Id, false, "bridge", "请求已失效，未回传允许", false, DateTime.UtcNow));
        }

        // ================= 交互 =================

        private void ApprovalAllow_Click(object sender, RoutedEventArgs e)
        {
            DecideApproval(true, "mouse");
            e.Handled = true;
        }

        private void ApprovalDeny_Click(object sender, RoutedEventArgs e)
        {
            DecideApproval(false, "mouse");
            e.Handled = true;
        }

        /// <summary>Y 允许 / N（或 Esc）拒绝。焦点在输入框（AI 卡片页）时不抢键。</summary>
        private void MainWindow_ApprovalKeyDown(object sender, KeyEventArgs e)
        {
            if (_approvalCurrent == null) return;
            if (_state == IslandState.Card && Keyboard.FocusedElement is TextBox) return;

            if (e.Key == System.Windows.Input.Key.Y)
            {
                DecideApproval(true, "key");
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.N || e.Key == System.Windows.Input.Key.Escape)
            {
                DecideApproval(false, "key");
                e.Handled = true;
            }
        }

        // ================= 调试钩子 =================

        /// <summary>
        /// ALPHA_ISLAND_AGENT_APPROVAL_FAKE=allow|deny（+ _DELAY=<秒>，默认 5）：
        /// 界面弹出后自动点一次，用来在没人值守的环境里验证"弹出→裁决→回传→收尾"整条链。
        /// </summary>
        private void ConfigureApprovalFake(string mode, double delaySeconds)
        {
            _approvalFakeMode = (mode ?? "").Trim().ToLowerInvariant();
            _approvalFakeDelay = delaySeconds > 0 ? delaySeconds : 5;
        }

        /// <summary>本地造一条审批请求走同一条链路（不需要真的起外部 agent）。</summary>
        private void InjectApprovalDemo()
        {
            var request = new ApprovalRequest(
                "demo-" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture),
                "file.write",
                "{\"path\":\"C:\\\\tmp\\\\alpha-report.md\",\"bytes\":2048}",
                "调试注入：把生成的报告写到磁盘",
                30,
                DateTime.UtcNow);

            IslandLog.Debug("AgentBridge", "调试钩子：注入一条本地审批请求");
            OnApprovalRequested(request);
        }

        private void StartApprovalAutoTimerIfNeeded()
        {
            if (string.IsNullOrEmpty(_approvalFakeMode)) return;

            bool approve = _approvalFakeMode is "allow" or "yes" or "true" or "1";
            double delay = _approvalFakeDelay;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                if (!ReferenceEquals(_approvalAutoTimer, timer)) return;
                IslandLog.Debug("AgentBridge", $"调试钩子：{delay:0.#}s 后自动{(approve ? "允许" : "拒绝")}审批");
                DecideApproval(approve, "debug");
            };
            _approvalAutoTimer = timer;
            timer.Start();
        }

        // ================= 工具 =================

        /// <summary>审批流水：一行一条，方便事后回溯"哪个工具在什么时候被允许/拒绝"。</summary>
        private void AppendApprovalLog(ApprovalRequest request, bool approved, string decidedBy, bool delivered)
        {
            if (!_runtimeEnabled) return; // 隔离界面测试不能向用户的审批流水写入模拟裁决。
            try
            {
                AppPaths.EnsureDirectory(AppPaths.AiDir);
                string line = "{\"t\":\"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                            + "\",\"id\":\"" + Escape(request.Id)
                            + "\",\"tool\":\"" + Escape(request.Tool)
                            + "\",\"approved\":" + (approved ? "true" : "false")
                            + ",\"decidedBy\":\"" + Escape(decidedBy) + "\""
                            + ",\"delivered\":" + (delivered ? "true" : "false")
                            + ",\"args\":\"" + Escape(Shorten(request.Args, 200)) + "\"}";
                File.AppendAllText(AppPaths.AiApprovalLogFile, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                IslandLog.Warn("AgentBridge", "审批流水写入失败: " + ex.Message);
            }
        }

        private static string Escape(string? text) =>
            (text ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");

        private static string Shorten(string? text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return flat.Length <= max ? flat : flat.Substring(0, max) + "…";
        }
    }
}
