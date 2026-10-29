using L = AlphaIsland.PluginSdk.Localization.UiText;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AlphaIsland.PluginSdk;
using alpha_island.Core;
using alpha_island.Plugins;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    private PluginHost? _pluginHost;
    private bool _pluginRefreshPending;
    internal void BindPluginHost(PluginHost host)
    {
        InitializePluginSettingsLifetime();
        UnbindPluginHost(); _pluginHost = host; host.Changed += QueuePluginRefresh;
        RebuildPluginRows();
    }
    private void UnbindPluginHost()
    { ReleasePluginSettingsSessions(); if (_pluginHost != null) _pluginHost.Changed -= QueuePluginRefresh; _pluginHost = null; }
    private void QueuePluginRefresh()
    {
        if (_pluginRefreshPending) return;
        _pluginRefreshPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        { _pluginRefreshPending = false; if (_pluginHost != null) RebuildPluginRows(); }));
    }
    private TextBlock PluginText(string text, string style) => new() { Text = text, Style = (Style)FindResource(style), TextWrapping = TextWrapping.Wrap };
    private void RebuildPluginRows()
    {
        if (PluginRows == null) return;
        PrunePluginSettingsSessions();
        PluginRows.Children.Clear();
        if (_pluginHost == null || _pluginHost.Entries.Count == 0)
        {
            PluginRows.Children.Add(PluginText(L.T("暂无插件"), "W11RowHint"));
            return;
        }
        foreach (var entry in _pluginHost.Entries)
        {
            var content = new StackPanel();
            var card = new Border { Style = (Style)FindResource("W11Card"), Margin = new Thickness(0, 0, 0, 12), Child = content };
            var header = new Grid { Margin = new Thickness(16, 14, 16, 14) };
            header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var labels = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
            labels.Children.Add(PluginText(entry.Manifest is { } manifest ? $"{L.T(manifest.Name)}  ·  {manifest.Version}" : Path.GetFileName(entry.Directory), "W11RowLabel"));
            string status = PluginHost.StateText(entry, entry.Manifest != null && PluginHost.IsEnabled(_options, entry.Manifest.Id));
            labels.Children.Add(PluginText(status, "W11RowHint"));
            if (entry.Manifest is { Description.Length: > 0 }) labels.ToolTip = entry.Manifest.Description;
            header.Children.Add(labels); content.Children.Add(header);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 16, 14) };
            var openDirectory = new Button
            {
                Content = L.T("打开此插件目录"), Style = (Style)FindResource("W11Button"),
                Margin = new Thickness(0, 0, 10, 0),
                Tag = "plugin-folder:" + entry.Directory, ToolTip = entry.Directory
            };
            openDirectory.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(entry.Directory) { UseShellExecute = true }); }
                catch (Exception ex) { IslandLog.Warn("Plugins", "打开插件目录失败：" + ex.Message); }
            };
            actions.Children.Add(openDirectory);
            var uninstall = new Button
            {
                Content = entry.UninstallPending ? L.T("重试卸载") : L.T("卸载"), Style = (Style)FindResource("W11Button"),
                Tag = "plugin-uninstall:" + entry.Directory, ToolTip = L.T("停用插件并删除此插件目录和设置")
            };
            System.Windows.Automation.AutomationProperties.SetName(uninstall, L.T("卸载") + (entry.Manifest?.Name ?? Path.GetFileName(entry.Directory)));
            uninstall.Click += (_, _) => UninstallPlugin(entry);
            actions.Children.Add(uninstall); content.Children.Add(actions);
            if (entry.Manifest != null)
            {
                string id = entry.Manifest.Id;
                var toggle = new CheckBox { Style = (Style)FindResource("W11Switch"), VerticalAlignment = VerticalAlignment.Center,
                    IsChecked = PluginHost.IsEnabled(_options, id), IsEnabled = !entry.UninstallPending, Tag = PluginHost.EnabledKey(id) };
                Grid.SetColumn(toggle, 1); header.Children.Add(toggle);
                System.Windows.Automation.AutomationProperties.SetName(toggle, L.T("启用") + entry.Manifest.Name);
                toggle.Checked += (_, _) => SetPluginValue(PluginHost.EnabledKey(id), true);
                toggle.Unchecked += (_, _) => SetPluginValue(PluginHost.EnabledKey(id), false);
                AddPluginDetails(entry, actions, content);
            }
            PluginRows.Children.Add(card);
        }
    }
    private async void UninstallPlugin(PluginHost.Entry entry)
    {
        var host = _pluginHost;
        if (host == null || _suppress) return;
        bool removed = await host.UninstallAsync(entry).ConfigureAwait(false);
        if (Dispatcher.HasShutdownStarted) return;
        await Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_pluginHost, host)) return;
            if (entry.Manifest != null)
            {
                if (removed) PluginHost.ForgetSettings(_options, entry.Manifest.Id);
                else _options.Extras[PluginHost.EnabledKey(entry.Manifest.Id)] = JsonSerializer.SerializeToElement(false);
            }
            ApplyChanges(); RebuildPluginRows();
        });
    }
    private FrameworkElement CreatePluginSettingRow(string id, PluginSetting setting)
    {
        string key = PluginHost.SettingKey(id, setting.Key);
        var value = PluginHost.NormalizeValue(setting, _options.Extras.TryGetValue(key, out var saved) ? saved : JsonSerializer.SerializeToElement(setting.DefaultValue));
        var row = new Grid { Margin = new Thickness(16, 0, 16, 14), Tag = key };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(PluginText(setting.Label, "W11RowLabel"));
        if (setting.Description.Length > 0) labels.Children.Add(PluginText(setting.Description, "W11RowHint"));
        row.Children.Add(labels);
        FrameworkElement editor;
        switch (setting.Kind)
        {
            case PluginSettingKind.Boolean:
                var check = new CheckBox { Style = (Style)FindResource("W11Switch"), IsChecked = value.GetBoolean() };
                check.Checked += (_, _) => SetPluginValue(key, true); check.Unchecked += (_, _) => SetPluginValue(key, false);
                editor = check; break;
            case PluginSettingKind.Number:
                var number = new Slider { Style = (Style)FindResource("W11Slider"), Width = 140, Minimum = setting.Minimum,
                    Maximum = setting.Maximum, TickFrequency = setting.Step, IsSnapToTickEnabled = true, Value = value.GetDouble(), ToolTip = value.ToString() };
                number.ValueChanged += (_, _) => { number.ToolTip = number.Value.ToString("0.##"); SetPluginValue(key, number.Value); };
                editor = number; break;
            case PluginSettingKind.Choice:
                var choice = new ComboBox { Width = 160, ItemsSource = (setting.Choices ?? []).Select(item => new KeyValuePair<string, string>(item, L.T(item))).ToArray(),
                    DisplayMemberPath = "Value", SelectedValuePath = "Key", SelectedValue = value.GetString() };
                choice.SelectionChanged += (_, _) => { if (choice.SelectedValue is string selected) SetPluginValue(key, selected); };
                editor = choice; break;
            default:
                var text = new TextBox { Width = 160, Text = value.GetString() ?? "", MaxLength = 1024 };
                text.TextChanged += (_, _) => SetPluginValue(key, text.Text);
                editor = text; break;
        }
        editor.Tag = key; editor.VerticalAlignment = VerticalAlignment.Center;
        System.Windows.Automation.AutomationProperties.SetName(editor, setting.Label);
        Grid.SetColumn(editor, 1); row.Children.Add(editor); return row;
    }
    private void SetPluginValue(string key, object value)
    {
        if (_suppress) return;
        _options.Extras[key] = JsonSerializer.SerializeToElement(value);
        _settingsEdited = true; ApplyChanges();
    }
    private void RefreshPlugins_Click(object sender, RoutedEventArgs e) => _pluginHost?.Discover();
    private void OpenAiPluginSettings_Click(object sender, RoutedEventArgs e) => SelectPageByTag("plugins");
    internal void OpenPluginSettings() => SelectPageByTag("plugins");
    internal void SynchronizePluginConfiguration(IslandOptions source)
    {
        foreach (var pair in source.Extras.Where(p => p.Key.StartsWith("plugin/", StringComparison.Ordinal) || p.Key.StartsWith("Ai", StringComparison.Ordinal)))
            _options.Extras[pair.Key] = pair.Value;
        foreach (var session in _pluginSettingsSessions.Values) session.Synchronize();
    }
    private void OpenPluginsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.UserPluginsDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.UserPluginsDir) { UseShellExecute = true });
        }
        catch (Exception ex) { IslandLog.Warn("Plugins", "打开插件目录失败：" + ex.Message); }
    }
}
