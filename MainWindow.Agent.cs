using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using alpha_island.Core;
using Color = System.Windows.Media.Color;

// B 组③：agent 运行状态的可视化（细条色点 + 一级胶囊）。
// 状态本身（State/Detail/SinceUtc/Elapsed/Changed/ColorHex）全部在 Core\AgentStatus.cs，
// 这里只负责把它画出来：
//   · 细条上一个小圆点，颜色随状态变（思考紫 / 输出青 / 等待授权黄 / 出错红），Idle 时收起；
//   · 一级胶囊（Pill）占位显示"正在思考/正在输出/等待工具授权/出错了 + 已用时长"，
//     靠 IslandLayerHost 的 ForceActive 语义让音乐图层自动让位。
// 驱动有两路：AgentStatus.Changed（状态一变立刻刷新）与 1 秒 ticker（刷新已用时长）。
namespace alpha_island
{
    public partial class MainWindow
    {
        private const string AgentTickerKey = "agentstatus";
        private static readonly TimeSpan AgentUiInterval = TimeSpan.FromSeconds(1);

        /// <summary>细条上那个状态色点要不要亮（SyncBarIndicators 会读它）。</summary>
        private bool _barAgentVisible;

        /// <summary>当前胶囊是否已经被我们占住；用于只在"占用状态翻转"那一次重算图层互斥。</summary>
        private bool _agentLayerUp;
        private AgentRunState _lastAgentUiState = AgentRunState.Idle;

        /// <summary>是否已经订阅过 AgentStatus.Changed。</summary>
        private bool _agentHooked;

        /// <summary>上次读到的设置值（开关关掉时要立刻把点与胶囊收干净）。</summary>
        private bool _agentStatusEnabled;

        /// <summary>上一次写进日志的色点状态（"显示 #FF8A5CFF" / "隐藏"），只在变化时打一行。</summary>
        private string _agentDotLogged = "";

        /// <summary>由 ApplyOptions 调用：设置变了就重挂/摘掉这条链路。</summary>
        private void OnAgentOptionsChanged()
        {
            bool enabled = _options != null && SettingsRegistry.GetBool(_options, "AgentStatusEnabled", true);
            if (!enabled)
            {
                // 关开关时要立刻收尾：色点熄灭、胶囊让回给音乐（ForceActive 会失效，但仍需一次 Sync）。
                StopAgentStatus();
                _agentStatusEnabled = false;
                return;
            }

            _agentStatusEnabled = true;
            StartAgentStatus();
            UpdateAgentStatusUi();
        }

        private void StartAgentStatus()
        {
            if (!_agentHooked)
            {
                _agentHooked = true;
                AgentStatus.Changed += AgentStatus_Changed;
            }

            // runImmediately：开机（或刚打开开关）时先按当前状态画一次，不用等满一秒。
            _ticker?.Register(AgentTickerKey, AgentUiInterval, UpdateAgentStatusUi, runImmediately: true);
        }

        private void StopAgentStatus()
        {
            _ticker?.Unregister(AgentTickerKey);

            if (_agentHooked)
            {
                _agentHooked = false;
                AgentStatus.Changed -= AgentStatus_Changed;
            }

            _barAgentVisible = false;
            if (BarAgentDot != null) BarAgentDot.Visibility = Visibility.Collapsed;

            bool wasUp = _agentLayerUp;
            _agentLayerUp = false;
            _lastAgentUiState = AgentRunState.Idle;
            _layers.Hide(AgentTickerKey);

            if (wasUp) SyncNotificationLayers();
            else SyncBarIndicators();
        }

        private void AgentStatus_Changed(object? sender, EventArgs e)
        {
            // 将来的桥接（B 组④）可能从非 UI 线程写状态，一律切回 UI 线程再动控件。
            try
            {
                if (Dispatcher.CheckAccess()) UpdateAgentStatusUi();
                else Dispatcher.BeginInvoke(new Action(UpdateAgentStatusUi), DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                IslandLog.Warn("AgentStatus", "刷新状态界面失败: " + ex.Message);
            }
        }

        /// <summary>把当前 agent 状态写到细条色点与一级胶囊；一秒一拍，只在状态变化时改可见性。</summary>
        private void UpdateAgentStatusUi()
        {
            if (_options == null || BarAgentDot == null) return;

            bool enabled = SettingsRegistry.GetBool(_options, "AgentStatusEnabled", true);
            if (!enabled)
            {
                if (_agentStatusEnabled) StopAgentStatus();
                _agentStatusEnabled = false;
                return;
            }
            _agentStatusEnabled = true;

            bool enteredToolWait = AgentStatus.State == AgentRunState.ToolWait && _lastAgentUiState != AgentRunState.ToolWait;
            _lastAgentUiState = AgentStatus.State;
            bool error = AgentStatus.State == AgentRunState.Error;
            bool up = AgentStatus.IsBusy || error;

            // ---- 细条色点 ----
            _barAgentVisible = up;
            if (up)
            {
                var color = ParseAgentColor(AgentStatus.ColorHex);
                if (BarAgentDot.Fill is SolidColorBrush brush && !brush.IsFrozen) brush.Color = color;
                else BarAgentDot.Fill = new SolidColorBrush(color);

                BarAgentDot.Visibility = Visibility.Visible;
            }
            else
            {
                BarAgentDot.Visibility = Visibility.Collapsed;
            }

            // 色点这一侧也留一行可核对的事实（颜色/亮灭只在真的变了时才打）。
            string dotState = up ? "显示 " + AgentStatus.ColorHex : "隐藏";
            if (dotState != _agentDotLogged)
            {
                _agentDotLogged = dotState;
                IslandLog.Debug("AgentStatus", $"细条色点 = {dotState}（{AgentStatus.Describe()}）");
            }

            // ---- 一级胶囊 ----
            if (up && _approvalCurrent == null)
            {
                WriteAgentLayerText(error);
                // 新任务和新进入的授权等待各提醒一次；同一状态续期不重新展开。
                // 剪切板待粘贴、文件拖入与锁屏期间保留当前交互，状态可从旁侧小岛查看。
                bool announce = (!_agentLayerUp || enteredToolWait) && !_clipboardLocked && !_sessionLocked
                    && !_fileDragActive && !_exportingTransferItem;
                ShowPillNotification(IslandNotification.Seconds(AgentTickerKey, 3, opensIsland: announce));
            }
            else if (_layers.IsPinned(AgentTickerKey))
            {
                // 置顶保留的是状态视图，结束后也必须更新内容，不能停在上一轮的动作和计时。
                WriteAgentLayerText(error);
            }
            else
            {
                _layers.Hide(AgentTickerKey);
            }

            // 占用状态翻转时才值得走一遍完整结算（音乐图层要让位 / 让回来）；
            // 平时只刷新细条自己那一层，免得每秒打扰一次音乐胶囊的显示逻辑。
            if (up != _agentLayerUp)
            {
                _agentLayerUp = up;
                SyncNotificationLayers();
                // 卡片态通过旁侧小岛提醒，不打断正在操作的详情页。
                IslandLog.Debug("AgentStatus",
                    $"agent 状态图层 = {(up ? "占用" : "让出")}（实际可见={_layers.IsVisible(AgentTickerKey)}）：{AgentStatus.Describe()}");
            }
            else
            {
                SyncBarIndicators();
            }
        }

        /// <summary>写字形/标题/说明/已用时长。颜色统一取自 AgentStatus.ColorHex，细条点与胶囊保持一致。</summary>
        private void WriteAgentLayerText(bool error)
        {
            string title;
            string glyph;
            switch (AgentStatus.State)
            {
                case AgentRunState.Thinking:
                    title = "正在思考"; glyph = "\uE945"; break;
                case AgentRunState.Streaming:
                    title = "正在输出"; glyph = "\uE895"; break;
                case AgentRunState.ToolWait:
                    title = "等待工具授权"; glyph = "\uE73E"; break;
                case AgentRunState.Error:
                    title = "出错了"; glyph = "\uE783"; break;
                default:
                    title = "空闲"; glyph = "\uE99A"; break;
            }

            bool idle = AgentStatus.State == AgentRunState.Idle;
            string detail = idle
                ? "当前没有运行中的任务"
                : string.IsNullOrWhiteSpace(AgentStatus.Detail) ? "agent 运行中" : AgentStatus.Detail;

            if (AgentNotifyGlyph != null)
            {
                AgentNotifyGlyph.Text = glyph;
                var color = ParseAgentColor(AgentStatus.ColorHex);
                if (AgentNotifyGlyph.Foreground is SolidColorBrush brush && !brush.IsFrozen) brush.Color = color;
                else AgentNotifyGlyph.Foreground = new SolidColorBrush(color);
            }

            if (AgentNotifyTitle != null) AgentNotifyTitle.Text = title;
            if (AgentNotifyDetail != null) AgentNotifyDetail.Text = detail;
            if (AgentNotifyValue != null)
            {
                AgentNotifyValue.Text = idle
                    ? "—"
                    : error ? "⚠"
                    : AgentStatus.Elapsed.TotalSeconds.ToString("0.0") + "s";
            }
        }

        /// <summary>把 AgentStatus.ColorHex（#AARRGGBB）翻成画刷用的颜色；解析失败退回思考紫。</summary>
        private static Color ParseAgentColor(string hex)
        {
            try
            {
                // 全限定：UseWindowsForms 下 ColorConverter 也存在于 System.Drawing。
                if (!string.IsNullOrWhiteSpace(hex) &&
                    System.Windows.Media.ColorConverter.ConvertFromString(hex) is Color parsed)
                {
                    return parsed;
                }
            }
            catch
            {
                // 颜色写错不值得让界面炸掉，走下面的兜底。
            }

            return Color.FromRgb(0x8A, 0x5C, 0xFF);
        }
    }
}
