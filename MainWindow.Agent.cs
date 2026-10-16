using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Animation;
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

        /// <summary>是否已经订阅过 AgentStatus.Changed。</summary>
        private bool _agentHooked;

        /// <summary>上次读到的设置值（开关关掉时要立刻把点与胶囊收干净）。</summary>
        private bool _agentStatusEnabled;

        /// <summary>上一次写进日志的色点状态（"显示 #FF8A5CFF" / "隐藏"），只在变化时打一行。</summary>


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
            StopAgentWindowTracking();
            ClearAgentSessions();
            _layers.SetPinned(AgentTickerKey, false);
            _layers.SetPresentationSuppressed(AgentTickerKey, true);
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
            _layers.Hide(AgentTickerKey);

            if (wasUp) SyncNotificationLayers();
            else SyncBarIndicators();
        }

        private int _agentStatusRefreshQueued;
        private void QueueAgentStatusRefresh()
        {
            if (_exiting || Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _agentStatusRefreshQueued, 1) != 0) return;
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
                {
                    Interlocked.Exchange(ref _agentStatusRefreshQueued, 0);
                    if (!_exiting && _agentHooked) UpdateAgentStatusUi();
                }));
            }
            catch (InvalidOperationException) { Interlocked.Exchange(ref _agentStatusRefreshQueued, 0); }
        }

        private void AgentStatus_Changed(object? sender, EventArgs e)
        {
            // 将来的桥接（B 组④）可能从非 UI 线程写状态，一律切回 UI 线程再动控件。
            try
            {
                if (Dispatcher.CheckAccess() && !_updatingAgentSessions) UpdateAgentStatusUi();
                else QueueAgentStatusRefresh();
            }
            catch (Exception ex)
            {
                IslandLog.Warn("AgentStatus", "刷新状态界面失败: " + ex.Message);
            }
        }

        /// <summary>把当前 agent 状态写到细条色点与一级胶囊；一秒一拍，只在状态变化时改可见性。</summary>
        private void UpdateAgentStatusUi() => UpdateAgentSessions();

        private void WriteAgentLayerText(bool error)
        {
            if (_agentSessions.TryGetValue(AgentTickerKey, out var session)) WriteAgentSession(session);
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
