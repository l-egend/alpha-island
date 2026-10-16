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
        Error = 4,

        /// <summary>正在执行工具或后台任务。</summary>
        Working = 5
    }

    /// <summary>
    /// 全局 agent 状态。细条上的状态色点、胶囊上的"动作 + 已用时长"都读这里。
    /// </summary>
    public sealed record AgentActivity(string Owner, AgentRunState State, string Detail, DateTime SinceUtc, long ActivityId, string ArtworkFamily = "");

    public static class AgentStatus
    {
        private static readonly object Gate = new();
        private sealed record Claim(AgentRunState State, string Detail, DateTime SinceUtc, long ActivityId, string ArtworkFamily);
        private static readonly System.Collections.Generic.Dictionary<string, Claim> Claims = new();

        private static AgentRunState _state = AgentRunState.Idle;
        private static string _detail = "";
        private static string _owner = "";
        private static DateTime _sinceUtc = DateTime.UtcNow;
        private static long _activityId, _nextActivityId;

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

        public static string Owner { get { lock(Gate)return _owner; } }

        /// <summary>同一 owner 从空闲恢复时创建新的身份；状态切换和续期保留身份。</summary>
        public static long ActivityId { get { lock (Gate) return _activityId; } }

        public static AgentActivity[] Snapshot
        {
            get
            {
                lock (Gate)
                {
                    var result = new AgentActivity[Claims.Count];
                    int index = 0;
                    foreach (var item in Claims)
                        result[index++] = new(item.Key, item.Value.State, item.Value.Detail, item.Value.SinceUtc, item.Value.ActivityId, item.Value.ArtworkFamily);
                    return result;
                }
            }
        }

        public static bool HasClaim(string owner) { lock (Gate) return Claims.ContainsKey(owner); }

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
        public static void Set(AgentRunState state, string detail = "", string owner = "agent", string? artworkFamily = null)
            => SetCore(state, detail, owner, artworkFamily, false);

        /// <summary>One explicit new task renews its identity atomically, without an idle frame.</summary>
        internal static void BeginActivity(AgentRunState state, string detail, string owner, string? artworkFamily)
            => SetCore(state, detail, owner, artworkFamily, true);

        private static void SetCore(AgentRunState state, string detail, string owner, string? artworkFamily, bool beginsActivity)
        {
            bool changed;
            lock (Gate)
            {
                bool claimChanged = false;
                if (state == AgentRunState.Idle) claimChanged = Claims.Remove(owner);
                else
                {
                    Claims.TryGetValue(owner, out var old);
                    string family = artworkFamily ?? old?.ArtworkFamily ?? "";
                    claimChanged = beginsActivity || old == null || old.State != state || old.Detail != (detail ?? "") || old.ArtworkFamily != family;
                    var since = !beginsActivity && old?.State == state ? old.SinceUtc : DateTime.UtcNow;
                    Claims[owner] = new Claim(state, detail ?? "", since, !beginsActivity && old != null ? old.ActivityId : ++_nextActivityId, family);
                }
                Claim? winner = null;string winnerOwner="";
                foreach (var claim in Claims)
                    if (winner == null || Rank(claim.Value.State) > Rank(winner.State)) {winner = claim.Value;winnerOwner=claim.Key;}
                var next = winner?.State ?? AgentRunState.Idle;
                var nextDetail = winner?.Detail ?? detail ?? "";
                long nextActivityId = winner?.ActivityId ?? 0;
                changed = claimChanged || _state != next || _detail != nextDetail || _owner != winnerOwner || _activityId != nextActivityId;
                _activityId = nextActivityId;
                _sinceUtc = winner?.SinceUtc ?? (_state != next ? DateTime.UtcNow : _sinceUtc);
                _state = next;
                _detail = nextDetail;
                _owner=winnerOwner;
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
            AgentRunState.Working or AgentRunState.Streaming => 2,
            AgentRunState.Thinking => 1,
            _ => 0
        };

        /// <summary>细条状态色：空闲不显示，其它状态各有颜色（B 组③ 的 UI 直接用）。</summary>
        public static string ColorHex => ColorHexFor(State);
        public static string ColorHexFor(AgentRunState state) => state switch
        {
            AgentRunState.Thinking => "#FF8A5CFF",
            AgentRunState.Working or AgentRunState.Streaming => "#FF22D3EE",
            AgentRunState.ToolWait => "#FFFFB900",
            AgentRunState.Error => "#FFFF5A5F",
            _ => "#00000000"
        };

        /// <summary>一行中文描述（日志与胶囊文案共用）。</summary>
        public static string Describe()
        {
            string detail = Detail;
            string name = State switch
            {
                AgentRunState.Thinking => "思考中",
                AgentRunState.Working => "执行中", AgentRunState.Streaming => "输出中",
                AgentRunState.ToolWait => "等待授权",
                AgentRunState.Error => "出错",
                _ => "空闲"
            };
            return string.IsNullOrWhiteSpace(detail) ? name : name + "｜" + detail;
        }
    }
}
