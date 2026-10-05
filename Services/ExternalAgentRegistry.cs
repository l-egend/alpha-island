using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using alpha_island.Core;

namespace alpha_island.Services
{
    /// <summary>External sessions have independent claims and expire when their heartbeat stops.</summary>
    public sealed class ExternalAgentRegistry
    {
        public sealed record Connection(string Source, string Session, string State, string Detail, DateTime ExpiresUtc);
        private readonly object _gate = new();
        private readonly Dictionary<string, Connection> _connections = new(StringComparer.Ordinal);
        private bool _accepting = true;
        private static readonly Regex Identifier = new("^[a-zA-Z0-9_.:-]{1,64}$", RegexOptions.CultureInvariant);
        private static string Owner(string source, string session) => "external:" + source + "/" + session;

        public Connection[] Snapshot { get { lock (_gate) return _connections.Values.ToArray(); } }

        public bool Update(string source, string session, string state, string detail, int ttlSec, DateTime now, out string error)
        {
            error = "";
            if (!Identifier.IsMatch(source) || !Identifier.IsMatch(session))
            { error = "source/session 必须为 1..64 位字母、数字或 _ . : -"; return false; }
            AgentRunState runState;
            switch (state)
            {
                case "idle": runState = AgentRunState.Idle; break;
                case "thinking": runState = AgentRunState.Thinking; break;
                case "streaming": runState = AgentRunState.Streaming; break;
                case "tool-wait": runState = AgentRunState.ToolWait; break;
                case "error": runState = AgentRunState.Error; break;
                default: error = "未知 agent 状态"; return false;
            }
            var owner = Owner(source, session);
            lock (_gate)
            {
                if (!_accepting) { error = "桥接已关闭"; return false; }
                Sweep(now);
                if (!_connections.ContainsKey(owner) && _connections.Count >= 64)
                { error = "最多连接 64 个 agent 会话"; return false; }
                detail = (detail ?? "").Trim();
                if (detail.Length > 160) detail = detail[..160];
                _connections[owner] = new(source, session, state, detail, now.AddSeconds(Math.Clamp(ttlSec, 5, 300)));
                AgentStatus.Set(runState, detail, owner);
            }
            return true;
        }

        public void Sweep(DateTime now)
        {
            lock (_gate)
                foreach (var pair in _connections.Where(p => p.Value.ExpiresUtc <= now).ToArray())
                {
                    _connections.Remove(pair.Key);
                    AgentStatus.Reset(owner: pair.Key);
                }
        }

        public void Clear()
        {
            lock (_gate)
            {
                foreach (var owner in _connections.Keys) AgentStatus.Reset(owner: owner);
                _connections.Clear();
            }
        }

        public void Open() { lock (_gate) _accepting = true; }
        public void Close()
        {
            lock (_gate) { _accepting = false; Clear(); }
        }
    }
}
