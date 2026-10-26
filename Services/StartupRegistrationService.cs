using Microsoft.Win32;
using System.IO;

namespace alpha_island.Services;

internal interface IStartupRegistrationStore
{
    string? Read();
    void Write(string? command);
    bool IsDisabledByWindows => false;
}

internal sealed class StartupRegistrationService
{
    private readonly IStartupRegistrationStore _store;
    private readonly string _executable;
    internal StartupRegistrationService(IStartupRegistrationStore? store = null, string? executable = null)
    {
        _store = store ?? new UserRunRegistrationStore();
        _executable = Path.GetFullPath(executable ?? ResolveExecutable());
    }
    internal bool IsDisabledByWindows => _store.IsDisabledByWindows;
    internal bool IsEnabled => !IsDisabledByWindows && string.Equals(_store.Read(), Command, StringComparison.OrdinalIgnoreCase);
    internal string Command => $"\"{_executable}\" --startup";
    internal bool RestoreIfRequested(bool enabled)
    {
        if (enabled && !IsDisabledByWindows && !IsEnabled) SetEnabled(true);
        return IsEnabled;
    }
    internal void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (IsDisabledByWindows)
                throw new InvalidOperationException("Windows 已禁用此启动项，请在 Windows 设置 → 应用 → 启动中重新开启 Alpha Island");
            if (!File.Exists(_executable)) throw new FileNotFoundException("找不到当前程序，请从安装目录启动后重试");
            if (Command.Length > 260 || _executable.Contains('"') || _executable.Any(char.IsControl))
                throw new InvalidOperationException("程序路径过长或无效，请移到较短的目录后重试");
        }
        _store.Write(enabled ? Command : null);
        if (!string.Equals(_store.Read(), enabled ? Command : null, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Windows 未保存自启动登记，请检查启动项管理或安全软件后重试");
    }
    private static string ResolveExecutable()
    {
        if (Environment.ProcessPath is string path && !Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return path;
        return Path.Combine(AppContext.BaseDirectory, "alpha-island.exe");
    }
    internal static bool IsWindowsStartupDisabled(object? value)
        => value is byte[] { Length: > 0 } state && state[0] is not (2 or 6);

    private sealed class UserRunRegistrationStore : IStartupRegistrationStore
    {
        private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Value = "AlphaIsland";
        public bool IsDisabledByWindows
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
                return IsWindowsStartupDisabled(key?.GetValue(Value));
            }
        }
        public string? Read() { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue(Value) as string; }
        public void Write(string? command)
        {
            if (command == null)
            {
                using var key = Registry.CurrentUser.OpenSubKey(Key, writable: true);
                key?.DeleteValue(Value, throwOnMissingValue: false);
            }
            else
            {
                using var key = Registry.CurrentUser.CreateSubKey(Key, writable: true);
                key.SetValue(Value, command, RegistryValueKind.String);
            }
        }
    }
}
