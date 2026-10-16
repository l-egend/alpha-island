using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private sealed class TurnHistoryFixture : IDisposable
    {
        private IntPtr _db;
        internal TurnHistoryFixture(string home)
        {
            Directory.CreateDirectory(home);
            Check(FixtureOpen(Path.Combine(home, "thread_history_1.sqlite"), out _db) == 0, "创建隔离的原生任务历史数据库");
            Exec("CREATE TABLE thread_turns(thread_id TEXT, turn_id TEXT, status TEXT, error_json TEXT, completed_at INTEGER, PRIMARY KEY(thread_id,turn_id));");
        }
        internal void Exec(string sql) => Check(FixtureExec(_db, sql, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == 0, "写入脱敏原生任务状态样本");
        public void Dispose() { if (_db != IntPtr.Zero) FixtureClose(_db); _db = IntPtr.Zero; }
        [DllImport("winsqlite3.dll", EntryPoint="sqlite3_open", CallingConvention=CallingConvention.Cdecl)]
        private static extern int FixtureOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db);
        [DllImport("winsqlite3.dll", EntryPoint="sqlite3_exec", CallingConvention=CallingConvention.Cdecl)]
        private static extern int FixtureExec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr data, IntPtr error);
        [DllImport("winsqlite3.dll", EntryPoint="sqlite3_close", CallingConvention=CallingConvention.Cdecl)]
        private static extern int FixtureClose(IntPtr db);
    }
    private static void CodexTerminalBoundaryChecks(TurnHistoryFixture fixture)
    {
        using var registryLifetime = new RegistryCleanup();
        var registry = registryLifetime.Registry;
        var history = new CodexTurnHistory();
        DateTime now = DateTime.UtcNow;
        void Report(string session, string thread, string turn, string state = "thinking", bool begins = true, string? parent = null)
            => Check(registry.Update("codex", session, state, "Codex", 300, now, out _, parentSession: parent,
                beginsActivity: begins, nativeThreadId: thread, nativeTurnId: turn), "登记任务边界样本 " + session + "/" + turn);
        bool Active(string session) => AgentStatus.Snapshot.Any(a => a.Owner == "external:codex/" + session);
        fixture.Exec("INSERT INTO thread_turns VALUES('parallel-thread','parallel-turn','inProgress',NULL,NULL),('retry-thread','retry-turn','inProgress','{\"codexErrorInfo\":\"usageLimitExceeded\"}',NULL);");
        Report("quota-session", "quota-thread", "quota-turn");
        Report("parallel-session", "parallel-thread", "parallel-turn");
        Report("retry-session", "retry-thread", "retry-turn");
        history.Reconcile(registry, now);
        Check(!Active("quota-session") && Active("parallel-session") && Active("retry-session"), "只结束已确认失败的对应任务，其他会话和可重试任务继续工作");
        Report("quota-session", "quota-thread", "quota-turn", "working", false);
        Report("quota-session", "quota-thread", "quota-turn");
        Check(!Active("quota-session"), "额度失败后的迟到工具事件和重复提交不能复活旧任务");
        fixture.Exec("INSERT INTO thread_turns VALUES('quota-thread','new-turn','inProgress',NULL,NULL);");
        Report("quota-session", "quota-thread", "new-turn");
        history.Reconcile(registry, now);
        Check(Active("quota-session"), "恢复额度后的新一轮任务不会被旧限额记录结束");
        var old = registry.Snapshot.Single(c => c.Session == "quota-session");
        Report("quota-session", "quota-thread", "newer-turn");
        Check(!registry.FinishNativeTurn(old, now) && Active("quota-session"), "历史查询与新任务同时发生时，旧结果不能清除新任务");
        Report("quota-session", "quota-thread", "new-turn", "working", false);
        Check(registry.Snapshot.Single(c => c.Session == "quota-session").NativeTurnId == "newer-turn", "旧轮迟到心跳不能覆盖当前原生任务标识");
        Report("quota-session", "quota-thread", "new-turn", "idle", false);
        Check(Active("quota-session") && registry.Snapshot.Single(c => c.Session == "quota-session").NativeTurnId == "newer-turn", "旧轮迟到的结束钩子不能收回当前新任务");
        Check(registry.Update("codex", "quota-session", "thinking", "", 300, now, out _, beginsActivity:true), "兼容未提供原生任务标识的提交事件");
        Check(registry.Snapshot.Single(c => c.Session == "quota-session").NativeTurnId.Length == 0, "新提交缺少任务 ID 时清除旧 ID，避免误读旧的结束记录");
        fixture.Exec("INSERT INTO thread_turns VALUES('parent-thread','parent-turn','inProgress',NULL,NULL),('child-thread','child-turn','completed',NULL,1);");
        Report("parent-session", "parent-thread", "parent-turn");
        Report("child-session", "child-thread", "child-turn", parent:"parent-session");
        history.Reconcile(registry, now);
        Check(Active("parent-session") && !Active("child-session"), "子智能体结束不收回仍在工作的主会话");
        fixture.Exec("UPDATE thread_turns SET status='failed',completed_at=1 WHERE thread_id='parent-thread';");
        history.Reconcile(registry, now);
        Check(!Active("parent-session"), "主会话失败后正确结束聚合的助手岛");
        foreach (string state in new[] { "completed", "interrupted", "failed" })
        {
            fixture.Exec($"INSERT INTO thread_turns VALUES('end-{state}','turn','{state}',NULL,1);");
            Report("end-"+state, "end-"+state, "turn"); history.Reconcile(registry, now);
            Check(!Active("end-"+state), "完成、取消或终止失败均有缺失结束钩子的兜底："+state);
        }
        Report("unknown-session", "unknown-thread", "unknown-turn"); history.Reconcile(registry, now);
        Check(Active("unknown-session"), "没有对应历史记录时保留正常钩子状态，不按静默时长猜测结束");
        fixture.Exec("INSERT INTO thread_turns VALUES('projection-thread','projected-turn','completed',NULL,1);");
        Report("projected-session", "original-rollout-thread", "projected-turn");
        history.Reconcile(registry, now);
        Check(!Active("projected-session"), "桌面任务的数据库线程 ID 与历史文件头不同时，完成任务仍停止思考");
        Report("late-root", "late-thread", "late-turn");
        Report("late-root", "late-thread", "late-turn", "idle", false);
        Report("late-root", "late-thread", "late-turn", "working", false);
        Check(!Active("late-root"), "正常结束后的迟到工具事件不能复活已完成任务");
        Report("late-root", "late-thread", "late-turn");
        Check(!Active("late-root"), "已完成任务的重复提交事件也不会复活旧任务");
        Report("late-root", "late-thread", "next-late-turn");
        Check(Active("late-root"), "已完成会话的新一轮任务仍正常开始");
        fixture.Exec("INSERT INTO thread_turns VALUES('collision-a','ambiguous-turn','completed',NULL,1),('collision-b','ambiguous-turn','completed',NULL,1);");
        Report("ambiguous-session", "unknown-collision-thread", "ambiguous-turn");history.Reconcile(registry,now);
        Check(Active("ambiguous-session"), "原生任务 ID 不唯一时不猜测完成，避免误收回其他任务");
        Report("exact-collision-session", "collision-a", "ambiguous-turn");history.Reconcile(registry,now);
        Check(!Active("exact-collision-session"), "有精确线程匹配时仍可安全识别完成");
        fixture.Exec("INSERT INTO thread_turns VALUES('resolved-execution','mapped-live-turn','inProgress',NULL,NULL);");
        Report("mapped-live-session", "old-header-thread", "mapped-live-turn");history.Reconcile(registry,now);
        Check(Active("mapped-live-session"), "线程映射只缓存身份，不把运行状态缓存为终态");
        fixture.Exec("UPDATE thread_turns SET status='completed',completed_at=1 WHERE thread_id='resolved-execution';");
        history.Reconcile(registry,now);
        Check(!Active("mapped-live-session"), "已映射的执行线程完成后及时收回，不保留旧的运行状态");
        string json = JsonSerializer.Serialize(registry.Snapshot);
        Check(!json.Contains("NativeThreadId") && !json.Contains("nativeThreadId") && !json.Contains("quota-thread"), "原生任务 ID 不暴露到连接查询或诊断序列化");
        fixture.Exec("DROP TABLE thread_turns;"); history.Reconcile(registry, now);
        Check(Active("parallel-session") && Active("unknown-session"), "旧版 Codex 缺少投影表时正常降级，不误结束活跃对话");
    }
    private sealed class RegistryCleanup : IDisposable
    {
        internal ExternalAgentRegistry Registry { get; } = new();
        public void Dispose() => Registry.Close();
    }
    private static async Task CodexTerminalChecks(string root)
    {
        string? oldHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        string home = Path.Combine(root, "codex-home");
        using var fixture = new TurnHistoryFixture(home);
        fixture.Exec("INSERT INTO thread_turns VALUES('quota-thread','quota-turn','inProgress',NULL,NULL);");
        Environment.SetEnvironmentVariable("CODEX_HOME", home);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var bridge = new AgentBridgeService(port, _ => false);
        try
        {
            bridge.Start();
            using var http = new HttpClient { BaseAddress = new Uri(bridge.Endpoint) };
            using var reply = await http.PostAsync("status", new StringContent(JsonSerializer.Serialize(new {
                source="codex", session="quota-session", state="thinking", beginsActivity=true, ttlSec=300,
                nativeThreadId="quota-thread", nativeTurnId="quota-turn"
            }), Encoding.UTF8, "application/json"));
            Check(reply.IsSuccessStatusCode && AgentStatus.Snapshot.Any(a => a.Owner=="external:codex/quota-session"), "新任务经真实 HTTP 进入思考状态");
            fixture.Exec("UPDATE thread_turns SET status='failed',error_json='{\"codexErrorInfo\":\"usageLimitExceeded\"}',completed_at=1792118736;");
            for (int i=0; i<35 && AgentStatus.Snapshot.Any(a => a.Owner=="external:codex/quota-session"); i++) await Task.Delay(100);
            Check(!AgentStatus.Snapshot.Any(a => a.Owner=="external:codex/quota-session"), "原生任务因账户额度耗尽失败后，岛停止显示思考");
        }
        finally { bridge.Stop(); Environment.SetEnvironmentVariable("CODEX_HOME", oldHome); }
        Environment.SetEnvironmentVariable("CODEX_HOME", home);
        try { CodexTerminalBoundaryChecks(fixture); }
        finally { Environment.SetEnvironmentVariable("CODEX_HOME", oldHome); }
    }
}