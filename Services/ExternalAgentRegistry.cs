using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using alpha_island.Core;

namespace alpha_island.Services;

/// <summary>Transport claims are grouped by native conversation before publishing island state.</summary>
public sealed class ExternalAgentRegistry
{
    public sealed record Connection(string Source, string Session, string State, string Detail, DateTime ExpiresUtc,
        int? ProcessId = null, long? ProcessStartStamp = null, string ArtworkFamily = "", bool NativeClaudeSession = false,
        string ParentSession = "", DateTime UpdatedUtc = default, bool NativeHostSession = false,
        [property: System.Text.Json.Serialization.JsonIgnore] string NativeThreadId = "",
        [property: System.Text.Json.Serialization.JsonIgnore] string NativeTurnId = "",
        [property: System.Text.Json.Serialization.JsonIgnore] DateTime ActivityStartedUtc = default);
    private readonly object _gate = new();
    private readonly Dictionary<string, Connection> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _parents = new(StringComparer.Ordinal), _aliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _ended = new(StringComparer.Ordinal);
    private readonly HashSet<string> _explicitStarts = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Thread, string Turn), DateTime> _finishedTurns = new();
    private readonly HashSet<string> _published = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string State, DateTime Received, long Count)> _observations = new(StringComparer.Ordinal);
    private bool _accepting = true;
    private static readonly Regex Identifier = new("^[a-zA-Z0-9_.:-]{1,64}$", RegexOptions.CultureInvariant);
    private static string Owner(string source, string session) => "external:" + source + "/" + session;
    public Connection[] Snapshot { get { lock (_gate) return _connections.Values.ToArray(); } }
    internal string DiagnosticsText()
    {
        lock (_gate) return $"活动主会话={_published.Count}；子任务连接={_connections.Values.Count(c => c.ParentSession.Length > 0)}\n"
            + (_observations.Count == 0 ? "尚未收到外部助手状态事件" : string.Join("\n", _observations.Select(p =>
                $"{p.Key}：最近状态={p.Value.State}；收到时间={p.Value.Received.ToLocalTime():yyyy-MM-dd HH:mm:ss}；累计事件={p.Value.Count}")));
    }
    public bool Update(string source, string session, string state, string detail, int ttlSec, DateTime now, out string error,
        int? processId = null, string? artworkFamily = null, string? parentSession = null, string? sessionAlias = null, bool beginsActivity = false, string? nativeThreadId = null, string? nativeTurnId = null, DateTime? activityStartedUtc = null)
        => UpdateCore(source, session, state, detail, ttlSec, now, out error, processId, artworkFamily, parentSession, sessionAlias, beginsActivity, false, nativeThreadId, nativeTurnId, activityStartedUtc);

    internal bool Connect(string source, string session, int ttlSec, DateTime now, out string error,
        int? processId = null, string? artworkFamily = null, string? parentSession = null, string? sessionAlias = null)
        => UpdateCore(source, session, "idle", "", ttlSec, now, out error, processId, artworkFamily, parentSession, sessionAlias, false, true, null, null);

    private bool UpdateCore(string source, string session, string state, string detail, int ttlSec, DateTime now, out string error,
        int? processId, string? artworkFamily, string? parentSession, string? sessionAlias, bool beginsActivity, bool connectOnly, string? nativeThreadId, string? nativeTurnId, DateTime? activityStartedUtc = null)
    {
        error = "";
        if (!Identifier.IsMatch(source) || !Identifier.IsMatch(session)
            || !string.IsNullOrEmpty(parentSession) && !Identifier.IsMatch(parentSession)
            || !string.IsNullOrEmpty(sessionAlias) && !Identifier.IsMatch(sessionAlias))
        { error = "source/session/parentSession/sessionAlias 标识无效"; return false; }
        if (!string.IsNullOrEmpty(nativeThreadId) && !Identifier.IsMatch(nativeThreadId)
            || !string.IsNullOrEmpty(nativeTurnId) && !Identifier.IsMatch(nativeTurnId))
        { error = "原生任务标识无效"; return false; }
        if (!KnownState(state)) { error = "未知 agent 状态"; return false; }
        if (artworkFamily is not (null or "" or "gemini" or "claude" or "deepseek" or "codex" or "default"))
        { error = "未知形象类型"; return false; }
        if (parentSession == session || !string.IsNullOrEmpty(sessionAlias) && parentSession == sessionAlias) { error = "会话别名不能指向父会话"; return false; }
        lock (_gate)
        {
            if (!_accepting) { error = "桥接已关闭"; return false; }
            SweepLocked(now);
            // A SessionStart is readiness, not completion. It can arrive after UserPromptSubmit.
            // Preserve an active owner, parent or transport alias before changing any identity maps.
            if (connectOnly)
            {
                var roots = new[] { session, parentSession, sessionAlias }.Where(id => !string.IsNullOrEmpty(id))
                    .Select(id => Root(Owner(source, id!))).ToHashSet(StringComparer.Ordinal);
                if (_connections.Any(pair => pair.Value.State != "idle" && roots.Contains(Root(pair.Key))))
                    return true;
            }
            string owner = ResolveAlias(Owner(source, session));
            if (!string.IsNullOrEmpty(sessionAlias) && sessionAlias != session)
            {
                string alias = Owner(source, sessionAlias);
                if (ResolveAlias(owner) == alias) { error = "会话别名循环"; return false; }
                _connections.Remove(alias); _aliases[alias] = owner;
            }
            // Some integrations report an execution and its transport independently.
            // Only exact native task identity is evidence that they are the same work.
            if (source == "codex" && !string.IsNullOrEmpty(nativeThreadId) && !string.IsNullOrEmpty(nativeTurnId)
                && string.IsNullOrEmpty(parentSession))
            {
                var duplicate = _connections.FirstOrDefault(pair => pair.Key != owner && pair.Value.ParentSession.Length == 0
                    && pair.Value.NativeThreadId == nativeThreadId && pair.Value.NativeTurnId == nativeTurnId);
                if (duplicate.Key != null)
                {
                    _connections.Remove(owner); _aliases[owner] = duplicate.Key; owner = duplicate.Key;
                }
            }
            _connections.TryGetValue(owner, out var previous);
            if (beginsActivity && previous is { State: not "idle", NativeTurnId.Length: > 0 }
                && previous.NativeThreadId == nativeThreadId && previous.NativeTurnId == nativeTurnId) beginsActivity = false;
            string nativeThread = string.IsNullOrEmpty(nativeThreadId) ? previous?.NativeThreadId ?? "" : nativeThreadId;
            string nativeTurn = string.IsNullOrEmpty(nativeTurnId) ? beginsActivity ? "" : previous?.NativeTurnId ?? "" : nativeTurnId;
            if (source == "codex" && nativeTurn.Length > 0)
            {
                // A late Stop is just as stale as a late tool heartbeat from the previous turn.
                if (!beginsActivity && previous is { State: not "idle", NativeTurnId.Length: > 0 }
                    && (previous.NativeTurnId != nativeTurn || previous.NativeThreadId != nativeThread)) return true;
                if (state != "idle" && _finishedTurns.ContainsKey((nativeThread, nativeTurn))) return true;
                if (state == "idle" && !connectOnly) _finishedTurns[(nativeThread, nativeTurn)] = now.AddHours(24);
            }
            string parent = !string.IsNullOrEmpty(parentSession) ? ResolveAlias(Owner(source, parentSession))
                : _parents.GetValueOrDefault(owner, "");
            if (parent.Length > 0)
            {
                if (parent == owner || Root(parent) == owner) { error = "父会话关系循环"; return false; }
                _parents[owner] = parent;
            }
            string root = Root(owner);
            if (!beginsActivity && state != "idle" && parent.Length == 0 && _ended.ContainsKey(root)
                && nativeTurn.Length == 0 && (_explicitStarts.Contains(root)
                    || nativeThread.Length > 0 && previous?.NativeThreadId == nativeThread))
            { Publish(); return true; }
            if (beginsActivity && state != "idle") { _explicitStarts.Add(root); _ended.Remove(root); _ended.Remove(owner); }
            // Keep an ended conversation tombstone so late child stop/tool events cannot resurrect it.
            if (parent.Length == 0 && state == "idle")
            {
                // Initial SessionStart/idle is not evidence that a conversation ended.
                if (_connections.Any(pair => Root(pair.Key) == root && pair.Value.State != "idle"))
                {
                    if (!connectOnly) AgentStatus.Finish(root, now);
                    _ended[root] = now.AddHours(24);
                    foreach (string child in _connections.Keys.Where(key => key != owner && Root(key) == root).ToArray())
                    { _ended[child] = now.AddHours(24); _connections.Remove(child); }
                }
            }
            else if (parent.Length == 0) _ended.Remove(root);
            if (parent.Length > 0 && (_ended.ContainsKey(root) || _ended.ContainsKey(owner))) { _connections.Remove(owner); Publish(); return true; }
            if (!_connections.ContainsKey(owner) && _connections.Count >= 64) { error = "最多连接 64 个 agent 会话"; return false; }
            detail = (detail ?? "").Trim(); if (detail.Length > 160) detail = detail[..160];
            long? stamp = processId != null ? AgentHostProcessMonitor.StartStamp(processId.Value) : previous?.ProcessStartStamp;
            processId ??= previous?.ProcessId;
            string family = string.IsNullOrEmpty(artworkFamily) ? previous?.ArtworkFamily ?? "" : artworkFamily;
            bool native = processId is int pid && stamp != null && AgentHostProcessMonitor.IsNativeHost(source, pid);
            _connections[owner] = new(source, owner[(owner.IndexOf('/') + 1)..], state, detail,
                now.AddSeconds(Math.Clamp(ttlSec, 5, 300)), processId, stamp, family, native && source == "claude-code",
                parent.Length == 0 ? "" : parent[(parent.IndexOf('/') + 1)..], now, native, source == "codex" ? nativeThread : "", source == "codex" ? nativeTurn : "",
                state != "idle" && (beginsActivity || previous == null || previous.State == "idle")
                    ? activityStartedUtc is { } started && started <= now && started >= now.AddHours(-24) ? started : now
                    : previous?.ActivityStartedUtc ?? now);
            if (!_observations.ContainsKey(source) && _observations.Count >= 64) _observations.Remove(_observations.OrderBy(p => p.Value.Received).First().Key);
            _observations.TryGetValue(source, out var observation); _observations[source] = (state, now, observation.Count + 1);
            TrimMetadata(); Publish(beginsActivity && state != "idle" ? root : null); return true;
        }
    }
    internal bool FinishNativeTurn(Connection expected, DateTime now)
    {
        lock (_gate)
        {
            string owner = Owner(expected.Source, expected.Session);
            if (!_accepting || !_connections.TryGetValue(owner, out var current) || current.State == "idle"
                || current.NativeThreadId != expected.NativeThreadId || current.NativeTurnId != expected.NativeTurnId
                || current.ActivityStartedUtc != expected.ActivityStartedUtc) return false;
            if (expected.NativeTurnId.Length > 0) _finishedTurns[(expected.NativeThreadId, expected.NativeTurnId)] = now.AddHours(24);
            return UpdateCore(current.Source, current.Session, "idle", "", 5, now, out _, null, null, null, null,
                false, false, current.NativeThreadId, current.NativeTurnId);
        }
    }
    private string ResolveAlias(string owner)
    {
        for (int depth = 0; depth < 16 && _aliases.TryGetValue(owner, out string? next); depth++) owner = next;
        return owner;
    }
    private string Root(string owner)
    {
        owner = ResolveAlias(owner);
        for (int depth = 0; depth < 16 && _parents.TryGetValue(owner, out string? parent); depth++) owner = ResolveAlias(parent);
        return owner;
    }
    private static bool KnownState(string state) => state is "idle" or "thinking" or "working" or "streaming" or "tool-wait" or "error";
    private static AgentRunState State(string state) => state switch
    { "thinking" => AgentRunState.Thinking, "working" => AgentRunState.Working, "streaming" => AgentRunState.Streaming,
        "tool-wait" => AgentRunState.ToolWait, "error" => AgentRunState.Error, _ => AgentRunState.Idle };
    private static int Rank(string state) => state switch { "tool-wait" => 5, "error" => 4, "working" => 3, "streaming" => 2, "thinking" => 1, _ => 0 };
    private void Publish(string? startedRoot = null)
    {
        var groups = _connections.Where(pair => pair.Value.State != "idle" && !_ended.ContainsKey(Root(pair.Key)))
            .GroupBy(pair => Root(pair.Key), StringComparer.Ordinal).ToArray();
        var owners = groups.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        foreach (string old in _published.Where(owner => !owners.Contains(owner)).ToArray()) AgentStatus.Reset(owner: old);
        _published.Clear();
        foreach (var group in groups)
        {
            var winner = group.OrderByDescending(pair => Rank(pair.Value.State)).ThenByDescending(pair => pair.Value.UpdatedUtc).First().Value;
            string family = _connections.GetValueOrDefault(group.Key)?.ArtworkFamily is { Length: > 0 } rootFamily ? rootFamily : winner.ArtworkFamily;
            if (group.Key == startedRoot) AgentStatus.BeginActivity(State(winner.State), winner.Detail, group.Key, family);
            else AgentStatus.Set(State(winner.State), winner.Detail, group.Key, family);
            _published.Add(group.Key);
        }
    }
    private void TrimMetadata()
    {
        while (_finishedTurns.Count > 256) _finishedTurns.Remove(_finishedTurns.OrderBy(pair => pair.Value).First().Key);
        while (_ended.Count > 256) _ended.Remove(_ended.OrderBy(pair => pair.Value).First().Key);
        _explicitStarts.RemoveWhere(key => !_connections.ContainsKey(key) && !_ended.ContainsKey(key));
        // Active connections and their ancestors stay in the bounded metadata cache.
        var retained = new HashSet<string>(_connections.Keys, StringComparer.Ordinal);
        foreach (string key in _connections.Keys)
        {
            string current = key;
            for (int depth = 0; depth < 16 && _parents.TryGetValue(current, out string? parent); depth++) { retained.Add(parent); current = parent; }
        }
        if (_parents.Count > 1024) foreach (string key in _parents.Keys.Where(key => !retained.Contains(key)).Take(_parents.Count - 1024).ToArray()) _parents.Remove(key);
        if (_aliases.Count > 1024) foreach (string key in _aliases.Keys.Where(key => !retained.Contains(key)).Take(_aliases.Count - 1024).ToArray()) _aliases.Remove(key);
    }
    private void SweepLocked(DateTime now)
    {
        foreach (var key in _finishedTurns.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray()) _finishedTurns.Remove(key);
        foreach (var pair in _connections.Where(pair => pair.Value.ExpiresUtc <= now
            && !(pair.Value.NativeHostSession && pair.Value.State is "thinking" or "working" or "streaming" or "tool-wait")
            || pair.Value.State != "idle" && pair.Value.ProcessId is int pid && !AgentHostProcessMonitor.IsAlive(pid, pair.Value.ProcessStartStamp)).ToArray())
            _connections.Remove(pair.Key);
        foreach (string key in _ended.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray()) _ended.Remove(key);
    }
    public void Sweep(DateTime now) { lock (_gate) { SweepLocked(now); Publish(); } }
    public void Clear()
    {
        lock (_gate) { foreach (string owner in _published) AgentStatus.Reset(owner: owner); _published.Clear(); _connections.Clear(); _parents.Clear(); _aliases.Clear(); _ended.Clear(); _explicitStarts.Clear(); _finishedTurns.Clear(); }
    }
    public void Open() { lock (_gate) _accepting = true; }
    public void Close() { lock (_gate) { _accepting = false; Clear(); } }
}
