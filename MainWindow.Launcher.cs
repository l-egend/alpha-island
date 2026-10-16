using System.Diagnostics;
using System.Windows.Input;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;
using alpha_island.ViewModels;

namespace alpha_island;
public partial class MainWindow
{
    private readonly LauncherApplicationIndex _launcherIndex = new();
    private QuickLauncherWindow? _launcherWindow;
    private LauncherViewModel? _launcherModel;
    private LauncherEntry[] _launcherApplications = [];
    private IDisposable? _launcherHotkey;
    private string _launcherRegistrationKey = "", _launcherHotkeyStatus = "";
    private bool _launcherIndexing;
    private DateTime _launcherIndexed = DateTime.MinValue;
    private void ConfigureLauncher()
    {
        if (!_runtimeEnabled) return;
        string shortcut = SettingsRegistry.GetText(_options, "LauncherShortcut", "Ctrl+Alt+Space").Trim();
        bool enabled = SettingsRegistry.GetBool(_options, "LauncherEnabled", true);
        string key = enabled ? shortcut : "";
        if (_launcherRegistrationKey == key && (_launcherHotkey != null || !enabled)) return;
        _launcherHotkey?.Dispose(); _launcherHotkey = null; _launcherRegistrationKey = key;
        _launcherHotkeyStatus = enabled ? "" : "快速启动热键已关闭";
        if (!enabled) { _launcherWindow?.Hide(); _settingsWindow?.SetLauncherStatus(_launcherHotkeyStatus); return; }
        try
        {
            if (new KeyGestureConverter().ConvertFromInvariantString(shortcut) is not KeyGesture gesture
                || gesture.Key == Key.None || (gesture.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
                throw new ArgumentException("请使用包含 Ctrl、Alt 或 Win 的组合键");
            bool registered = HotkeyService.TryRegisterSystem(new(gesture.Modifiers, gesture.Key), ShowQuickLauncher, out _launcherHotkey, out var error);
            _launcherHotkeyStatus = registered ? "快速启动热键：" + shortcut : error + "；可从屏幕工具卡打开启动器并修改快捷键";
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or FormatException)
        { _launcherHotkeyStatus = "快捷键格式无效：" + ex.Message; }
        DiagnosticsReport.RegisterSection("快速启动", () => _launcherHotkeyStatus);
        _settingsWindow?.SetLauncherStatus(_launcherHotkeyStatus);
    }
    private async void ShowQuickLauncher()
    {
        if (_sessionLocked || _exiting) return;
        if (_launcherWindow == null)
        {
            _launcherModel = new LauncherViewModel(ExecuteLauncherAsync);
            _launcherWindow = new QuickLauncherWindow(_launcherModel);
            _launcherWindow.Closed += (_, _) => { _launcherWindow = null; _launcherModel = null; };
        }
        if (_launcherWindow.IsVisible) { _launcherWindow.Hide(); return; }
        RefreshLauncherEntries(); _launcherWindow.Show(); _launcherWindow.Activate();
        if (_launcherIndexing || DateTime.UtcNow - _launcherIndexed < TimeSpan.FromMinutes(5)) return;
        _launcherIndexing = true;
        try
        {
            _launcherApplications = await _launcherIndex.ScanAsync(_utilitiesLifetime.Token); _launcherIndexed = DateTime.UtcNow;
            if (!_exiting && _launcherModel != null) { RefreshLauncherEntries(); _launcherModel.Status = "↑↓ 选择 · Enter 打开 · Esc 关闭"; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (_launcherModel != null) _launcherModel.Status = "应用索引失败：" + ex.Message; }
        finally { _launcherIndexing = false; }
    }
    private void RefreshLauncherEntries()
    {
        var entries = _pages.Pages.Select(page => new LauncherEntry("page:" + page.Id, page.Title, "岛内组件", LauncherKind.Component, page.Id)).ToList();
        entries.AddRange(new[] { ("codex", "Codex"), ("antigravity", "Antigravity"), ("deepseek-harness", "DSH / DeepSeek"), ("claude-code", "Claude Code") }
            .Select(pair => new LauncherEntry("agent:" + pair.Item1, pair.Item2, "切换至已打开的助手窗口", LauncherKind.Agent, "external:" + pair.Item1 + "/launcher")));
        entries.AddRange(_launcherApplications); _launcherModel?.SetEntries(entries);
        if (_launcherModel != null) _launcherModel.Status = _launcherApplications.Length == 0 ? "正在读取开始菜单应用…" : "↑↓ 选择 · Enter 打开 · Esc 关闭";
    }
    private async Task ExecuteLauncherAsync(LauncherEntry item)
    {
        if (_sessionLocked || _exiting) return;
        switch (item.Kind)
        {
            case LauncherKind.Application:
                Process.Start(new ProcessStartInfo(item.Target) { UseShellExecute = true }); break;
            case LauncherKind.Component: SelectComponent(item.Target); break;
            case LauncherKind.Agent:
                if (!_agentNavigation.TryOpen(item.Target, out string message))
                {
                    var app = _launcherApplications.FirstOrDefault(app => app.Title.Contains(item.Title.Split(' ')[0], StringComparison.OrdinalIgnoreCase));
                    if (app == null) throw new InvalidOperationException(message + " 请先启动该助手。");
                    Process.Start(new ProcessStartInfo(app.Target) { UseShellExecute = true });
                }
                break;
            case LauncherKind.AiPrompt:
                string? page = _pages.Pages.FirstOrDefault(page => page.Id.StartsWith("plugin:ai.local-agents/", StringComparison.Ordinal))?.Id;
                page ??= _pages.IndexOf("plugin:ai.api-chat/chat") >= 0 ? "plugin:ai.api-chat/chat" : null;
                if (page == null || _plugins == null || !await _plugins.PrefillAsync(page, item.Target))
                    throw new InvalidOperationException("请在插件设置中启用 AI 对话或本机 Agent；文本尚未发送。");
                SelectComponent(page); break;
        }
        _launcherWindow?.Hide();
    }
    private void StopLauncher()
    { _launcherHotkey?.Dispose(); _launcherHotkey = null; _launcherWindow?.Close(); _launcherWindow = null; }
}
