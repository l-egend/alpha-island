using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using alpha_island.Core;
using alpha_island.Services;

namespace alpha_island.ViewModels;

/// <summary>The window composes views and animations; these objects own application state.</summary>
internal sealed class IslandWorkspaceViewModel
{
    public SystemResourcesViewModel Resources { get; } = new();
    public MediaViewModel Media { get; } = new();
    public DownloadsViewModel Downloads { get; } = new();
    public NotificationsViewModel Notifications { get; } = new();
    public AgentSessionsViewModel Agents { get; } = new();
    public FocusViewModel Focus { get; } = new();
    public PageSelectionViewModel Pages { get; } = new();
}

internal readonly record struct MediaTransition(bool TrackChanged, bool PlaybackChanged, bool HadMedia, bool? WasPlaying);
internal sealed class MediaViewModel : ObservableObject
{
    private NowPlayingInfo? _current;
    private bool _hidden;
    public NowPlayingInfo? Current { get => _current; private set => SetProperty(ref _current, value); }
    public bool Hidden { get => _hidden; set => SetProperty(ref _hidden, value); }
    public MediaTransition Preview(NowPlayingInfo info) => new(Current == null ||
        (Current.Title, Current.Artist, Current.SourceApp, Current.SessionId, Current.ContentKind) !=
        (info.Title, info.Artist, info.SourceApp, info.SessionId, info.ContentKind),
        Current != null && Current.IsPlaying != info.IsPlaying, Current != null, Current?.IsPlaying);
    public void Commit(NowPlayingInfo info) => Current = info;
    public void Reset() { Current = null; Hidden = false; }
}

internal sealed class PageSelectionViewModel : ObservableObject
{
    internal IslandPageHost Host { get; } = new();
    public string SelectedId => Host.ActiveId;
    public string Title => Host.ActivePage?.Title ?? "";
    public IRelayCommand<string> SelectCommand { get; }
    public PageSelectionViewModel()
    {
        SelectCommand = new RelayCommand<string>(id => { if (id != null && Host.IndexOf(id) is >= 0 and var index) Host.SetActiveIndex(index); });
        Host.ActiveChanged += () => { OnPropertyChanged(nameof(SelectedId)); OnPropertyChanged(nameof(Title)); };
    }
}

internal sealed class NotificationsViewModel : ObservableObject
{
    internal DesktopNotificationInbox Inbox { get; } = new();
    internal DesktopNotificationBurst Burst { get; } = new();
    internal HashSet<string>? ExpandedKeys { get; set; }
    internal HashSet<string> ExpandedApplications { get; } = new(StringComparer.Ordinal);
    internal DesktopNotificationPolicy? Policy { get; set; }
    private DesktopNotification? _current;
    public DesktopNotification? Current { get => _current; set => SetProperty(ref _current, value); }
    internal DateTimeOffset Expires { get; set; }
    private LocalStateFile<DesktopNotification[]>? _archive;
    private bool _remember;
    private bool _archiveDirty;
    private IslandTicker.Subscription? _archiveTick;
    public NotificationImportance MinimumImportance { get; set; }
    public string Search { get; set; } = "";
    internal bool UnreadOnly { get; set; }
    internal void AttachArchive(string path, bool remember, IslandTicker ticker)
    {
        _archive = new(path, maximumBytes: 8 * 1024 * 1024, encrypted: true); _remember = remember;
        if (remember)
            foreach (var item in (_archive.Read() ?? []).Where(Valid).OrderBy(item => item.Created).TakeLast(100)) Inbox.Add(item, false);
        _archiveTick = ticker.Subscribe("notifications/archive", TimeSpan.FromSeconds(2), FlushArchive);
        _archiveTick.SetPaused(true); Inbox.Changed += MarkArchiveDirty;
        if (!remember) _archive.Write([]);
    }
    internal void SetRemember(bool value)
    { if (_remember == value) return; _remember = value; MarkArchiveDirty(); }
    internal IEnumerable<DesktopNotification> Filter(DesktopNotificationPolicy policy) => Inbox.History
        .Where(item => (!UnreadOnly || !item.IsRead && item.Kind == DesktopEventKind.Message)
            && (ExpandedKeys == null || ExpandedKeys.Contains(item.Key)) && policy.Accepts(item) && policy.Level(item) >= MinimumImportance
            && (Search.Length == 0 || (item.AppName + " " + item.Title + " " + item.Body).Contains(Search, StringComparison.OrdinalIgnoreCase)))
        .OrderByDescending(item => item.IsPinned).ThenByDescending(item => item.Created);
    private static bool Valid(DesktopNotification? item) => item != null && !string.IsNullOrWhiteSpace(item.Key)
        && item.Key.Length <= 2048 && item.Title != null && item.Body != null && item.AppId != null && item.AppName != null && item.ActionFeedback is { Length: <= 256 }
        && Enum.IsDefined(item.Kind) && Enum.IsDefined(item.Importance)
        && (item.Reminder == null || item.Kind == DesktopEventKind.Assistant || item.IsCritical)
        && (item.Reminder == null || AssistantReminderRules.Valid(item.Reminder));
    private void MarkArchiveDirty() { _archiveDirty = true; _archiveTick?.SetPaused(false); }
    private void FlushArchive()
    {
        _archiveTick?.SetPaused(true);
        if (!_archiveDirty) return; _archiveDirty = false;
        _archive?.Write(_remember ? Inbox.History.Take(100).Select(item => item with
    {
        Logo = null, ContentImage = null,
        Title = item.Title[..Math.Min(item.Title.Length, 2048)], Body = item.Body[..Math.Min(item.Body.Length, 4096)],
        AppId = item.AppId[..Math.Min(item.AppId.Length, 2048)], AppName = item.AppName[..Math.Min(item.AppName.Length, 256)], ActionFeedback = item.ActionFeedback[..Math.Min(item.ActionFeedback.Length, 256)]
        }).ToArray() : []);
    }
    internal void Dispose() { Inbox.Changed -= MarkArchiveDirty; FlushArchive(); _archiveTick?.Dispose(); _archive?.Dispose(); }
}

internal sealed class FocusViewModel : ObservableObject
{
    internal PomodoroTimer Timer { get; } = new();
    public string Remaining => Timer.DisplayRemaining();
    public string ButtonText => Timer.Running ? "暂停" : Timer.CycleFinished ? "重新开始" : Timer.IsBreak ? "开始休息" : "开始专注";
    public string Detail => $"{(Timer.IsLongBreak ? "长休息" : Timer.IsBreak ? "短休息" : "专注阶段")} · 已完成 {Timer.CompletedFocus} 轮"
        + (Timer.Options.AutoCycle ? $" / {Timer.Options.CycleCount} 轮计划" : "");
    public bool Running => Timer.Running;
    public IRelayCommand ToggleCommand { get; }
    public IRelayCommand ResetCommand { get; }
    public FocusViewModel()
    {
        ToggleCommand = new RelayCommand(() => Toggle(DateTime.UtcNow));
        ResetCommand = new RelayCommand(() => { Timer.Reset(); Refresh(); });
    }
    internal event Action? Changed;
    internal event Action? PhaseCompleted;
    internal void Configure(PomodoroOptions options) { Timer.Configure(options); Refresh(); }
    internal void Toggle(DateTime now) { if (Timer.Running) Timer.Pause(now); else Timer.Start(now); Refresh(); }
    internal void Tick(DateTime now) { bool completed = Timer.Tick(now); Refresh(); if (completed) PhaseCompleted?.Invoke(); }
    internal void Refresh()
    {
        OnPropertyChanged(nameof(Remaining)); OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(Detail)); OnPropertyChanged(nameof(Running)); Changed?.Invoke();
    }
}
