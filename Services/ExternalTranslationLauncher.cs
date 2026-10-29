using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using alpha_island.Core;

namespace alpha_island.Services;

/// <summary>有道从进程、Windows 应用注册与安装目录发现；不保存某台机器的盘符。</summary>
internal static class ExternalTranslationLauncher
{
    internal static bool IsYoudao(string name) => name.Contains("有道", StringComparison.OrdinalIgnoreCase) || name.Contains("Youdao", StringComparison.OrdinalIgnoreCase);
    internal static string? FindYoudao()
    {
        foreach (var process in Process.GetProcessesByName("YoudaoDict"))
        {
            using (process) { try { if (process.MainModule?.FileName is { } path && File.Exists(path)) return path; } catch { } }
        }
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var app = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\YoudaoDict.exe");
                if (app?.GetValue("") is string exe && File.Exists(exe.Trim('"'))) return exe.Trim('"');
                using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (string key in uninstall?.GetSubKeyNames() ?? [])
                {
                    using var entry = uninstall?.OpenSubKey(key);
                    if (!IsYoudao(entry?.GetValue("DisplayName") as string ?? "")) continue;
                    if (entry?.GetValue("InstallLocation") is string location && FindInDirectory(location) is { } found) return found;
                    if (entry?.GetValue("DisplayIcon") is string icon)
                    {
                        string candidate = icon.Trim().Trim('"');
                        int suffix = candidate.LastIndexOf(",", StringComparison.Ordinal);
                        if (suffix > 0 && int.TryParse(candidate[(suffix + 1)..], out _)) candidate = candidate[..suffix].Trim('"');
                        if (Path.GetFileName(candidate).Equals("YoudaoDict.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate)) return candidate;
                        if (FindInDirectory(Path.GetDirectoryName(candidate)) is { } next) return next;
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException) { }
        }
        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            if (FindInDirectory(Path.Combine(root, "Youdao", "Dict")) is { } found) return found;
        return null;
    }

    internal static string? FindInDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) return null;
        foreach (string relative in new[] { "YoudaoDict.exe", @"Dict\YoudaoDict.exe", @"Youdao\Dict\YoudaoDict.exe" })
        {
            string file = Path.Combine(directory, relative);
            if (File.Exists(file)) return file;
        }
        return null;
    }

    internal static async Task<ShortcutSendResult> SendAsync(ExternalTranslationOptions options, ShortcutSimulationService shortcut, CancellationToken cancellationToken)
    {
        if (!options.Enabled) return new(false, "外部翻译调用未启用。");
        if (!options.IsConfigured) return new(false, "请先填写应用名称和快捷键。");
        if (!ShortcutSimulationService.TryParse(options.Shortcut, out _)) return new(false, "快捷键格式无效，请在设置中修改。");
        if (IsYoudao(options.Name))
        {
            bool running = false;
            foreach (var process in Process.GetProcessesByName("YoudaoDict")) { using (process) running = true; }
            if (!running)
            {
                string? executable = await Task.Run(FindYoudao, cancellationToken).ConfigureAwait(false);
                if (executable == null) return new(false, "未找到所配置的应用，请先运行该应用或检查设置。");
                using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                // 首次启动需要等待软件注册全局热键；已运行时保持原来快速调用路径。
                if (process != null) await Task.Run(() => { try { process.WaitForInputIdle(4000); } catch (InvalidOperationException) { } }, cancellationToken).ConfigureAwait(false);
                await Task.Delay(600, cancellationToken).ConfigureAwait(false);
            }
        }
        return await shortcut.SendAsync(options.Shortcut, cancellationToken).ConfigureAwait(false);
    }
}
