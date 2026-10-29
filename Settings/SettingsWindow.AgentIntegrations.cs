using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Services;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    private StackPanel _integrationProviders = null!;
    private TextBlock _integrationStatus = null!;
    private bool _integrationBusy;

    private void AddAgentIntegrationSettings()
    {
        var panel = new StackPanel { Tag = L.T("Agent 自动接入 Codex Antigravity Claude Code DeepSeek Harness DSH 开箱即用") };
        panel.Children.Add(new TextBlock { Text = L.T("外部 Agent 自动接入"), Style = (Style)FindResource("W11CardTitle"), Margin = new Thickness(0, 26, 0, 12) });
        var content = new StackPanel { Margin = new Thickness(16) };
        content.Children.Add(new TextBlock { Text = L.T("自动识别本机配置。安装或首次启动时接入已检测到的应用；停用选择会保留。运行环境随安装包提供。"),
            Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 8) };
        var detect = new Button { Content = L.T("重新检测"), Style = (Style)FindResource("W11Button") };
        detect.Click += async (_, _) => await RefreshAgentIntegrationsAsync();
        var setup = new Button { Content = L.T("接入检测到的应用"), Style = (Style)FindResource("W11Button"), Margin = new Thickness(8, 0, 0, 0) };
        setup.Click += async (_, _) => await RefreshAgentIntegrationsAsync("Auto");
        actions.Children.Add(detect); actions.Children.Add(setup); content.Children.Add(actions);
        _integrationStatus = new TextBlock { Text = L.T("打开设置后自动检测。"), Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap };
        content.Children.Add(_integrationStatus);
        panel.Children.Add(SettingCard(content));
        _integrationProviders = new StackPanel(); panel.Children.Add(_integrationProviders);
        Page_Ai.Children.Insert(1, panel);
        IsVisibleChanged += async (_, e) => { if (e.NewValue is true && _persistSettings) await RefreshAgentIntegrationsAsync(); };
    }

    private async Task RefreshAgentIntegrationsAsync(string mode = "Detect", string provider = "all", string? project = null, string? harness = null, bool allProjects = false)
    {
        if (_integrationBusy || !_persistSettings) return;
        _integrationBusy = true; _integrationProviders.IsEnabled = false;
        _integrationStatus.Text = mode == "Detect" ? L.T("正在检测本机应用…") : L.T("正在更新接入…");
        try
        {
            var report = await AgentIntegrationService.RunAsync(mode, SettingsRegistry.GetInt(_options, "AgentBridgePort", 8765), provider, project, harness, allProjects: allProjects);
            _integrationStatus.Text = report.Error ?? (report.RuntimeAvailable ? L.T("接入运行环境已就绪。应用安装或位置变化后可重新检测。") : L.T("接入运行环境缺失，请修复安装。"));
            _integrationProviders.Children.Clear();
            foreach (var item in report.Providers)
            {
                var row = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
                row.Children.Add(new TextBlock { Text = item.Name, Style = (Style)FindResource("W11RowLabel") });
                row.Children.Add(new TextBlock { Text = item.StatusText + (item.AllProjects ? L.T("\n项目范围：所有项目") : item.Scope.Length > 0 ? L.T("\n项目范围：") + item.Scope : "") +
                    (item.Profile.Length > 0 && item.Detected ? L.T("\n配置档：") + item.Profile : ""), Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap });
                var buttons = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                var connect = new Button { Content = L.T("接入 / 修复"), Style = (Style)FindResource("W11Button"), IsEnabled = item.Detected && item.Status != "scope-required" };
                connect.Click += async (_, _) => await RefreshAgentIntegrationsAsync("Install", item.Id);
                buttons.Children.Add(connect);
                if (item.Id == "codex")
                {
                    var check = new Button { Content = L.T("检查状态通道"), Style = (Style)FindResource("W11Button"), Margin = new Thickness(8, 0, 0, 0), IsEnabled = item.Detected };
                    check.Click += async (_, _) => await RefreshAgentIntegrationsAsync("Check", "codex");
                    buttons.Children.Add(check);
                    var global = new Button { Content = L.T("所有项目"), Style = (Style)FindResource("W11Button"), Margin = new Thickness(8, 0, 0, 0), IsEnabled = item.Detected };
                    global.Click += async (_, _) => await RefreshAgentIntegrationsAsync("Install", "codex", allProjects: true);
                    buttons.Children.Add(global);
                }
                if (item.Id is "codex" or "deepseek-harness")
                {
                    var select = new Button { Content = item.Id == "codex" ? L.T("选择项目") : L.T("其他 DSH 目录"), Style = (Style)FindResource("W11Button"), Margin = new Thickness(8, 0, 0, 0) };
                    select.Click += async (_, _) =>
                    {
                        var picker = new Microsoft.Win32.OpenFolderDialog { Title = item.Id == "codex" ? L.T("选择允许接入的项目目录") : L.T("选择包含 profiles 的 DSH 配置目录") };
                        if (picker.ShowDialog(this) == true) await RefreshAgentIntegrationsAsync("Install", item.Id,
                            item.Id == "codex" ? picker.FolderName : null, item.Id == "deepseek-harness" ? picker.FolderName : null);
                    };
                    buttons.Children.Add(select);
                }
                var disable = new Button { Content = L.T("停用接入"), Style = (Style)FindResource("W11Button"), Margin = new Thickness(8, 0, 0, 0), IsEnabled = item.Status == "configured" };
                disable.Click += async (_, _) => await RefreshAgentIntegrationsAsync("Remove", item.Id);
                buttons.Children.Add(disable);
                foreach (Button action in buttons.Children) action.Margin = new Thickness(action.Margin.Left, 4, 0, 4);
                row.Children.Add(buttons); _integrationProviders.Children.Add(SettingCard(row));
            }
            if (mode is "Auto" or "Install" && report.Providers.Any(p => p.Status == "configured"))
            {
                AgentBridgeToggle.IsChecked = true; // 用户点击接入时同时启用本机桥接，走现有设置保存链路。
            }
            if (mode == "Check")
                _integrationStatus.Text = await AgentIntegrationChannelCheck.CheckAsync(SettingsRegistry.GetInt(_options, "AgentBridgePort", 8765));
        }
        finally { _integrationBusy = false; _integrationProviders.IsEnabled = true; }
    }
}
