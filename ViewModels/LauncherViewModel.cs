using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using alpha_island.Core;

namespace alpha_island.ViewModels;

internal sealed class LauncherViewModel : ObservableObject
{
    private LauncherEntry[] _entries = [];
    private string _query = "", _status = "";
    private LauncherEntry? _selected;
    public ObservableCollection<LauncherEntry> Results { get; } = new();
    public string Query { get => _query; set { if (SetProperty(ref _query, value)) Search(); } }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public LauncherEntry? Selected { get => _selected; set => SetProperty(ref _selected, value); }
    public IAsyncRelayCommand RunCommand { get; }
    internal LauncherViewModel(Func<LauncherEntry, Task> execute)
    {
        RunCommand = new AsyncRelayCommand(async () =>
        {
            if (Selected == null) return;
            try { await execute(Selected); }
            catch (Exception ex) { Status = L.T("打开失败：") + ex.GetBaseException().Message; }
        });
    }
    internal void SetEntries(IEnumerable<LauncherEntry> entries) { _entries = entries.ToArray(); Search(); }
    private void Search()
    {
        string? id = Selected?.Id; Results.Clear();
        foreach (var item in LauncherSearch.Search(_entries, Query)) Results.Add(item);
        if (Query.Trim().Length > 0)
            Results.Add(new("ai:prompt", L.T("向 AI 提问：") + Query.Trim(), L.T("打开 AI 对话并填入文本，确认后再发送"), LauncherKind.AiPrompt, Query.Trim()));
        Selected = Results.FirstOrDefault(item => item.Id == id) ?? Results.FirstOrDefault();
    }
}
