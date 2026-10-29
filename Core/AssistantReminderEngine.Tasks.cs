using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Security.Cryptography;
using System.Text;

namespace alpha_island.Core;

internal sealed partial class AssistantReminderEngine
{
    private sealed record WaitState(long Id, AgentRunState State, DateTimeOffset Since, string Label);
    private sealed record Progress(long Bytes, DateTimeOffset Since, bool Measuring, bool SeenRunning, bool Ended);
    private sealed record EndedAgent(string Owner, long Id, string Label, DateTimeOffset When, bool Failed);
    private readonly Dictionary<string, WaitState> _agents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Progress> _downloadProgress = new(StringComparer.Ordinal);
    private readonly List<EndedAgent> _endedAgents = [];
    private readonly HashSet<string> _endedIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _endedOrder = new();
    internal void RecordEnded(string owner, long id, DateTimeOffset when, bool failed)
    {
        string key = Digest(owner) + ":" + id;
        if (!_endedIds.Add(key)) return;
        _endedOrder.Enqueue(key);
        while (_endedOrder.Count > 256) _endedIds.Remove(_endedOrder.Dequeue());
        string label = _agents.TryGetValue(owner, out var value) ? value.Label : owner.Split('/')[0] switch
        { "external:codex" => "Codex", "external:antigravity" => "Antigravity", "external:claude-code" => "Claude Code", "external:deepseek-harness" => "DSH", _ => L.T("助手任务") };
        if (_gameName != null && when >= _gameStarted) _gameFinished++;
        if (_quiet && when >= _quietStarted) _quietFinished++;
        _endedAgents.Add(new(owner[..Math.Min(256, owner.Length)], id, label, when, failed));
        while (_endedAgents.Count > 64) _endedAgents.RemoveAt(0);
    }
    private void ObserveAgents(AssistantFrame frame)
    {
        var live = frame.Agents.Take(64).ToDictionary(a => a.Owner, StringComparer.Ordinal);
        foreach (var notice in frame.Notifications.Where(n => n.Reminder is { Pending: true } r && r.Rule is "agent-wait" or "agent-error"))
        {
            string owner = notice.Reminder!.Actions.FirstOrDefault(a => a.Id == "agent")?.Target ?? "";
            bool Relevant(AgentRunState state) => notice.Reminder.Rule == "agent-wait"
                ? state is AgentRunState.ToolWait or AgentRunState.InputWait : state == AgentRunState.Error;
            if (live.TryGetValue(owner, out var current) ? !Relevant(current.State) : frame.Now - _firstObservation >= TimeSpan.FromSeconds(30))
                Resolve(notice.Key, L.T("当前未再检测到此等待或错误状态，请以原助手为准"));
        }
        foreach (string owner in _agents.Keys.Where(k => !live.ContainsKey(k)).ToArray())
        {
            var previous = _agents[owner]; ResolveAgent(owner, previous.Id, L.T("当前活动状态已变化，请以助手内结果为准")); _agents.Remove(owner);
        }
        foreach (var agent in live.Values)
        {
            bool same = _agents.TryGetValue(agent.Owner, out var previous) && previous.Id == agent.ActivityId && previous.State == agent.State;
            if (previous != null && !same) ResolveAgent(agent.Owner, previous.Id, L.T("任务已离开该状态"));
            var current = same ? previous! : new WaitState(agent.ActivityId, agent.State, frame.Now, Clean(agent.Label));
            _agents[agent.Owner] = current;
            if (!_options.Enabled || !_options.AgentFollowup) continue;
            string id = Digest(agent.Owner) + ":" + agent.ActivityId;
            if (agent.State is AgentRunState.ToolWait or AgentRunState.InputWait && frame.Now - current.Since >= TimeSpan.FromMinutes(Math.Clamp(_options.AgentWaitMinutes, 1, 60)))
                Put(frame, "agent-wait", id, current.Label + L.T(" · 等待处理"), agent.State == AgentRunState.InputWait
                    ? L.T("该任务等待回复已超过设定时间。请打开助手回答问题。")
                    : L.T("该任务等待授权已超过设定时间。请打开助手确认；不会自动批准操作。"), true, [new("agent", L.T("打开助手"), agent.Owner)]);
            else if (agent.State == AgentRunState.Error)
                Put(frame, "agent-error", id, current.Label + L.T(" · 需要检查"), L.T("助手报告错误或任务中断，请打开原应用查看原因。"), true, [new("agent", L.T("打开助手"), agent.Owner)]);
        }
        if (_endedAgents.Count > 0 && (frame.Now - _endedAgents[^1].When >= TimeSpan.FromSeconds(3) || frame.Now - _endedAgents[0].When >= TimeSpan.FromSeconds(10)))
        {
            var batch = _endedAgents.ToArray(); _endedAgents.Clear();
            if (_options.Enabled && _options.AgentFollowup)
            {
                var actions = batch.DistinctBy(a => a.Owner).Take(3).Select(a => new AssistantNoticeAction("agent", L.T("打开 ") + a.Label[..Math.Min(15, a.Label.Length)], a.Owner)).ToArray();
                Put(frame, "agent-ended", batch[0].When.UtcTicks.ToString(), L.F($"{batch.Length} 个助手任务已结束"),
                    string.Join("\n", batch.Take(8).Select(a => a.Label + (a.Failed ? L.T(" · 曾报告错误，请检查结果") : L.T(" · 已收到结束信号")))), false, actions, once: true);
            }
        }
    }
    private void ResolveAgent(string owner, long id, string result)
    { string key = Digest(owner) + ":" + id; Resolve("assistant:agent-wait:" + key, result); Resolve("assistant:agent-error:" + key, result); }
    private void ObserveDownloads(AssistantFrame frame)
    {
        var live = frame.Downloads.Take(64).ToDictionary(d => d.Id, StringComparer.Ordinal);
        if (frame.Now - _firstObservation >= TimeSpan.FromSeconds(30))
            foreach (var notice in frame.Notifications.Where(n => n.Reminder is { Rule: "download", Pending: true }))
                if (!live.Values.Any(d => notice.Key.EndsWith(":" + d.Id, StringComparison.Ordinal) && d.Measuring))
                    Resolve(notice.Key, L.T("当前任务不再处于可跟踪的下载状态"));
        foreach (string id in _downloadProgress.Keys.Where(k => !live.ContainsKey(k)).ToArray())
        { _downloadProgress.Remove(id); Resolve("assistant:download:" + id, L.T("当前下载已离开可跟踪列表")); }
        foreach (var task in live.Values)
        {
            _downloadProgress.TryGetValue(task.Id, out var previous);
            if (task.Completed && previous is { SeenRunning: true, Ended: false })
            { if (_gameName != null) _gameDownloads++; if (_quiet) _quietDownloads++; }
            bool reset = previous == null || !task.Measuring || !previous.Measuring || task.Bytes != previous.Bytes;
            var current = new Progress(task.Bytes, reset ? frame.Now : previous!.Since, task.Measuring,
                task.Measuring || previous?.SeenRunning == true, task.Completed);
            _downloadProgress[task.Id] = current;
            if (reset || task.Completed) Resolve("assistant:download:" + task.Id, task.Completed ? L.T("下载已完成") : L.T("进度或状态已变化"));
            if (_options.Enabled && _options.DownloadStall && task.Measuring && !task.Completed
                && frame.Now - current.Since >= TimeSpan.FromSeconds(Math.Clamp(_options.DownloadStallSeconds, 30, 900)))
                Put(frame, "download", task.Id, L.T("下载进度暂未变化"), Clean(task.Label) + L.F($"\n可信进度已有 {Duration((frame.Now - current.Since).TotalSeconds)} 未变化；请在下载详情中确认。"),
                    true, [new("downloads", L.T("查看下载"))]);
        }
    }
    internal static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..20].ToLowerInvariant();
}
