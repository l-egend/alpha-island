using System;

namespace alpha_island.Core
{
    /// <summary>
    /// agent 的运行状态（B 组③）。
    /// 这一层刻意做成"谁都能写、谁都能读"的静态量表：AI 对话卡片（B 组①/②）驱动它，
    /// 以后外部 agent 通过桥接（B 组④）或命令行也能写，岛体只负责显示。
    /// 状态迁移全部由 UI 线程调用（卡片页与桥接回调都先 Dispatcher 回 UI 线程）。
    /// </summary>
    public enum AgentRunState
    {
        /// <summary>空闲，没有任务。</summary>
        Idle = 0,

        /// <summary>已发出请求，还没收到第一个 token。</summary>
        Thinking = 1,

        /// <summary>正在流式输出。</summary>
        Streaming = 2,

        /// <summary>等待工具权限审批（B 组④ 用）。</summary>
        ToolWait = 3,

        /// <summary>出错（网络/鉴权/被中断）。</summary>
        Error = 4
    }

    /// <summary>
    /// 全局 agent 状态。细条上的状态色点、胶囊上的"动作 + 已用时长"都读这里。
    /// </summary>
    public static class AgentStatus
    {
        private static readonly object Gate = new();
        private sealed record Claim(AgentRunState State, string Detail, DateTime SinceUtc);
        private static readonly System.Collections.Generic.Dictionary<string, Claim> Claims = new();

        private static AgentRunState _state = AgentRunState.Idle;
        private static string _detail = "";
        private static DateTime _sinceUtc = DateTime.UtcNow;

        /// <summary>状态或说明变化时触发（UI 线程）。</summary>
        public static event EventHandler? Changed;

        public static AgentRunState State
        {
            get { lock (Gate) return _state; }
        }

        /// <summary>状态说明，例如"正在回答""等待授权：run_command"。</summary>
        public static string Detail
        {
            get { lock (Gate) return _detail; }
        }

        /// <summary>当前状态开始的时间（算"已用时长"用）。</summary>
        public static DateTime SinceUtc
        {
            get { lock (Gate) return _sinceUtc; }
        }

        /// <summary>当前状态已持续多久。</summary>
        public static TimeSpan Elapsed
        {
            get { lock (Gate) return DateTime.UtcNow - _sinceUtc; }
        }

        public static bool IsBusy
        {
            get { lock (Gate) return _state != AgentRunState.Idle; }
        }

        /// <summary>置状态。同一个状态重复置只更新说明，不重置计时。</summary>
        public static void Set(AgentRunState state, string detail = "", string owner = "agent")
        {
            bool changed;
            lock (Gate)
            {
                if (state == AgentRunState.Idle) Claims.Remove(owner);
                else
                {
                    var since = Claims.TryGetValue(owner, out var old) && old.State == state
                        ? old.SinceUtc : DateTime.UtcNow;
                    Claims[owner] = new Claim(state, detail ?? "", since);
                }
                Claim? winner = null;
                foreach (var claim in Claims.Values)
                    if (winner == null || Rank(claim.State) > Rank(winner.State)) winner = claim;
                var next = winner?.State ?? AgentRunState.Idle;
                var nextDetail = winner?.Detail ?? detail ?? "";
                changed = _state != next || _detail != nextDetail;
                _sinceUtc = winner?.SinceUtc ?? (_state != next ? DateTime.UtcNow : _sinceUtc);
                _state = next;
                _detail = nextDetail;
            }

            if (!changed) return;
            try { Changed?.Invoke(null, EventArgs.Empty); }
            catch (Exception ex) { IslandLog.Error("AgentStatus", ex); }
        }

        /// <summary>回到空闲（对话结束 / 被用户停止 / 出错后清理）。</summary>
        public static void Reset(string detail = "", string owner = "agent") => Set(AgentRunState.Idle, detail, owner);

        private static int Rank(AgentRunState state) => state switch
        {
            AgentRunState.ToolWait => 4,
            AgentRunState.Error => 3,
            AgentRunState.Streaming => 2,
            AgentRunState.Thinking => 1,
            _ => 0
        };

        /// <summary>细条状态色：空闲不显示，其它状态各有颜色（B 组③ 的 UI 直接用）。</summary>
        public static string ColorHex
        {
            get
            {
                switch (State)
                {
                    case AgentRunState.Thinking: return "#FF8A5CFF";   // 紫
                    case AgentRunState.Streaming: return "#FF22D3EE";  // 青
                    case AgentRunState.ToolWait: return "#FFFFB900";   // 橙
                    case AgentRunState.Error: return "#FFFF5A5F";      // 红
                    default: return "#00000000";
                }
            }
        }

        /// <summary>一行中文描述（日志与胶囊文案共用）。</summary>
        public static string Describe()
        {
            string detail = Detail;
            string name = State switch
            {
                AgentRunState.Thinking => "思考中",
                AgentRunState.Streaming => "输出中",
                AgentRunState.ToolWait => "等待授权",
                AgentRunState.Error => "出错",
                _ => "空闲"
            };
            return string.IsNullOrWhiteSpace(detail) ? name : name + "｜" + detail;
        }
    }
}
