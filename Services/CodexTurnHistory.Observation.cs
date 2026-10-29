using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using alpha_island.Core;
using L = AlphaIsland.PluginSdk.Localization.UiText;

namespace alpha_island.Services;

internal sealed partial class CodexTurnHistory
{
    private readonly DateTime _observeAfter = DateTime.UtcNow.AddSeconds(-1);
    private string _nativeMode = "waiting";
    private bool _nativeSupported;
    private int _nativeObserved;
    private DateTime? _nativeChecked;
    internal object NativeObservationInfo => new { supported = _nativeSupported, mode = _nativeMode, observed = _nativeObserved, checkedUtc = _nativeChecked };

    internal void ObserveNewTurns(ExternalAgentRegistry registry, CodexStatusJournal journal, DateTime now, bool enabled)
    {
        _nativeObserved = 0; _nativeChecked = now;
        if (!enabled) { _nativeMode = "paused"; registry.PauseNativeObservation(now); return; }
        string? home = journal.NativeObservationHome(out string reason);
        if (home == null) { _nativeMode = reason; registry.PauseNativeObservation(now); return; }
        int? host = journal.NativeObservationHost();
        if (host == null || !AgentHostProcessMonitor.IsNativeHost("codex", host.Value)) { _nativeMode = "waiting-for-codex"; registry.PauseNativeObservation(now); return; }
        IntPtr db = IntPtr.Zero, statement = IntPtr.Zero;
        try
        {
            string file = Path.Combine(home, "thread_history_1.sqlite");
            if (!File.Exists(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            { _nativeMode = "native-store-unavailable"; return; }
            if (Open(file, out db, 1 | 0x8000, IntPtr.Zero) != 0) { _nativeMode = "native-store-busy"; return; }
            BusyTimeout(db, 25);
            var columns = new HashSet<string>(StringComparer.Ordinal);
            if (Prepare(db, "PRAGMA table_info(thread_turns)", -1, out statement, IntPtr.Zero) != 0) { _nativeMode = "native-schema-unavailable"; return; }
            while (Step(statement) == 100 && columns.Count < 64)
                if (Marshal.PtrToStringUTF8(ColumnText(statement, 1)) is { } name) columns.Add(name);
            Finalize(statement); statement = IntPtr.Zero;
            if (!new[] { "thread_id", "turn_id", "status", "started_at", "root_turn_id" }.All(columns.Contains))
            { _nativeMode = "native-schema-unsupported"; _nativeSupported = false; return; }
            _nativeSupported = true;
            // Select only task metadata. Message bodies and error payloads are never selected.
            string sql = "SELECT thread_id,turn_id,started_at,root_turn_id FROM thread_turns WHERE status IN ('inProgress','in_progress','running') AND root_turn_id=turn_id ORDER BY started_at DESC LIMIT 64";
            if (Prepare(db, sql, -1, out statement, IntPtr.Zero) != 0) { _nativeMode = "native-query-unavailable"; return; }
            int observed = 0;
            var threads = new HashSet<string>(StringComparer.Ordinal);
            while (Step(statement) == 100)
            {
                string thread = Marshal.PtrToStringUTF8(ColumnText(statement, 0)) ?? "", turn = Marshal.PtrToStringUTF8(ColumnText(statement, 1)) ?? "";
                string root = Marshal.PtrToStringUTF8(ColumnText(statement, 3)) ?? "";
                if (thread.Length is 0 or > 64 || turn.Length is 0 or > 64 || root != turn) continue;
                if (CompletedAt(ColumnText(statement, 2)) is not { } started || started < _observeAfter || started > now.AddSeconds(30)) continue;
                if (!threads.Add(thread)) continue;
                string session = "native." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(thread))).ToLowerInvariant()[..24];
                if (registry.ObserveNativeCodex(session, thread, turn, host.Value, started, now, L.T("Codex · 原生任务状态"))) observed++;
            }
            _nativeObserved = observed; _nativeChecked = now; _nativeMode = "observing";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        { _nativeMode = "native-read-unavailable"; }
        finally { if (statement != IntPtr.Zero) Finalize(statement); if (db != IntPtr.Zero) Close(db); }
    }
}
