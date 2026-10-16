using Microsoft.Win32;
using System.IO;

namespace alpha_island.Services;

internal interface IStartupRegistrationStore
{
    string? Read();
    void Write(string? command);
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
    internal bool IsEnabled => string.Equals(_store.Read(), Command, StringComparison.OrdinalIgnoreCase);
    internal string Command => $"\"{_executable}\" --startup";
    internal void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (!File.Exists(_executable)) throw new FileNotFoundException("找不到当前程序，请从安装目录启动后重试");
            if (Command.Length > 260 || _executable.Contains('"') || _executable.Any(char.IsControl))
                throw new InvalidOperationException("程序路径过长或无效，请移到较短的目录后重试");
        }
        _store.Write(enabled ? Command : null);
    }
    private static string ResolveExecutable()
    {
        if (Environment.ProcessPath is string path && !Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return path;
        return Path.Combine(AppContext.BaseDirectory, "alpha-island.exe");
    }
    private sealed class UserRunRegistrationStore : IStartupRegistrationStore
    {
        private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Value = "AlphaIsland";
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
