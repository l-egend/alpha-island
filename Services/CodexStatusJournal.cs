using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using alpha_island.Core;

namespace alpha_island.Services;

/// <summary>Recovers only unsent Codex state metadata. No conversation text or approval decisions enter this outbox.</summary>
internal sealed class CodexStatusJournal
{
    private readonly string _endpoint, _configuration;
    private readonly Func<int, int?>? _resolveHost;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _received = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _latest = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _started = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _completed = new(StringComparer.Ordinal);
    private readonly CodexTurnHistory _nativeTurns = new();
    private static readonly Regex EventName = new(@"^\d{13}-[a-f0-9-]{36}\.json$", RegexOptions.CultureInvariant);

    internal CodexStatusJournal(string endpoint, string? configuration = null, Func<int, int?>? resolveHost = null)
    { _endpoint = endpoint; _configuration = configuration ?? AppPaths.CodexHooksConfigFile; _resolveHost = resolveHost; }

    internal bool Deliver(JsonElement payload, DateTime now, Func<bool> apply)
    {
        string id = Text(payload, "statusEventId"), session = Text(payload, "session");
        if (Text(payload, "source") != "codex" || !Guid.TryParseExact(id, "D", out _)
            || !payload.TryGetProperty("reportedAtUnixMs", out var stamp) || !stamp.TryGetInt64(out long milliseconds)) return apply();
        DateTime sent;
        try { sent = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime; }
        catch (ArgumentOutOfRangeException) { return false; }
        if (sent > now.AddSeconds(30) || sent < now.AddHours(-24)) return false;
        lock (_gate)
        {
            bool connect = Flag(payload, "connectOnly"), begins = Flag(payload, "beginsActivity");
            var ordering = begins ? _started : _latest;
            string key = begins ? session : session + "/" + Text(payload, "nativeTurnId");
            if (_received.ContainsKey(id) || !connect && ordering.TryGetValue(key, out var last) && sent < last) return true;
            string turnKey = session + "/" + Text(payload, "nativeTurnId");
            // Tool reports can precede a recovered start, but an explicit completion
            // must win over an older start recovered after the task has finished.
            if (begins && _completed.TryGetValue(turnKey, out var completed) && sent <= completed) return true;
            if (!apply()) return false;
            _received[id] = now;
            // Readiness and later tool reports must not supersede a missing task-start.
            if (!connect) ordering[key] = sent;
            if (!connect && Text(payload, "state") == "idle") _completed[turnKey] = sent;
            while (_received.Count > 512) _received.Remove(_received.MinBy(item => item.Value).Key);
            while (_latest.Count > 256) _latest.Remove(_latest.MinBy(item => item.Value).Key);
            while (_started.Count > 128) _started.Remove(_started.MinBy(item => item.Value).Key);
            while (_completed.Count > 256) _completed.Remove(_completed.MinBy(item => item.Value).Key);
            return true;
        }
    }

    internal void Drain(ExternalAgentRegistry registry, DateTime now)
    {
        try
        {
            string? directory = DirectoryForEndpoint();
            if (directory == null || !Directory.Exists(directory) || Linked(directory)) return;
            int? fallbackHost = null; bool searchedHost = false;
            foreach (string file in Directory.EnumerateFiles(directory, "*.json").Where(path => EventName.IsMatch(Path.GetFileName(path)))
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal).Take(128))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > 8192 || Linked(file)) continue;
                    using var document = JsonDocument.Parse(File.ReadAllText(file));
                    var envelope = document.RootElement;
                    if (!envelope.TryGetProperty("version", out var version) || !version.TryGetInt32(out int v) || v != 1
                        || !envelope.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
                        || Text(payload, "source") != "codex") { File.Delete(file); continue; }
                    string state = Text(payload, "state"), session = Text(payload, "session");
                    if (!payload.TryGetProperty("reportedAtUnixMs", out var stamp) || !stamp.TryGetInt64(out long ms)
                        || !Guid.TryParseExact(Text(payload, "statusEventId"), "D", out _)) { File.Delete(file); continue; }
                    var sent = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
                    if (sent < now.AddMinutes(-5) || sent > now.AddSeconds(30)) { File.Delete(file); continue; }
                    int reporter = payload.TryGetProperty("reporterPid", out var pid) && pid.TryGetInt32(out int value) ? value : 0;
                    int? host = _resolveHost?.Invoke(reporter) ?? AgentHostProcessMonitor.Resolve("codex", reporter);
                    if (host == null && _resolveHost == null)
                    {
                        if (!searchedHost) { fallbackHost = CurrentHost(); searchedHost = true; }
                        host = fallbackHost;
                    }
                    // A queued start from an app which has already exited must not create a ghost island.
                    if (state != "idle" && host == null) { File.Delete(file); continue; }
                    bool connect = Flag(payload, "connectOnly");
                    if (connect && state != "idle") { File.Delete(file); continue; }
                    bool ApplyRecoveredStatus()
                    {
                        if (!connect && state != "idle" && _nativeTurns.IgnoreCompletedReport(registry,
                            Text(payload, "nativeThreadId"), Text(payload, "nativeTurnId"), sent, now)) return true;
                        return connect
                        ? registry.Connect("codex", session, 300, now, out _, host, Text(payload,"artworkFamily"), Text(payload,"parentSession"), Text(payload,"sessionAlias"))
                        : registry.Update("codex", session, state, Text(payload,"detail"), 300, now, out _, host,
                            Text(payload,"artworkFamily"), Text(payload,"parentSession"), Text(payload,"sessionAlias"), Flag(payload,"beginsActivity"),
                            Text(payload,"nativeThreadId"), Text(payload,"nativeTurnId"), activityStartedUtc: sent);
                    }
                    bool delivered = Deliver(payload, now, ApplyRecoveredStatus);
                    if (delivered) File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private string? DirectoryForEndpoint()
    {
        var info = new FileInfo(_configuration);
        if (!info.Exists || info.Length > 65536) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(_configuration));
        var config = document.RootElement;
        if (!Uri.TryCreate(Text(config,"endpoint"), UriKind.Absolute, out var address) || address.Scheme != "http"
            || !address.IsLoopback || address.UserInfo.Length != 0 || address.Port != new Uri(_endpoint).Port
            || address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0) return null;
        string runtime = Text(config,"runtimeDir");
        if (!Path.IsPathFullyQualified(runtime)) return null;
        string ledger = Path.Combine(Path.GetDirectoryName(_configuration)!, "integrations.json");
        if (File.Exists(ledger))
        {
            using var registrations = JsonDocument.Parse(File.ReadAllText(ledger));
            if (registrations.RootElement.TryGetProperty("providers",out var providers) && providers.TryGetProperty("codex",out var codex)
                && codex.TryGetProperty("enabled",out var enabled) && enabled.ValueKind == JsonValueKind.False) return null;
        }
        return Path.Combine(runtime,"status-events");
    }
    private static bool Linked(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool Flag(JsonElement value, string key) => value.TryGetProperty(key,out var item) && item.ValueKind == JsonValueKind.True;
    private static string Text(JsonElement value, string key) => value.TryGetProperty(key,out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
    private static int? CurrentHost()
    {
        foreach (string name in new[]{"Codex","ChatGPT"})
        {
            int? fallback = null, window = null;
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try { if (!process.HasExited) { fallback ??= process.Id; if(process.MainWindowHandle!=IntPtr.Zero) window ??= process.Id; } }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
            }
            if (window != null || fallback != null) return window ?? fallback;
        }
        return null;
    }
}
