using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Plugins;
using Button = System.Windows.Controls.Button;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    private readonly Dictionary<string, PluginHost.SettingsSession> _pluginSettingsSessions = new(StringComparer.OrdinalIgnoreCase);
    private bool _pluginSettingsVisibilityHooked;

    private void InitializePluginSettingsLifetime()
    {
        if (_pluginSettingsVisibilityHooked) return;
        _pluginSettingsVisibilityHooked = true;
        Page_Plugins.IsVisibleChanged += (_, _) =>
        {
            if (!Page_Plugins.IsVisible) ReleasePluginSettingsSessions();
            else QueuePluginRefresh();
        };
    }

    private void ReleasePluginSettingsSessions()
    {
        foreach (var session in _pluginSettingsSessions.Values) session.Dispose();
        _pluginSettingsSessions.Clear();
    }

    private void PrunePluginSettingsSessions()
    {
        foreach (var pair in _pluginSettingsSessions.ToArray())
            if (pair.Value.IsDisposed || _pluginHost?.Entries.Contains(pair.Value.Entry) != true || pair.Value.Entry.UninstallPending || pair.Value.Entry.Running)
            { pair.Value.Dispose(); _pluginSettingsSessions.Remove(pair.Key); }
    }

    private void OpenPluginDetails(PluginHost.Entry entry)
    {
        if (_pluginHost == null || entry.UninstallPending) return;
        if (entry.Running) return;
        if (_pluginSettingsSessions.TryGetValue(entry.Directory, out var existing))
        {
            if (existing.Error.Length == 0) return;
            existing.Dispose(); _pluginSettingsSessions.Remove(entry.Directory);
        }
        try
        {
            ReleasePluginSettingsSessions();
            var session = _pluginHost.OpenSettingsSession(entry);
            _pluginSettingsSessions.Add(entry.Directory, session);
            session.Changed += QueuePluginRefresh;
            RebuildPluginRows();
            _ = session.InitializeAsync();
        }
        catch (Exception error) { IslandLog.Warn("Plugins:Settings", DiagnosticRedactor.Redact(error.Message)); }
    }

    private void AddPluginDetails(PluginHost.Entry entry, StackPanel actions, StackPanel content)
    {
        _pluginSettingsSessions.TryGetValue(entry.Directory, out var session);
        var button = new Button
        {
            Content = session?.Error.Length > 0 ? L.T("重试设置") : L.T("插件设置"),
            Tag = "plugin-settings:" + entry.Directory,
            Style = (Style)FindResource("W11Button"), Margin = new Thickness(10, 0, 0, 0),
            IsEnabled = !entry.UninstallPending && session?.Loading != true
                && !(entry.Instance != null && !entry.Running && entry.Error.Length == 0)
        };
        System.Windows.Automation.AutomationProperties.SetName(button, L.T("设置") + entry.Manifest!.Name);
        button.Click += (_, _) =>
        {
            if (entry.Running) content.Children.OfType<FrameworkElement>().Skip(2).FirstOrDefault()?.BringIntoView();
            else OpenPluginDetails(entry);
        };
        actions.Children.Add(button);
        var context = entry.Running ? entry.Context : session?.Ready == true ? session.Context : null;
        if (context != null)
        {
            foreach (var setting in context.Settings) content.Children.Add(CreatePluginSettingRow(entry.Manifest.Id, setting));
            if (context.SettingsView != null)
            {
                try { content.Children.Add(context.SettingsView()); }
                catch (Exception error) { content.Children.Add(PluginDetailsHint(L.T("设置暂不可用：") + DiagnosticRedactor.Redact(error.Message))); }
            }
            else if (context.Settings.Count == 0) content.Children.Add(PluginDetailsHint(L.T("此插件没有额外设置")));
        }
        else if (session?.Loading == true) content.Children.Add(PluginDetailsHint(L.T("正在加载插件设置…")));
        else if (session?.Error.Length > 0) content.Children.Add(PluginDetailsHint(L.T("设置暂不可用：") + session.Error));
        else if (!entry.Running && !entry.UninstallPending)
            content.Children.Add(PluginDetailsHint(entry.GameSuspended ? L.T("后台已暂停，仍可单独打开设置") : L.T("点击“插件设置”修改配置")));
    }

    private TextBlock PluginDetailsHint(string text)
    {
        var hint = PluginText(text, "W11RowHint"); hint.Margin = new Thickness(16, 0, 16, 14); return hint;
    }
}
