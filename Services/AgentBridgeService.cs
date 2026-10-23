using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using alpha_island.Core;

// agent 工具权限审批的本地桥接。
//
// 为什么不用 HttpListener：前缀 `http://127.0.0.1:port/` 需要 URL ACL 保留（普通用户跑要 netsh 或提权），
// 而本项目"除全系统内存整理外全部免提权"。所以这里自己用 TcpListener 绑回环 + 手写最小 HTTP/1.1 服务端：
// 免提权、依赖为零；状态心跳与审批请求共用回环端口。
//
// 协议（也写在 docs/agent-bridge.md）：
//   GET  /health                        → 200 {ok,service,version,port,pending}
//   POST /approval                      → 阻塞直到用户裁决或超时，返回裁决结果
//         {id?, tool, args?|command?, reason?, timeoutSec?}   args 可以是任意 JSON（原样保留）
//   GET  /approval/{id}                 → 200 已裁决 | 202 {status:"pending"}
//   DELETE /approval/{id}              → 撤回请求（只会拒绝，不能放行）
//   GET /status / POST /status          → 查询连接 / 上报有期限的状态心跳
//   POST /approval/{id}                → 桌面程序禁止 API 自行裁决
//
// 安全边界：只绑 IPAddress.Loopback，并且每条连接都再验一次远端地址是回环；默认关闭（设置页开关）。
namespace alpha_island.Services
{
    /// <summary>一次工具权限审批请求（由外部 agent 通过回环 HTTP 发起）。</summary>
    public sealed record ApprovalRequest(
        string Id,
        string Tool,
        string Args,
        string Reason,
        int TimeoutSec,
        DateTime RequestedUtc);

    /// <summary>一次审批的裁决结果。</summary>
    public sealed record ApprovalDecision(
        string Id,
        bool Approved,
        string DecidedBy,
        string Note,
        bool TimedOut,
        DateTime DecidedUtc);

    /// <summary>
    /// agent 桥接服务本体：只做"收请求 → 交给界面 → 把裁决回传"，
    /// 不碰任何 WPF 类型（界面那一侧在 MainWindow.AgentBridge.cs）。
    /// </summary>
    public sealed class AgentBridgeService : IDisposable
    {
        public const string ServiceName = "alpha-island-agent-bridge";
        public const string Version = "0.2";
        public const int DefaultPort = 8765;
        public const int MinTimeoutSec = 5;
        public const int MaxTimeoutSec = 600;
        public const int DefaultTimeoutSec = 120;

        /// <summary>已裁决的请求留一小段缓存，好让 GET /approval/{id} 在裁决后仍能查到结果。</summary>
        private const int RecentCapacity = 32;
        private static readonly TimeSpan RecentTtl = TimeSpan.FromMinutes(5);

        private sealed class PendingApproval
        {
            public PendingApproval(ApprovalRequest request) { Request = request; }
            public ApprovalRequest Request { get; }
            public TaskCompletionSource<ApprovalDecision> Tcs { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly int _port;
        private readonly Func<ApprovalRequest, bool> _onRequest;
        private readonly bool _allowApiDecisions;
        private readonly ConcurrentDictionary<string, PendingApproval> _pending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ApprovalDecision> _recent = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTime> _recentAt = new(StringComparer.Ordinal);
        private readonly List<TcpClient> _clients = new();
        private readonly object _gate = new();
        private readonly object _decisionGate = new();

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _acceptTask;
        private System.Threading.Timer? _statusTimer;
        private readonly ExternalAgentRegistry _externalAgents = new();
        private readonly CodexTurnHistory _codexTurns = new();
        private readonly CodexStatusJournal _codexStatusJournal;
        private int _sweepRunning;
        internal string StatusDiagnostics => _externalAgents.DiagnosticsText();

        /// <param name="port">回环端口（调用方已经 clamp 过）。</param>
        /// <param name="onRequest">
        /// 把请求交给界面：返回 true = 界面上真的弹出来给用户裁决了（桥接会等结果）；
        /// 返回 false = 界面没接（审批开关关闭等），桥接立刻按"拒绝"回传，绝不让 agent 干等满超时。
        /// 该回调可能被网络线程调用，实现方自己切 UI 线程。
        /// </param>
        public AgentBridgeService(int port, Func<ApprovalRequest, bool> onRequest, bool allowApiDecisions = false)
            : this(port, onRequest, allowApiDecisions, null) { }

        internal AgentBridgeService(int port, Func<ApprovalRequest, bool> onRequest, bool allowApiDecisions, CodexStatusJournal? journal)
        {
            _port = port;
            _onRequest = onRequest;
            _allowApiDecisions = allowApiDecisions;
            _codexStatusJournal = journal ?? new(Endpoint);
        }

        public int Port => _port;
        public bool IsRunning => _listener != null;
        public string Endpoint => "http://127.0.0.1:" + _port.ToString(CultureInfo.InvariantCulture) + "/";
        public int PendingCount => _pending.Count;

        /// <summary>
        /// 每有一条"已经交给界面"的请求被裁决就触发一次，触发时机覆盖全部路径：
        /// 界面点的（Resolve）、外部用 API 裁的（Resolve）、等到超时的（自造拒绝）。
        /// 宿主靠它做界面收尾与流水记账 —— 否则 API 裁决那一侧会留下永远弹着的审批层。
        /// </summary>
        public event Action<ApprovalDecision>? Decided;

        // ================= 生命周期 =================

        /// <summary>绑定回环端口并开始收请求；端口被占用等失败会抛（宿主会把它标成 Degraded）。</summary>
        public void Start()
        {
            if (_listener != null) return;

            var listener = new TcpListener(IPAddress.Loopback, _port);
            listener.Start();
            _listener = listener;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _externalAgents.Open();
            _acceptTask = Task.Run(() => AcceptLoopAsync(listener, token));
            _statusTimer = new System.Threading.Timer(_ => SweepAgentStatus(), null, 1000, 1000);

            IslandLog.Info("AgentBridge", $"桥接已监听 {Endpoint}（仅回环；等待工具权限审批请求）");
        }

        private void SweepAgentStatus()
        {
            if (Interlocked.CompareExchange(ref _sweepRunning, 1, 0) != 0) return;
            try { var now = DateTime.UtcNow; _codexStatusJournal.Drain(_externalAgents, now); _codexTurns.Reconcile(_externalAgents, now); _externalAgents.Sweep(now); }
            finally { Volatile.Write(ref _sweepRunning, 0); }
        }
        /// <summary>停掉监听；所有还在等待的请求立刻按"拒绝：桥接已关闭"回传。</summary>
        public void Stop()
        {
            var listener = _listener;
            _listener = null;
            _externalAgents.Close();
            _statusTimer?.Dispose();
            _statusTimer = null;

            try { _cts?.Cancel(); } catch { /* 停的时候取消失败无所谓 */ }
            try { listener?.Stop(); } catch { }

            lock (_gate)
            {
                foreach (var client in _clients)
                {
                    try { client.Dispose(); } catch { }
                }
                _clients.Clear();
            }

            int abandoned = _pending.Count;
            ResolveAll(false, "bridge", "桥接已关闭");

            try { _cts?.Dispose(); } catch { }
            _cts = null;
            _acceptTask = null;

            if (abandoned > 0)
                IslandLog.Warn("AgentBridge", $"桥接停止，{abandoned} 个未裁决请求按拒绝收尾");
            else
                IslandLog.Info("AgentBridge", "桥接已停止");
        }

        public void Dispose() => Stop();

        // ================= 裁决入口（界面/API 都走这里） =================

        /// <summary>裁决一个待定请求；返回 false 表示 id 不存在或已被裁决。</summary>
        public bool Resolve(string id, bool approved, string decidedBy, string note)
        {
            if (string.IsNullOrEmpty(id)) return false;
            var decision = new ApprovalDecision(
                id, approved, decidedBy ?? "", note ?? "", false, DateTime.UtcNow);
            return CompleteDecision(decision);
        }

        private bool CompleteDecision(ApprovalDecision decision)
        {
            lock (_decisionGate)
            {
                if (!_pending.TryRemove(decision.Id, out var entry)) return false;
                Remember(decision);
                entry.Tcs.TrySetResult(decision);
            }
            RaiseDecided(decision);
            return true;
        }

        public bool IsPending(string id) => _pending.ContainsKey(id);

        public bool TryGetDecision(string id, out ApprovalDecision? decision)
        {
            bool found = _recent.TryGetValue(id, out var known);
            decision = known;
            return found;
        }

        /// <summary>把裁决结果通知宿主；宿主那边出问题不能连累网络线程。</summary>
        private void RaiseDecided(ApprovalDecision decision)
        {
            var handler = Decided;
            if (handler == null) return;

            try { handler(decision); }
            catch (Exception ex) { IslandLog.Warn("AgentBridge", "裁决回调失败: " + ex.Message); }
        }

        /// <summary>当前待裁决的请求（诊断/日志用）。</summary>
        public IReadOnlyList<ApprovalRequest> PendingRequests() =>
            _pending.Values.Select(p => p.Request).ToList();

        private void ResolveAll(bool approved, string decidedBy, string note)
        {
            foreach (var id in _pending.Keys.ToList())
            {
                Resolve(id, approved, decidedBy, note);
            }
        }

        private void Remember(ApprovalDecision decision)
        {
            _recent[decision.Id] = decision;
            _recentAt[decision.Id] = DateTime.UtcNow;

            // 只留最近 N 条 / TTL 内的，别让它无限长。
            var now = DateTime.UtcNow;
            foreach (var key in _recentAt.Where(kv => now - kv.Value > RecentTtl).Select(kv => kv.Key).ToList())
            {
                _recentAt.TryRemove(key, out _);
                _recent.TryRemove(key, out _);
            }

            while (_recent.Count > RecentCapacity)
            {
                var oldest = _recentAt.OrderBy(kv => kv.Value).FirstOrDefault().Key;
                if (oldest == null) break;
                _recentAt.TryRemove(oldest, out _);
                _recent.TryRemove(oldest, out _);
            }
        }

        // ================= 网络循环 =================

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException ex)
                {
                    if (ct.IsCancellationRequested) return;
                    IslandLog.Warn("AgentBridge", "接受连接失败: " + ex.Message);
                    await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) return;
                    IslandLog.Error("AgentBridge:accept", ex);
                    continue;
                }

                Track(client);
                _ = Task.Run(() => HandleClientAsync(client, ct));
            }
        }

        private void Track(TcpClient client)
        {
            lock (_gate)
            {
                _clients.RemoveAll(c => !SafeConnected(c));
                _clients.Add(client);
            }
        }

        private static bool SafeConnected(TcpClient client)
        {
            try { return client.Connected; } catch { return false; }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                using (client)
                {
                    client.NoDelay = true;
                    // 纵深防御：只认回环来的连接（绑定已经只绑回环了，这里再挡一层）。
                    if (client.Client.RemoteEndPoint is IPEndPoint remote && !IPAddress.IsLoopback(remote.Address))
                    {
                        IslandLog.Warn("AgentBridge", $"拒绝非回环连接：{remote.Address}");
                        return;
                    }

                    var stream = client.GetStream();
                    using var readLimit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readLimit.CancelAfter(TimeSpan.FromSeconds(15));
                    var (method, path, body) = await ReadRequestAsync(stream, readLimit.Token).ConfigureAwait(false);
                    var (status, json) = await RouteAsync(method, path, body, ct).ConfigureAwait(false);
                    await WriteResponseAsync(stream, status, json, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* 停服 */ }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;
                IslandLog.Warn("AgentBridge", "处理请求失败: " + ex.Message);
            }
            finally
            {
                lock (_gate) { _clients.Remove(client); }
            }
        }

        private static async Task<(string Method, string Path, string Body)> ReadRequestAsync(
            NetworkStream stream, CancellationToken ct)
        {
            var head = new MemoryStream();
            var buffer = new byte[4096];
            int headerEnd = -1;

            while (headerEnd < 0)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read <= 0) break;
                head.Write(buffer, 0, read);

                var bytes = head.GetBuffer();
                headerEnd = FindHeaderEnd(bytes, (int)head.Length);

                if (headerEnd < 0 && head.Length > 64 * 1024)
                    throw new InvalidDataException("请求头超过 64KB");
            }

            if (headerEnd < 0) return ("", "", "");

            var bytesAll = head.GetBuffer();
            int total = (int)head.Length;
            string headerText = Encoding.UTF8.GetString(bytesAll, 0, headerEnd);
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var requestLine = lines.Length > 0 ? lines[0].Split(' ') : Array.Empty<string>();
            string method = requestLine.Length > 0 ? requestLine[0].ToUpperInvariant() : "";
            string path = requestLine.Length > 1 ? requestLine[1] : "/";

            int contentLength = 0;
            foreach (var line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                if (line.Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(line.Substring(colon + 1).Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out contentLength);
                }
            }

            int bodyStart = headerEnd + 4;
            if (contentLength < 0 || contentLength > 1024 * 1024)
                throw new InvalidDataException("请求体必须小于 1MB");
            var bodyBytes = new List<byte>(Math.Max(contentLength, 0));
            for (int i = bodyStart; i < total && bodyBytes.Count < contentLength; i++)
                bodyBytes.Add(bytesAll[i]);

            while (bodyBytes.Count < contentLength)
            {
                int need = Math.Min(buffer.Length, contentLength - bodyBytes.Count);
                int read = await stream.ReadAsync(buffer.AsMemory(0, need), ct).ConfigureAwait(false);
                if (read <= 0) break;
                for (int i = 0; i < read; i++) bodyBytes.Add(buffer[i]);
            }

            string body = bodyBytes.Count == 0 ? "" : Encoding.UTF8.GetString(bodyBytes.ToArray());
            if (bodyBytes.Count != contentLength) throw new InvalidDataException("请求体不完整");
            return (method, path, body);
        }

        /// <summary>找 "\r\n\r\n"，返回第一个 \r 的下标；没找到返回 -1。</summary>
        private static int FindHeaderEnd(byte[] bytes, int length)
        {
            for (int i = 0; i + 3 < length; i++)
            {
                if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10)
                    return i;
            }
            return -1;
        }

        private static async Task WriteResponseAsync(NetworkStream stream, int status, string json, CancellationToken ct)
        {
            string reason = status switch
            {
                200 => "OK",
                202 => "Accepted",
                400 => "Bad Request",
                404 => "Not Found",
                405 => "Method Not Allowed",
                403 => "Forbidden",
                _ => "Error",
            };

            var payload = Encoding.UTF8.GetBytes(json ?? "{}");
            string head = $"HTTP/1.1 {status} {reason}\r\n"
                        + "Content-Type: application/json; charset=utf-8\r\n"
                        + $"Content-Length: {payload.Length}\r\n"
                        + "Connection: close\r\n"
                        + "Cache-Control: no-store\r\n\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        // ================= 路由 =================

        private async Task<(int Status, string Json)> RouteAsync(string method, string path, string body, CancellationToken ct)
        {
            string route = (path ?? "/").Split('?')[0];
            if (route.Length > 1 && route.EndsWith("/", StringComparison.Ordinal))
                route = route.TrimEnd('/');

            if (method == "GET" && (route == "/health" || route == "/"))
            {
                return (200, Json(new
                {
                    ok = true,
                    service = ServiceName,
                    version = Version,
                    port = _port,
                    pending = _pending.Count,
                    statusReporting = true,
                    apiDecisions = _allowApiDecisions,
                    usage = new[]
                    {
                        "POST /approval  {tool, args?, reason?, timeoutSec?, id?}",
                        "GET  /approval/{id}",
                        "DELETE /approval/{id} (cancel only)",
                        "GET /status; POST /status {source, session?, state, detail?, ttlSec?}",
                    },
                }));
            }

            if (route == "/status")
            {
                _externalAgents.Sweep(DateTime.UtcNow);
                if (method == "GET") return (200, Json(new { connections = _externalAgents.Snapshot }));
                if (method != "POST") return (405, Json(new { error = "只支持 GET / POST /status" }));
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    var session = ReadString(root, "session");
                    if (string.IsNullOrEmpty(session)) session = "default";
                    int ttl = 30;
                    if (root.TryGetProperty("ttlSec", out var t) && t.TryGetInt32(out var parsed)) ttl = parsed;
                    string source = ReadString(root, "source");
                    int? processId = null;
                    if (root.TryGetProperty("processId", out var process))
                    {
                        if (!process.TryGetInt32(out int pid) || pid <= 0) return (400, Json(new { error = "processId 必须为正整数" }));
                        processId = pid;
                    }
                    else if (root.TryGetProperty("reporterPid", out var reporter))
                    {
                        if (!reporter.TryGetInt32(out int pid) || pid <= 0) return (400, Json(new { error = "reporterPid 必须为正整数" }));
                        processId = AgentHostProcessMonitor.Resolve(source, pid);
                    }
                    bool connectOnly = root.TryGetProperty("connectOnly", out var connection) && connection.ValueKind == JsonValueKind.True;
                    string state = ReadString(root, "state");
                    if (connectOnly && state != "idle") return (400, Json(new { error = "连接登记不能同时修改任务状态" }));
                    string? family = ReadString(root, "artworkFamily"), parent = ReadString(root, "parentSession"), alias = ReadString(root, "sessionAlias");
                    string error = "";
                    bool ApplyStatus()
                    {
                        DateTime received = DateTime.UtcNow, reported = received;
                        if (source == "codex" && root.TryGetProperty("reportedAtUnixMs", out var sent) && sent.TryGetInt64(out long ms))
                            try { reported = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime; } catch (ArgumentOutOfRangeException) { }
                        string thread = ReadString(root, "nativeThreadId"), turn = ReadString(root, "nativeTurnId");
                        if (source == "codex" && !connectOnly && state != "idle"
                            && _codexTurns.IgnoreCompletedReport(_externalAgents, thread, turn, reported, received)) return true;
                        return connectOnly
                        ? _externalAgents.Connect(source, session, ttl, received, out error, processId, family, parent, alias)
                        : _externalAgents.Update(source, session, state, ReadString(root, "detail"), ttl, received,
                            out error, processId, family, parent, alias, root.TryGetProperty("beginsActivity", out var begins) && begins.ValueKind == JsonValueKind.True,
                            thread, turn, activityStartedUtc: reported);
                    }
                    // Recover an earlier missed start before accepting this turn's tool update.
                    if (source == "codex" && !connectOnly) _codexStatusJournal.Drain(_externalAgents, DateTime.UtcNow);
                    bool accepted = _codexStatusJournal.Deliver(root, DateTime.UtcNow, ApplyStatus);
                    if (!accepted) return (400, Json(new { error }));
                    return (200, Json(new { ok = true }));
                }
                catch (Exception) { return (400, Json(new { error = "状态请求必须为合法 JSON 对象" })); }
            }

            if (route == "/approval")
            {
                if (method != "POST") return (405, Json(new { error = "只支持 POST /approval" }));

                ApprovalRequest request;
                try
                {
                    request = ParseApprovalRequest(body);
                }
                catch (Exception ex)
                {
                    return (400, Json(new { error = "请求体不是合法 JSON: " + ex.Message }));
                }

                if (string.IsNullOrWhiteSpace(request.Tool))
                    return (400, Json(new { error = "缺少 tool 字段" }));

                var entry = new PendingApproval(request);
                lock (_decisionGate)
                {
                    if (_recent.ContainsKey(request.Id) || !_pending.TryAdd(request.Id, entry))
                        return (400, Json(new { error = "id 已存在: " + request.Id }));
                }

                bool offered = false;
                try { offered = _onRequest(request); }
                catch (Exception ex) { IslandLog.Warn("AgentBridge", "审批回调失败: " + ex.Message); }

                if (!offered)
                {
                    var denied = new ApprovalDecision(request.Id, false, "bridge", "岛上审批界面未开启，按拒绝处理", false, DateTime.UtcNow);
                    CompleteDecision(denied);
                    IslandLog.Info("AgentBridge", $"审批 {request.Id}（{request.Tool}）无人接收 → 直接拒绝");
                    return (200, Json(DecisionJson(await entry.Tcs.Task.ConfigureAwait(false))));
                }

                IslandLog.Info("AgentBridge", $"审批 {request.Id} 等待裁决（tool={request.Tool}，超时 {request.TimeoutSec}s）");

                int timeoutSec = Math.Clamp(request.TimeoutSec, MinTimeoutSec, MaxTimeoutSec);
                await Task.WhenAny(
                    entry.Tcs.Task,
                    Task.Delay(TimeSpan.FromSeconds(timeoutSec), ct)).ConfigureAwait(false);

                if (!entry.Tcs.Task.IsCompleted)
                {
                    // 超时：自己造一条拒绝结果投进去，并把它记进缓存（界面那一侧也会收到同样的裁决）。
                    var timedOut = new ApprovalDecision(request.Id, false, "timeout",
                        $"等待 {timeoutSec}s 未裁决，按拒绝处理", true, DateTime.UtcNow);
                    if (CompleteDecision(timedOut))
                        IslandLog.Info("AgentBridge", $"审批 {request.Id} 超时 → 拒绝");
                }

                var decision = await entry.Tcs.Task.ConfigureAwait(false);
                return (200, Json(DecisionJson(decision)));
            }

            if (route.StartsWith("/approval/", StringComparison.Ordinal))
            {
                string id = route.Substring("/approval/".Length);

                if (method == "DELETE")
                {
                    if (!Resolve(id, false, "client", "请求方已撤回"))
                        return (404, Json(new { id, error = "没有这个待裁决请求" }));
                    return (200, Json(new { id, approved = false, status = "cancelled" }));
                }

                if (method == "GET")
                {
                    if (_pending.ContainsKey(id))
                        return (202, Json(new { id, status = "pending" }));
                    if (_recent.TryGetValue(id, out var known))
                        return (200, Json(DecisionJson(known)));

                    return (404, Json(new { id, error = "没有这个审批请求（可能已过期）" }));
                }

                if (method == "POST")
                {
                    if (!_allowApiDecisions)
                        return (403, Json(new { id, error = "API 裁决已关闭，请在岛上允许或拒绝。" }));
                    bool approved = false;
                    string note = "";
                    try
                    {
                        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("approved", out var a) &&
                            (a.ValueKind == JsonValueKind.True || a.ValueKind == JsonValueKind.False))
                            approved = a.GetBoolean();
                        if (root.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String)
                            note = n.GetString() ?? "";
                    }
                    catch (Exception ex)
                    {
                        return (400, Json(new { error = "请求体不是合法 JSON: " + ex.Message }));
                    }

                    if (!Resolve(id, approved, "api", string.IsNullOrEmpty(note) ? (approved ? "API 允许" : "API 拒绝") : note))
                        return (404, Json(new { id, error = "没有这个待裁决请求" }));

                    _recent.TryGetValue(id, out var decided);
                    return (200, Json(DecisionJson(decided ?? new ApprovalDecision(id, approved, "api", note, false, DateTime.UtcNow))));
                }

                return (405, Json(new { error = "只支持 GET / POST" }));
            }

            return (404, Json(new { error = "未知路由: " + route }));
        }

        private ApprovalRequest ParseApprovalRequest(string body)
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;

            string id = ReadString(root, "id");
            if (string.IsNullOrWhiteSpace(id)) id = Guid.NewGuid().ToString("N");

            string tool = ReadString(root, "tool");
            if (string.IsNullOrWhiteSpace(tool)) tool = ReadString(root, "name");

            string reason = ReadString(root, "reason");

            string args = "";
            if (root.TryGetProperty("args", out var argsElement))
                args = argsElement.ValueKind == JsonValueKind.String ? argsElement.GetString() ?? "" : argsElement.GetRawText();
            else if (root.TryGetProperty("command", out var cmdElement))
                args = cmdElement.ValueKind == JsonValueKind.String ? cmdElement.GetString() ?? "" : cmdElement.GetRawText();

            int timeout = DefaultTimeoutSec;
            if (root.TryGetProperty("timeoutSec", out var t) && t.ValueKind == JsonValueKind.Number &&
                t.TryGetInt32(out int parsed)) timeout = parsed;

            return new ApprovalRequest(id, tool, args, reason, Math.Clamp(timeout, MinTimeoutSec, MaxTimeoutSec), DateTime.UtcNow);
        }

        private static string ReadString(JsonElement root, string name)
        {
            if (root.ValueKind != JsonValueKind.Object) return "";
            if (!root.TryGetProperty(name, out var element)) return "";
            return element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.ToString();
        }

        private static object DecisionJson(ApprovalDecision d) => new
        {
            id = d.Id,
            approved = d.Approved,
            status = "decided",
            decidedBy = d.DecidedBy,
            note = d.Note,
            timedOut = d.TimedOut,
            decidedAtUtc = d.DecidedUtc.ToString("o", CultureInfo.InvariantCulture),
        };

        private static string Json(object value) =>
            JsonSerializer.Serialize(value, JsonOptions);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // 只在本机回环上跑，中文按原样回（否则 note 会变成 \uXXXX，人看 curl 输出很累）。
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
    }
}
