using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using alpha_island.Core;

namespace alpha_island.Services;

/// <summary>Read only the terminal status of an exact native turn. No messages, log text or account credentials are read.</summary>
internal sealed class CodexTurnHistory
{
    private readonly Dictionary<(string Thread, string Turn), string> _resolvedThreads = new();
    internal void Reconcile(ExternalAgentRegistry registry, DateTime now)
    {
        var active = registry.Snapshot.Where(c => c.Source == "codex" && c.State != "idle"
            && c.NativeThreadId.Length > 0 && c.NativeTurnId.Length > 0).ToArray();
        if (active.Length == 0) return;
        IntPtr db = IntPtr.Zero, statement = IntPtr.Zero, byTurn = IntPtr.Zero;
        try
        {
            string file = Path.Combine(ResolveHome(), "thread_history_1.sqlite");
            if (!File.Exists(file) || Open(file, out db, 1 | 0x8000, IntPtr.Zero) != 0) return; // READONLY | NOMUTEX
            BusyTimeout(db, 25);
            if (Prepare(db, "SELECT status FROM thread_turns WHERE thread_id=?1 AND turn_id=?2 LIMIT 1", -1, out statement, IntPtr.Zero) != 0) return;
            Prepare(db, "SELECT thread_id,status FROM thread_turns WHERE turn_id=?1 LIMIT 2", -1, out byTurn, IntPtr.Zero);
            foreach (var connection in active)
            {
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
        finally { if (statement != IntPtr.Zero) Finalize(statement); if (byTurn != IntPtr.Zero) Finalize(byTurn); if (db != IntPtr.Zero) Close(db); }
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