using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AlphaIsland.AgentTools;
using AlphaIsland.AgentTools.IslandTools;
using AlphaIsland.PluginSdk;
using alpha_island.Core;
using alpha_island.Services;
using Button = System.Windows.Controls.Button;

namespace alpha_island;

public partial class MainWindow
{
    private DesktopAssistantService? _desktopAssistant;
    private ToolRegistry? _desktopTools;
    private volatile bool _desktopToolsEnabled = true;
    private AssistantReminderService? _assistantReminders;
    private IIslandToolHost? _islandToolHost;
    private void InitializeDesktopAssistant()
    {
        if (_desktopAssistant != null) return;
        _desktopTools = new(() => _desktopToolsEnabled && !_exiting && !_sessionLocked);
        var adapter = new IslandToolHostAdapter(Dispatcher, () => _volumeService, () => _smtc,
            _workspace.Focus, () => PomodoroOptions.From(_options), ReadLatestDesktopState, ExecuteUserReminderTool, ReadIslandInformation);
        _islandToolHost = adapter;
        foreach (var tool in DesktopTools.Create(adapter)) _desktopTools.Register("island", tool, _utilitiesLifetime.Token);
        _desktopAssistant = new(Dispatcher, IslandTicker.Shared(Dispatcher), ObserveDesktop,
            key => SettingsRegistry.GetBool(_options, key, true), _runtimeEnabled ? Path.Combine(AppPaths.Root, "timeline.dat") : null);
        ConfigureDesktopAssistant();
        _assistantReminders = new(Dispatcher, IslandTicker.Shared(Dispatcher), ReadAssistantFrame,
            _runtimeEnabled ? Path.Combine(AppPaths.Root, "assistant-reminders.dat") : null);
        _assistantReminders.ChangesReady += ApplyAssistantNotificationChanges;
        _assistantReminders.Configure(AssistantRuleOptions.From(_options));
        InitializePersonalTools();
    }
    private void ConfigureDesktopAssistant()
    {
        _desktopToolsEnabled = SettingsRegistry.GetBool(_options, "AgentDesktopToolsEnabled", true);
        if (_plugins != null) _plugins.AllowPluginTools = SettingsRegistry.GetBool(_options, "AgentPluginToolsEnabled", false);
        _desktopAssistant?.Configure(SettingsRegistry.GetInt(_options, "TimelineRetentionDays", 7));
        _assistantReminders?.Configure(AssistantRuleOptions.From(_options));
        ConfigurePersonalTools();
    }
    private DesktopObservation ObserveDesktop() => new(
        _gameActivity?.Current is { Active: true } game ? game.Name : null,
        _workspace.Media.Current is { IsPlaying: true, ContentKind: MediaContentKind.Music } media ? media.SourceName + " · " + media.Title + " · " + media.Artist : null,
        _downloads == null ? 0 : _workspace.Downloads.Current.Count(d => d.IsRunning),
        SettingsRegistry.GetBool(_options, "CpuMonitorEnabled", false) ? _workspace.Resources.Snapshot.CpuPercent : null,
        SettingsRegistry.GetBool(_options, "MemoryMonitorEnabled", false) ? _workspace.Resources.Snapshot.MemoryPercent : null);
    private string? ForegroundApplication()
    {
        try
        {
            var hwnd = Interop.NativeMethods.GetForegroundWindow();
            Interop.NativeMethods.GetWindowThreadProcessId(hwnd, out uint process);
            using var app = Process.GetProcessById((int)process); return app.ProcessName;
        }
        catch (Exception) { return null; }
    }
    private async ValueTask<DesktopContextSnapshot> ReadDesktopContext(string prompt, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var sections = DesktopContextPolicy.RequestedSections(prompt);
        return sections.Length == 0 ? DesktopContextSnapshot.Empty : await CaptureIslandInformation(sections, prompt, false, token);
    }
    private bool CanReadIslandInformation => !_exiting && !_sessionLocked && SettingsRegistry.GetBool(_options, "AiDesktopContextEnabled", true);
    private async ValueTask<DesktopContextSnapshot> ReadLatestDesktopState(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!CanReadIslandInformation) return DesktopContextSnapshot.Empty;
        if (_desktopAssistant != null) await _desktopAssistant.RefreshContextAsync("", true, token);
        token.ThrowIfCancellationRequested();
        // Preserve the legacy endpoint's minimal payload for existing plugin clients.
        return CanReadIslandInformation ? _desktopAssistant?.Context("", ForegroundApplication(), explicitQuery: true) ?? DesktopContextSnapshot.Empty
            : DesktopContextSnapshot.Empty;
    }
    private ValueTask<DesktopContextSnapshot> ReadIslandInformation(IReadOnlyList<string> sections, CancellationToken token)
        => CaptureIslandInformation(sections, "", true, token);
    private async ValueTask<DesktopContextSnapshot> CaptureIslandInformation(IReadOnlyList<string> sections, string prompt, bool explicitQuery, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!CanReadIslandInformation) return DesktopContextSnapshot.Empty;
        if (_desktopAssistant != null && (sections.Contains("all") || sections.Contains("privacy")))
            await _desktopAssistant.RefreshContextAsync(prompt, explicitQuery, token);
        token.ThrowIfCancellationRequested();
        // Recheck after asynchronous sampling, so lock/settings changes cannot leak a pending read.
        if (!CanReadIslandInformation) return DesktopContextSnapshot.Empty;
        var baseline = _desktopAssistant?.Context(prompt, sections.Contains("all") || sections.Contains("foreground") ? ForegroundApplication() : null, explicitQuery)
            ?? DesktopContextSnapshot.Empty;
        return IslandInformationService.Capture(_workspace, ReadIslandInformationState(), baseline, sections, DateTimeOffset.Now);
    }
    private IslandInformationState ReadIslandInformationState() => new()
    {
        DeviceBatteries = (_bluetooth?.DeviceBatterySamples ?? []).Concat(_plugins?.DeviceBatteries ?? []).ToArray(),
        Power = _battery?.Current,
        Resources = _workspace.Resources.Snapshot with
        {
            CpuPercent = SettingsRegistry.GetBool(_options, "CpuMonitorEnabled", true) ? _workspace.Resources.Snapshot.CpuPercent : null,
            MemoryPercent = SettingsRegistry.GetBool(_options, "MemoryMonitorEnabled", false) ? _workspace.Resources.Snapshot.MemoryPercent : null,
            CpuWatts = SettingsRegistry.GetBool(_options, "StatusShowPower", true) ? _workspace.Resources.Snapshot.CpuWatts : null,
            GpuWatts = SettingsRegistry.GetBool(_options, "StatusShowPower", true) ? _workspace.Resources.Snapshot.GpuWatts : null,
            TotalWatts = SettingsRegistry.GetBool(_options, "StatusShowPower", true) ? _workspace.Resources.Snapshot.TotalWatts : null
        },
        Network = _netSpeed != null || _externalNetwork.Active ? CurrentNetworkSample : null,
        DailyTraffic = _dailyTraffic?.Current,
        NetworkSource = _externalNetwork.Active ? _externalNetwork.Title : L.T("本机采样"),
        Weather = _weatherSnapshot,
        Volume = _volumeService is { IsAvailable: true } volume ? volume.CurrentPercent : null,
        Muted = _volumeService is { IsAvailable: true } output ? output.IsMuted : null,
        Productivity = _productivity is { Enabled: true } productivity
            ? productivity.Days.FirstOrDefault(d => d.Date == DateTime.Now.ToString("yyyy-MM-dd")) ?? new(DateTime.Now.ToString("yyyy-MM-dd")) : null,
        ClipboardCount = _clipboard?.HistoryCount,
        SensitiveGuard = _clipboard?.SensitiveGuardEnabled == true,
        ActiveReminders = _userReminders is { Enabled: true } reminders ? reminders.Jobs.Count(j => j.Active) : null,
        LauncherCount = _launcherIndexed != DateTime.MinValue ? _launcherApplications.Length : null,
        Plugins = _plugins?.Entries.Where(p => p.Manifest != null).Select(p => new IslandPluginState(p.Manifest!.Name, p.Manifest.Version, p.Running, p.Error.Length > 0)).ToArray() ?? [],
        GameMode = _gameQuietActive, QuietMode = NotificationQuietEffective, Pinned = _isPinned
    };
}
