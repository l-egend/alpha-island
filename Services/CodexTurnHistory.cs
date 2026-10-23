using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using alpha_island.Core;

namespace alpha_island.Services;

/// <summary>Read only the terminal status of an exact native turn. No messages, log text or account credentials are read.</summary>
internal sealed class CodexTurnHistory
{
    private readonly Dictionary<(string Thread, string Turn), string> _resolvedThreads = new();
    internal bool IgnoreCompletedReport(ExternalAgentRegistry registry, string thread, string turn, DateTime reportedUtc, DateTime receivedUtc)
    {
        if (!IsCompletedReport(thread, turn, reportedUtc)) return false;
        foreach (var active in registry.Snapshot.Where(c => c.Source == "codex" && c.NativeThreadId == thread
            && (turn.Length > 0 ? c.NativeTurnId == turn : c.NativeTurnId.Length == 0 && c.ActivityStartedUtc <= reportedUtc)))
            registry.FinishNativeTurn(active, receivedUtc);
        return true;
    }

    internal bool IsCompletedReport(string thread, string turn, DateTime reportedUtc)
    {
        if (thread.Length is 0 or > 64 || turn.Length > 64) return false;
        IntPtr db = IntPtr.Zero, statement = IntPtr.Zero;
        try
        {
            string file = Path.Combine(ResolveHome(), "thread_history_1.sqlite");
            if (!File.Exists(file) || Open(file, out db, 1 | 0x8000, IntPtr.Zero) != 0) return false;
            BusyTimeout(db, 25);
            string sql = turn.Length > 0
                ? "SELECT status FROM thread_turns WHERE thread_id=?1 AND turn_id=?2 LIMIT 1"
                : "SELECT status,completed_at FROM thread_turns WHERE thread_id=?1 AND completed_at IS NOT NULL "
                    + "AND status IN ('completed','failed','interrupted') AND NOT EXISTS "
                    + "(SELECT 1 FROM thread_turns live WHERE live.thread_id=?1 AND live.status NOT IN ('completed','failed','interrupted')) "
                    + "ORDER BY completed_at DESC LIMIT 1";
            if (Prepare(db, sql, -1, out statement, IntPtr.Zero) != 0) return false;
            Bind(statement, 1, thread, -1, new IntPtr(-1));
            if (turn.Length > 0) Bind(statement, 2, turn, -1, new IntPtr(-1));
            string? status = Step(statement) == 100 ? Marshal.PtrToStringUTF8(ColumnText(statement, 0)) : null;
            if (turn.Length == 0)
                return status is "completed" or "failed" or "interrupted" && CompletedAt(ColumnText(statement, 1)) is { } finished
                    && finished >= reportedUtc && finished <= DateTime.UtcNow.AddSeconds(30);
            if (status != null) return status is "completed" or "failed" or "interrupted";
            Finalize(statement); statement = IntPtr.Zero;
            // A unique turn may be stored under the desktop execution thread rather
            // than the rollout header. Ambiguous matches remain normal hook events.
            if (Prepare(db, "SELECT status FROM thread_turns WHERE turn_id=?1 LIMIT 2", -1, out statement, IntPtr.Zero) != 0) return false;
            Bind(statement, 1, turn, -1, new IntPtr(-1));
            if (Step(statement) != 100) return false;
            status = Marshal.PtrToStringUTF8(ColumnText(statement, 0));
            return Step(statement) == 101 && status is "completed" or "failed" or "interrupted";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        { return false; }
        finally { if (statement != IntPtr.Zero) Finalize(statement); if (db != IntPtr.Zero) Close(db); }
    }

    internal void Reconcile(ExternalAgentRegistry registry, DateTime now)
    {
        var active = registry.Snapshot.Where(c => c.Source == "codex" && c.State != "idle"
            && c.NativeThreadId.Length > 0).ToArray();
        if (active.Length == 0) return;
        IntPtr db = IntPtr.Zero, statement = IntPtr.Zero, byTurn = IntPtr.Zero, byThread = IntPtr.Zero;
        try
        {
            string file = Path.Combine(ResolveHome(), "thread_history_1.sqlite");
            if (!File.Exists(file) || Open(file, out db, 1 | 0x8000, IntPtr.Zero) != 0) return; // READONLY | NOMUTEX
            BusyTimeout(db, 25);
            if (Prepare(db, "SELECT status FROM thread_turns WHERE thread_id=?1 AND turn_id=?2 LIMIT 1", -1, out statement, IntPtr.Zero) != 0) return;
            Prepare(db, "SELECT thread_id,status FROM thread_turns WHERE turn_id=?1 LIMIT 2", -1, out byTurn, IntPtr.Zero);
            // Older hook payloads can omit turn_id. A completed task after this
            // activity started is sufficient only if this exact thread has no live task.
            Prepare(db, "SELECT status,completed_at FROM thread_turns WHERE thread_id=?1 AND completed_at IS NOT NULL "
                + "AND status IN ('completed','failed','interrupted') AND NOT EXISTS "
                + "(SELECT 1 FROM thread_turns live WHERE live.thread_id=?1 AND live.status NOT IN ('completed','failed','interrupted')) "
                + "ORDER BY completed_at DESC LIMIT 1", -1, out byThread, IntPtr.Zero);
            foreach (var connection in active)
            {
                if (connection.NativeTurnId.Length == 0)
                {
                    if (byThread == IntPtr.Zero || connection.ActivityStartedUtc == default) continue;
                    Bind(byThread, 1, connection.NativeThreadId, -1, new IntPtr(-1));
                    if (Step(byThread) == 100 && CompletedAt(ColumnText(byThread, 1)) is { } finished
                        && finished >= connection.ActivityStartedUtc && finished <= now.AddSeconds(30))
                        registry.FinishNativeTurn(connection, now);
                    Reset(byThread); ClearBindings(byThread);
                    continue;
                }
                var key = (connection.NativeThreadId, connection.NativeTurnId);
                Bind(statement, 1, _resolvedThreads.GetValueOrDefault(key, connection.NativeThreadId), -1, new IntPtr(-1));
                Bind(statement, 2, connection.NativeTurnId, -1, new IntPtr(-1));
                string? status = Step(statement) == 100 ? Marshal.PtrToStringUTF8(ColumnText(statement, 0)) : null;
                // Desktop execution threads can differ from the original rollout header.
                // An exact, unique turn ID still identifies the task; ambiguous matches do not.
                if (status == null && byTurn != IntPtr.Zero)
                {
                    Bind(byTurn, 1, connection.NativeTurnId, -1, new IntPtr(-1));
                    if (Step(byTurn) == 100)
                    {
                        string? actualThread = Marshal.PtrToStringUTF8(ColumnText(byTurn, 0));
                        string? candidate = Marshal.PtrToStringUTF8(ColumnText(byTurn, 1));
                        if (Step(byTurn) == 101)
                        {
                            status = candidate;
                            if (actualThread is { Length: > 0 and <= 64 }) _resolvedThreads[key] = actualThread;
                            while (_resolvedThreads.Count > 128) _resolvedThreads.Remove(_resolvedThreads.Keys.First());
                        }
                    }
                    Reset(byTurn); ClearBindings(byTurn);
                }
                if (status is "completed" or "failed" or "interrupted")
                { registry.FinishNativeTurn(connection, now); _resolvedThreads.Remove(key); }
                Reset(statement); ClearBindings(statement);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        { /* Missing/older projections keep the normal hook lifecycle. Never infer completion from silence. */ }
        finally { if (statement != IntPtr.Zero) Finalize(statement); if (byTurn != IntPtr.Zero) Finalize(byTurn); if (byThread != IntPtr.Zero) Finalize(byThread); if (db != IntPtr.Zero) Close(db); }
    }

    private static DateTime? CompletedAt(IntPtr value)
    {
        string? text = Marshal.PtrToStringUTF8(value);
        if (long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long timestamp))
        {
            try { return DateTimeOffset.FromUnixTimeMilliseconds(timestamp > 100_000_000_000_000 ? timestamp / 1000 : timestamp > 100_000_000_000 ? timestamp : checked(timestamp * 1000)).UtcDateTime; }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException) { return null; }
        }
        return DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.UtcDateTime : null;
    }

    private static string ResolveHome()
    {
        if (Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home && Path.IsPathFullyQualified(home)) return home;
        try
        {
            var file = new FileInfo(AppPaths.CodexHooksConfigFile);
            if (file.Exists && file.Length <= 64 * 1024)
            {
                using var config = JsonDocument.Parse(File.ReadAllText(file.FullName));
                if (config.RootElement.TryGetProperty("hookPath", out var hook) && hook.ValueKind == JsonValueKind.String
                    && hook.GetString() is { } path && Path.IsPathFullyQualified(path) && Path.GetDirectoryName(path) is { } parent)
                    return parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }
    [DllImport("winsqlite3.dll", EntryPoint="sqlite3_open_v2", CallingConvention=CallingConvention.Cdecl)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", EntryPoint="sqlite3_busy_timeout", CallingConvention=CallingConvention.Cdecl)] private static extern int BusyTimeout(IntPtr db,int ms);
    [DllImport("winsqlite3.dll", EntryPoint="sqlite3_prepare_v2", CallingConvention=CallingConvention.Cdecl)]
    private static extern int Prepare(IntPtr db,[MarshalAs(UnmanagedType.LPUTF8Str)] string sql,int length,out IntPtr statement,IntPtr tail);
    [DllImport("winsqlite3.dll", EntryPoint="sqlite3_bind_text", CallingConvention=CallingConvention.Cdecl)]
    private static extern int Bind(IntPtr statement,int index,[MarshalAs(UnmanagedType.LPUTF8Str)] string value,int length,IntPtr destructor);
    [DllImport("winsqlite3.dll", EntryPoint="sqlite3_step", CallingConvention=CallingConvention.Cdecl)] private static extern int Step(IntPtr statement);
    [DllImport("winsqlite3.dll", EntryPoint="sqlite3_column_text", CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr ColumnText(IntPtr statement,int column);
    [DllImport("winsqlite3.dll", EntryPoint="sqlite3_reset", CallingConvention=CallingConvention.Cdecl)] private static extern int Reset(IntPtr statement);
    [DllImport("winsqlite3.dll", EntryPoint="sqlite3_clear_bindings", CallingConvention=CallingConvention.Cdecl)] private static extern int ClearBindings(IntPtr statement);
    [DllImport("winsqlite3.dll", EntryPoint="sqlite3_finalize", CallingConvention=CallingConvention.Cdecl)] private static extern int Finalize(IntPtr statement);
    [DllImport("winsqlite3.dll", EntryPoint="sqlite3_close", CallingConvention=CallingConvention.Cdecl)] private static extern int Close(IntPtr db);
}
