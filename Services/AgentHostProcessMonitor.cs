using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace alpha_island.Services;

/// <summary>State liveness only: no window activation, process writes or persistent polling.</summary>
internal static class AgentHostProcessMonitor
{
    internal sealed record Entry(int Id, int ParentId, string Name);

    internal static int? Resolve(string source, int reporterPid)
    {
        if (source is not ("codex" or "antigravity" or "claude-code")) return null;
        try
        {
            using var snapshot = CreateToolhelp32Snapshot(2, 0);
            if (snapshot.IsInvalid) return null;
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Name = "" };
            var entries = new Dictionary<int, Entry>();
            if (Process32First(snapshot, ref entry))
                do { entries[(int)entry.Id] = new((int)entry.Id, (int)entry.ParentId, entry.Name); }
                while (Process32Next(snapshot, ref entry));
            return Resolve(source, reporterPid, entries);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        { return null; }
    }

    internal static int? Resolve(string source, int reporterPid, IReadOnlyDictionary<int, Entry> entries)
    {
        int? host = null;
        var seen = new HashSet<int>();
        for (int depth = 0; depth < 32 && reporterPid > 0 && seen.Add(reporterPid); depth++)
        {
            if (!entries.TryGetValue(reporterPid, out var entry)) break;
            string name = entry.Name.ToLowerInvariant();
            if (source == "claude-code" && name == "claude.exe") return entry.Id;
            if ((source == "codex" && name is "codex.exe" or "chatgpt.exe")
                || (source == "antigravity" && name == "antigravity.exe")) host = entry.Id;
            reporterPid = entry.ParentId;
        }
        // Use the outer app, not the short-lived Node hook, shell or app-server child.
        return host;
    }

    internal static long? StartStamp(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.StartTime.ToUniversalTime().Ticks; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    internal static bool IsNativeHost(string source, int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            string name = process.ProcessName.ToLowerInvariant();
            return source switch { "claude-code" => name == "claude", "codex" => name is "codex" or "chatgpt", "antigravity" => name == "antigravity", _ => false };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    internal static bool IsAlive(int pid, long? stamp)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && (stamp == null || process.StartTime.ToUniversalTime().Ticks == stamp);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; } // Unknown access keeps the bounded lease.
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Id;
        public UIntPtr Heap;
        public uint Module, Threads, ParentId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}
