using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace AlphaIsland.Plugins.LocalAgents;

internal static partial class AgentDiscovery
{
    internal static string ManualFile(string config) => Path.Combine(config, "plugins", "ai.local-agents", "executables.json");
    internal static void SetCodexExecutable(string config, string? executable)
    {
        string file = ManualFile(config);
        if (executable == null) { if (File.Exists(file)) File.Delete(file); return; }
        if (!File.Exists(executable) || !Path.GetFileName(executable).Equals("codex.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("请选择 codex.exe 命令行程序。");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(new { codex = CodexCommand(Path.GetFullPath(executable)) }));
    }

    private static string? FindCodex(string config, string local, string user)
    {
        try
        {
            string file = ManualFile(config);
            if (new FileInfo(file).Length < 16384)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("codex", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } custom
                    && Path.GetFileName(custom).Equals("codex.exe", StringComparison.OrdinalIgnoreCase) && Existing(custom) is { } found) return CodexCommand(found);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        if (OnPath("codex.exe") is { } native) return CodexCommand(native);
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Select(path => path.Trim().Trim('"'))
            .Where(path => path.Length > 0).Concat(new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
                Path.Combine(user, ".local", "bin") }).Take(128);
        foreach (string folder in paths)
        {
            try { if (NpmCodex(folder) is { } npm) return npm; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        foreach (string root in new[] { Path.Combine(local, "OpenAI", "Codex"), Path.Combine(local, "Programs", "Codex") })
        {
            if (PackagedCodex(root) is { } direct) return direct;
            if (Children(Path.Combine(root, "bin"), "codex.exe") is { } versioned) return versioned;
            foreach (string child in SafeDirectories(root, "app-*")) if (PackagedCodex(child) is { } app) return app;
        }
        foreach (string root in StoreInstallations()) if (PackagedCodex(root) is { } store) return store;
        foreach (string root in SafeDirectories(Path.Combine(user, ".vscode", "extensions"), "openai.chatgpt-*"))
            foreach (string arch in Architectures())
                if (Existing(Path.Combine(root, "bin", "windows-" + arch, "codex.exe")) is { } extension) return extension;
        return null;
    }

    private static string CodexCommand(string executable) => PackagedCodex(Path.GetDirectoryName(executable)!) ?? executable;

    private static IEnumerable<string> Architectures() => RuntimeInformation.OSArchitecture == Architecture.Arm64
        ? new[] { "aarch64", "x86_64" } : new[] { "x86_64" };

    internal static string? NpmCodex(string prefix)
    {
        foreach (string arch in Architectures())
        foreach (string package in new[] { "@openai/codex", "@openai/codex/node_modules/@openai/codex-win32-x64",
            "@openai/codex-win32-x64", "@openai/codex/node_modules/@openai/codex-win32-arm64", "@openai/codex-win32-arm64" })
        {
            string candidate = Path.Combine(prefix, "node_modules", package.Replace('/', Path.DirectorySeparatorChar),
                "vendor", arch + "-pc-windows-msvc", "codex", "codex.exe");
            if (Existing(candidate) is { } path) return path;
        }
        return null;
    }

    internal static string? PackagedCodex(string root)
    {
        foreach (string candidate in new[] { "resources/codex.exe", "app/resources/codex.exe", "bin/codex.exe" })
            if (Existing(Path.Combine(root, candidate.Replace('/', Path.DirectorySeparatorChar))) is { } path) return path;
        return null;
    }

    private static string[] SafeDirectories(string root, string pattern)
    {
        try { return Directory.EnumerateDirectories(root, pattern).Take(32).OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    private static IEnumerable<string> StoreInstallations()
    {
        var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            foreach (string name in (packages?.GetSubKeyNames() ?? []).Where(name => name.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)).Take(16))
            {
                using var package = packages!.OpenSubKey(name);
                if (package?.GetValue("PackageRootFolder") is string root) locations.Add(root);
            }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        foreach (string root in SafeDirectories(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"), "OpenAI.Codex_*")) locations.Add(root);
        foreach (var process in Process.GetProcessesByName("Codex").Take(24))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is not { } file) continue;
                    string directory = Path.GetDirectoryName(file)!;
                    locations.Add(directory);
                    if (Path.GetFileName(directory).Equals("resources", StringComparison.OrdinalIgnoreCase)) locations.Add(Path.GetDirectoryName(directory)!);
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return locations;
    }
}
