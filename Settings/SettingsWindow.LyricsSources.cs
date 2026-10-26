using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Services;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    private StackPanel _lyricsSourceEditor = null!, _lyricsSourceRows = null!, _lyricsHttpEditor = null!;
    private TextBlock _lyricsSourceStatus = null!;
    private TextBox _lyricsSourceName = null!, _lyricsSourceUrl = null!;
    private ComboBox _lyricsSourceFormat = null!;
    private LocalLyricsStore _localLyricsStore = null!;
    private string _lyricsSourceRuntime = "尚未命中";

    private void AddLyricsSourceSettings()
    {
        _localLyricsStore = new LocalLyricsStore(Path.Combine(Path.GetDirectoryName(_greetingAssets.DirectoryPath)!, "lyrics", "local"));
        var row = (Grid)LyricsSourceCard.Child;
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        ((StackPanel)row.Children[1]).Children.OfType<TextBlock>().Last().TextWrapping = TextWrapping.Wrap;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(LyricsButton("自定义", "LyricsSourcesCustomize", () => _lyricsSourceEditor.Visibility = _lyricsSourceEditor.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible));
        buttons.Children.Add(LyricsButton("重置", "LyricsSourcesReset", ResetLyricsSources));
        Grid.SetColumn(buttons, 2); row.Children.Add(buttons);

        _lyricsSourceEditor = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 8) };
        _lyricsSourceEditor.Children.Add(new TextBlock { Text = "优先级从上到下，可关闭暂不使用的来源。", Style = (Style)FindResource("W11RowHint"), Margin = new Thickness(12, 4, 12, 10), TextWrapping = TextWrapping.Wrap });
        _lyricsSourceRows = new StackPanel(); _lyricsSourceEditor.Children.Add(_lyricsSourceRows);
        var actions = new WrapPanel { Margin = new Thickness(8, 8, 0, 4) };
        actions.Children.Add(LyricsButton("添加接口", "LyricsSourcesAdd", () => _lyricsHttpEditor.Visibility = _lyricsHttpEditor.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible));
        actions.Children.Add(LyricsButton("导入来源 JSON", "LyricsSourcesImport", ChooseLyricsSources));
        actions.Children.Add(LyricsButton("导入本地歌词", "LyricsLocalImport", ChooseLocalLyrics));
        _lyricsSourceEditor.Children.Add(actions);
        _lyricsHttpEditor = new StackPanel { Margin = new Thickness(12), Visibility = Visibility.Collapsed };
        _lyricsSourceName = LyricsInput("来源名称", "LyricsSourceName", "例如：我的歌词接口");
        _lyricsSourceUrl = LyricsInput("接口地址", "LyricsSourceUrl", "https://example.com/lyrics?title={title}&artist={artist}");
        _lyricsSourceFormat = new ComboBox { ItemsSource = new[] { "JSON（标准字段）", "LRC 文本" }, SelectedIndex = 0, MinHeight = 32, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, MinWidth = 180, Margin = new Thickness(0, 0, 0, 8) };
        _lyricsHttpEditor.Children.Add(_lyricsSourceFormat);
        _lyricsHttpEditor.Children.Add(LyricsButton("添加", "LyricsSourceConfirm", AddHttpLyricsSource));
        _lyricsSourceEditor.Children.Add(_lyricsHttpEditor);
        var guide = new Expander { Header = "导入说明与规范", Margin = new Thickness(12, 6, 12, 6) };
        guide.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "W11TextPrimary");
        var help = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        help.Children.Add(new TextBlock { Text = "外部来源：导入 schemaVersion 为 1 的 JSON，可配置 URL 占位符和响应字段。\n本地歌词：支持 UTF-8 / Unicode LRC；建议包含 [ti:歌名]、[ar:歌手]。导入后保留本机副本。\n默认来源为网易云、LRCLIB、酷狗。优先等待首项 1.2 秒，超时后由其他来源补充。\n重置只恢复来源设置，保留已导入的歌词。", Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap });
        help.Children.Add(LyricsButton("打开规范与示例", "LyricsSourcesGuide", OpenLyricsSourceGuide));
        guide.Content = help; _lyricsSourceEditor.Children.Add(guide);
        _lyricsSourceStatus = new TextBlock { Style = (Style)FindResource("W11RowHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 4, 12, 8) };
        _lyricsSourceEditor.Children.Add(_lyricsSourceStatus);
        ((StackPanel)LyricsSourceCard.Parent).Children.Add(_lyricsSourceEditor);
    }

    private TextBox LyricsInput(string label, string name, string tip)
    {
        _lyricsHttpEditor.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("W11RowLabel"), Margin = new Thickness(0, 0, 0, 4) });
        var box = new TextBox { Name = name, Style = (Style)FindResource("W11TextBox"), MinHeight = 32, Margin = new Thickness(0, 0, 0, 10), ToolTip = tip };
        RegisterName(name, box); _lyricsHttpEditor.Children.Add(box); return box;
    }

    private Button LyricsButton(string text, string? name, Action action)
    {
        var button = new Button { Content = text, Style = (Style)FindResource("W11Button"), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(4, 0, 4, 6), MinHeight = 30, VerticalAlignment = VerticalAlignment.Center };
        if (name != null) { button.Name = name; RegisterName(name, button); }
        button.Click += (_, _) => action(); return button;
    }

    private void RefreshLyricsSourceSummary() => LyricsSourceText.Text = LyricsSourceSettings.Summary(_options) + " · 当前：" + _lyricsSourceRuntime;

    private void RefreshLyricsSourceSettings()
    {
        if (_lyricsSourceRows == null) return;
        RefreshLyricsSourceSummary(); _lyricsSourceRows.Children.Clear();
        var sources = LyricsSourceSettings.Read(_options);
        for (int index = 0; index < sources.Count; index++)
        {
            int position = index;
            var source = sources[index];
            var row = new Grid { Style = (Style)FindResource("W11Row") };
            row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var toggle = new CheckBox { Style = (Style)FindResource("W11Switch"), IsChecked = source.Enabled, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                ToolTip = source.Kind == "local" ? "本机保存的 LRC 歌词" : source.Kind == "builtin" ? "内置来源" : source.Name + "\n" + source.UrlTemplate };
            toggle.Checked += (_, _) => SetLyricsSourceEnabled(source.Id, true);
            toggle.Unchecked += (_, _) => SetLyricsSourceEnabled(source.Id, false);
            var label = new Grid { Margin = new Thickness(0, 0, 12, 0) };
            label.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); label.ColumnDefinitions.Add(new());
            label.Children.Add(toggle);
            var name = new TextBlock { Text = source.Name, TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)FindResource("W11RowLabel"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1); label.Children.Add(name); row.Children.Add(label);
            var commands = new StackPanel { Orientation = Orientation.Horizontal };
            var up = LyricsButton("↑", null, () => MoveLyricsSource(position, -1)); up.ToolTip = "提高优先级"; up.IsEnabled = index > 0;
            var down = LyricsButton("↓", null, () => MoveLyricsSource(position, 1)); down.ToolTip = "降低优先级"; down.IsEnabled = index < sources.Count - 1;
            commands.Children.Add(up); commands.Children.Add(down);
            if (!LyricsSourceSettings.IsDefault(source)) commands.Children.Add(LyricsButton("移除", null, () => RemoveLyricsSource(source.Id)));
            Grid.SetColumn(commands, 1); row.Children.Add(commands); _lyricsSourceRows.Children.Add(SettingCard(row));
        }
    }

    private void CommitLyricsSources(IEnumerable<LyricsSourceDefinition> sources)
    {
        LyricsSourceSettings.Write(_options, sources); RefreshLyricsSourceSettings(); ApplyChanges();
    }
    private void ResetLyricsSources() { CommitLyricsSources(LyricsSourceSettings.Defaults()); _lyricsSourceStatus.Text = "已恢复默认来源；本地歌词仍保留。"; }
    private void SetLyricsSourceEnabled(string id, bool enabled) => CommitLyricsSources(LyricsSourceSettings.Read(_options).Select(s => s.Id == id ? s with { Enabled = enabled } : s));
    private void MoveLyricsSource(int index, int delta)
    {
        var sources = LyricsSourceSettings.Read(_options); int target = index + delta;
        if (index < 0 || index >= sources.Count || target < 0 || target >= sources.Count) return;
        (sources[index], sources[target]) = (sources[target], sources[index]); CommitLyricsSources(sources);
    }
    private void RemoveLyricsSource(string id)
    {
        var sources = LyricsSourceSettings.Read(_options);
        CommitLyricsSources(sources.Count > 1 ? sources.Where(s => s.Id != id) : sources.Select(s => s with { Enabled = false }));
    }

    private void AddHttpLyricsSource()
    {
        try
        {
            var sources = LyricsSourceSettings.Read(_options);
            sources.Add(new() { Name = _lyricsSourceName.Text.Trim(), UrlTemplate = _lyricsSourceUrl.Text.Trim(), Format = _lyricsSourceFormat.SelectedIndex == 1 ? "lrc" : "json" });
            CommitLyricsSources(sources); _lyricsHttpEditor.Visibility = Visibility.Collapsed;
            _lyricsSourceName.Clear(); _lyricsSourceUrl.Clear(); _lyricsSourceStatus.Text = "来源已添加，可调整优先级。";
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException) { _lyricsSourceStatus.Text = e.Message; }
    }

    private void ChooseLyricsSources()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "导入歌词来源配置", Filter = "歌词来源配置|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try { ImportLyricsSources(LocalLyricsStore.ReadText(dialog.FileName)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException) { _lyricsSourceStatus.Text = "未更改来源：" + e.Message; }
    }
    private void ImportLyricsSources(string json)
    {
        var incoming = LyricsSourceSettings.Import(json);
        var sources = LyricsSourceSettings.Read(_options);
        foreach (var source in incoming) { int index = sources.FindIndex(s => s.Id == source.Id); if (index >= 0) sources[index] = source; else sources.Add(source); }
        CommitLyricsSources(sources); _lyricsSourceStatus.Text = $"已导入 {incoming.Count} 个来源，可调整优先级。";
    }

    private async void ChooseLocalLyrics()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "导入本地 LRC 歌词", Filter = "LRC 歌词|*.lrc", CheckFileExists = true, Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        if (FindName("LyricsLocalImport") is not Button button) return;
        button.IsEnabled = false;
        try
        {
            var priorSources = LyricsSourceSettings.Read(_options);
            if (priorSources.Count >= LyricsSourceSettings.MaxSources && priorSources.All(s => s.Id != "local")) throw new InvalidDataException("来源已满，请先移除一个自定义来源。");
            int count = await Task.Run(() => _localLyricsStore.Import(dialog.FileNames));
            var sources = LyricsSourceSettings.Read(_options);
            sources.RemoveAll(s => s.Id == "local"); sources.Insert(0, new() { Id = "local", Name = "本地歌词", Kind = "local" });
            SettingsRegistry.SetExtra(_options, "LyricsLibraryRevision", Guid.NewGuid().ToString("N"));
            CommitLyricsSources(sources); _lyricsSourceStatus.Text = $"已导入 {count} 首，本地库共 {_localLyricsStore.Entries.Count} 首。";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { _lyricsSourceStatus.Text = "未更改本地库：" + e.Message; }
        finally { button.IsEnabled = true; }
    }

    private void OpenLyricsSourceGuide()
    {
        try
        {
            string directory = Path.Combine(Path.GetDirectoryName(_localLyricsStore.DirectoryPath)!, "导入说明");
            Directory.CreateDirectory(directory);
            foreach (string name in new[] { "歌词源导入规范.md", "歌词源示例.json", "本地歌词示例.lrc" })
            {
                using var resource = typeof(SettingsWindow).Assembly.GetManifestResourceStream("AlphaIsland.Lyrics." + name) ?? throw new IOException("导入说明资源不可用。");
                string path = Path.Combine(directory, name);
                if (!File.Exists(path)) { using var output = File.Create(path); resource.CopyTo(output); }
            }
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { _lyricsSourceStatus.Text = "无法打开说明：" + e.Message; }
    }
}
