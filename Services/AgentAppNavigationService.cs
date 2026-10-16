using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using alpha_island.Interop;

namespace alpha_island.Services;

internal sealed record AgentAppWindow(IntPtr Handle, string Process, string Title, bool Minimized, uint ProcessId = 0);
internal interface IAgentWindowAccess
{
    IReadOnlyList<AgentAppWindow> ReadWindows();
    IntPtr Foreground { get; }
    bool Activate(AgentAppWindow window);
}

/// <summary>Switches to an existing agent window, preserving its open conversation.</summary>
internal sealed class AgentAppNavigationService
{
    private readonly IAgentWindowAccess _windows;
    internal AgentAppNavigationService(IAgentWindowAccess? windows = null) => _windows = windows ?? new NativeAgentWindowAccess();

    internal static string? Provider(string owner)
    {
        if (!owner.StartsWith("external:", StringComparison.Ordinal)) return null;
        string source = owner[9..].Split('/')[0].ToLowerInvariant();
        return source switch { "codex" => "codex", "antigravity" => "antigravity",
            "deepseek-harness" or "dsh" => "deepseek-harness", "claude-code" => "claude-code", _ => null };
    }

    internal static bool Matches(string provider, AgentAppWindow window)
    {
        string process = window.Process.ToLowerInvariant();
        return provider switch
        {
            "codex" => process is "codex" or "chatgpt",
            "antigravity" => process is "antigravity",
            "claude-code" => process == "claude" || (process is "windowsterminal" or "pwsh" or "powershell" or "cmd" or "conhost")
                && Regex.IsMatch(window.Title, @"\bClaude(?:\s+Code)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            "deepseek-harness" => process is "dsh" or "deepseek-harness" or "deepseek"
                || (process is "windowsterminal" or "pwsh" or "powershell" or "cmd")
                    && Regex.IsMatch(window.Title, @"\b(DSH|DeepSeek\s+Harness)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            _ => false
        };
    }

    internal bool TryOpen(string owner, out string message)
    {
        if (!TryResolve(owner, out var target, out message)) return false;
        return Activate(target!, out message);
    }

    internal bool TryResolve(string owner, out AgentAppWindow? target, out string message)
    {
        target = null;
        message = "";
        if (Provider(owner) is not { } provider) { message = "此助手尚未提供可切换的应用窗口。"; return false; }
        var candidates = _windows.ReadWindows().Where(w => Matches(provider, w)).ToArray();
        // Codex can also run in the ChatGPT desktop work surface. Prefer the
        // standalone app when it has a window; otherwise use that existing host.
        if (provider == "codex" && candidates.Any(w => w.Process.Equals("codex", StringComparison.OrdinalIgnoreCase)))
            candidates = candidates.Where(w => w.Process.Equals("codex", StringComparison.OrdinalIgnoreCase)).ToArray();
        target = candidates.FirstOrDefault(w => w.Handle == _windows.Foreground) ?? candidates.FirstOrDefault();
        if (target == null) { message = "未找到对应助手的已打开窗口。"; return false; }
        return true;
    }

    internal Dictionary<string, AgentAppWindow> ResolveActivities(IEnumerable<string> owners)
    {
        var windows = _windows.ReadWindows();
        var foreground = _windows.Foreground;
        var result = new Dictionary<string, AgentAppWindow>(StringComparer.Ordinal);
        foreach (string owner in owners)
        {
            if (Provider(owner) is not { } provider) continue;
            var candidates = windows.Where(w => Matches(provider, w)).ToArray();
            if (provider == "codex" && candidates.Any(w => w.Process.Equals("codex", StringComparison.OrdinalIgnoreCase)))
                candidates = candidates.Where(w => w.Process.Equals("codex", StringComparison.OrdinalIgnoreCase)).ToArray();
            if ((candidates.FirstOrDefault(w => w.Handle == foreground) ?? candidates.FirstOrDefault()) is { } target)
                result[owner] = target;
        }
        return result;
    }

    internal bool Activate(AgentAppWindow target, out string message)
    {
        message = "";
        if (_windows.Activate(target)) return true;
        message = "暂时无法切换到助手窗口，可从任务栏打开。"; return false;
    }
    internal bool IsForeground(AgentAppWindow target) => _windows.Foreground == target.Handle;

    internal sealed class NativeAgentWindowAccess : IAgentWindowAccess
    {
        public IntPtr Foreground => NativeMethods.GetForegroundWindow();
        public IReadOnlyList<AgentAppWindow> ReadWindows()
        {
            var windows = new List<AgentAppWindow>();
            var names = new Dictionary<uint, string>();
            NativeMethods.EnumWindows((handle, _) =>
            {
                if (!NativeMethods.IsWindowVisible(handle) || NativeMethods.GetWindow(handle, 4) != IntPtr.Zero) return true;
                NativeMethods.GetWindowThreadProcessId(handle, out uint id);
                try
                {
                    if (!names.TryGetValue(id, out string? name))
                    { using var process = Process.GetProcessById((int)id); names[id] = name = process.ProcessName; }
                    var title = new StringBuilder(512); GetWindowText(handle, title, title.Capacity);
                    if (title.Length > 0) windows.Add(new(handle, name, title.ToString(), NativeMethods.IsIconic(handle), id));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                return true;
            }, IntPtr.Zero);
            return windows;
        }
        public bool Activate(AgentAppWindow window)
        {
            if (!NativeMethods.IsWindow(window.Handle)) return false;
            NativeMethods.GetWindowThreadProcessId(window.Handle, out uint process);
            if (window.ProcessId != 0 && window.ProcessId != process) return false;
            if (NativeMethods.IsIconic(window.Handle) || !NativeMethods.IsWindowVisible(window.Handle))
                NativeMethods.ShowWindowAsync(window.Handle, NativeMethods.SW_RESTORE);
            return NativeMethods.GetForegroundWindow() == window.Handle || NativeMethods.SetForegroundWindow(window.Handle);
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int size);
    }
}
