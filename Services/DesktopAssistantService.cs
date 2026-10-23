using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;
using AlphaIsland.AgentTools;
using AlphaIsland.PluginSdk;
using alpha_island.Core;

namespace alpha_island.Services;

internal sealed record DesktopObservation(string? Game, string? Media, int Downloads, double? Cpu, double? Memory);
/// <summary>One low-frequency sampler fans out to local guards, context and the event ledger.</summary>
internal sealed class DesktopAssistantService : IDisposable
{
    private readonly Func<DesktopObservation> _observe;
    private readonly Func<string, bool> _flag;
    private readonly IslandTicker.Subscription? _tick;
    private readonly LocalStateFile<TimelineState>? _file;
    private Task _sampleTask = Task.CompletedTask;
    private readonly TimelineLedger _ledger = new(TimeZoneInfo.Local);
    private bool _disposed, _sampling, _blocked, _enabled, _locked, _sleeping;
    private DateTimeOffset _lastSave, _privacyAt;
    private IReadOnlyList<PrivacyUsage> _privacy = [];
    private MicrophoneState _microphone = new(false, false, "", default);
    internal IReadOnlyList<PrivacyUsage> PrivacySnapshot => DateTimeOffset.UtcNow - _privacyAt < TimeSpan.FromSeconds(10) ? _privacy : [];
    internal bool PrivacyFresh => DateTimeOffset.UtcNow - _privacyAt < TimeSpan.FromSeconds(10);
    internal bool? InputMuted => PrivacyFresh && _microphone.Available ? _microphone.Muted : null;
    internal PrivacyIndicatorService Privacy { get; } = new();
    internal event Action? Changed;
    internal TimelineEntry[] Entries => _ledger.Snapshot().Entries;
    internal int RetentionDays => _ledger.RetentionDays;
    internal bool Enabled => _enabled;
    internal DesktopAssistantService(Dispatcher dispatcher, IslandTicker ticker, Func<DesktopObservation> observe, Func<string, bool> flag, string? path)
    {
        _dispatcher = dispatcher; _observe = observe; _flag = flag; _enabled = flag("TimelineEnabled");
        if (path == null) return;
        _file = new(path, maximumBytes: 2 * 1024 * 1024, encrypted: true); _ledger.Restore(_file.Read()); _ledger.Trim(DateTimeOffset.UtcNow); Save();
        _tick = ticker.Subscribe("desktop-assistant", TimeSpan.FromSeconds(3), () => _ = RequestSample(), runImmediately: true);
        SystemEvents.SessionSwitch += SessionChanged; SystemEvents.PowerModeChanged += PowerChanged;
        _ = ReadBoot();
    }
    internal void Configure(int retentionDays)
    {
        bool retentionChanged = _ledger.RetentionDays != Math.Clamp(retentionDays, 1, 30);
        _ledger.RetentionDays = Math.Clamp(retentionDays, 1, 30);
        if (retentionChanged) { _ledger.Trim(DateTimeOffset.UtcNow); Save(); Changed?.Invoke(); }
        bool enabled = _flag("TimelineEnabled");
        if (_enabled && !enabled) { _ledger.CloseAll(DateTimeOffset.UtcNow); Save(); }
        _enabled = enabled;
        _tick?.SetPaused(_blocked || !_enabled && !_flag("MeetingGuardEnabled") && !_flag("AgentProactiveEnabled") && !_flag("AiDesktopContextEnabled") && !_flag("PrivacyIndicatorEnabled"));
    }
    private async Task ReadBoot()
    {
        var reading = await Task.Run(BootTimingService.Read);
        if (!_disposed && _enabled && reading.BootStartedUtc is { } started) { _ledger.AddBoot(started); Save(); Changed?.Invoke(); }
    }
    private async Task SampleAsync()
    {
        if (_disposed || _blocked || _sampling) return;
        _sampling = true;
        try
        {
            var sampleStarted = DateTimeOffset.UtcNow;
            bool needPrivacy = _enabled || _flag("MeetingGuardEnabled") || _flag("AgentProactiveEnabled") || _flag("AiDesktopContextEnabled") || _flag("PrivacyIndicatorEnabled");
            var sampled = needPrivacy ? await Task.Run(() => (Privacy.Sample(), MicrophoneStateReader.Read())) : (Array.Empty<PrivacyUsage>() as IReadOnlyList<PrivacyUsage>, new MicrophoneState(false, false, "", default));
            if (_disposed || _blocked) return;
            var now = DateTimeOffset.UtcNow;
            var observation = _observe();
            bool fresh = Privacy.DegradedReason == null && now - sampleStarted < TimeSpan.FromSeconds(5);
            _privacy = fresh ? sampled.Item1 : [];
            _privacyAt = fresh ? now : default; _microphone = sampled.Item2;
            if (_enabled) ObserveTimeline(observation, now);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { IslandLog.Warn("DesktopAssistant", "本地状态暂不可用：" + ex.GetType().Name); }
        finally { _sampling = false; }
    }
    private (string? Game, string? Media) _lastTimelineActivity;
    internal void NotifyActivityChanged()
    {
        if (_disposed || _blocked || !_enabled) return;
        var observation = _observe();
        if (_lastTimelineActivity == (observation.Game, observation.Media)) return;
        try { ObserveTimeline(observation, DateTimeOffset.UtcNow); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { IslandLog.Warn("Timeline", "活动记录暂不可用：" + ex.GetType().Name); }
    }
    private void ObserveTimeline(DesktopObservation observation, DateTimeOffset now)
    {
        var activities = new Dictionary<string, (string, string)>();
        if (observation.Game is { Length: > 0 } game) activities["game"] = ("game", SensitiveTextDetector.Inspect(game).Redacted);
        if (observation.Media is { Length: > 0 } media) activities["music"] = ("music", SensitiveTextDetector.Inspect(media).Redacted);
        foreach (var item in PrivacySnapshot.Take(32))
        {
            string kind = item.Capability == PrivacyCapability.Camera ? "camera" : "microphone";
            activities[kind + ":" + item.AppName] = (kind, SensitiveTextDetector.Inspect(item.AppName).Redacted);
        }
        var before = _ledger.Snapshot().Entries;
        _ledger.Observe(activities, now, Environment.TickCount64);
        _lastTimelineActivity = (observation.Game, observation.Media);
        bool changed = !before.SequenceEqual(_ledger.Snapshot().Entries);
        if (changed || now - _lastSave >= TimeSpan.FromSeconds(30)) Save();
        Changed?.Invoke();
    }
    internal DesktopContextSnapshot Context(string prompt, string? foregroundApp = null, bool explicitQuery = false)
    {
        var observation = _observe(); var fields = new Dictionary<string, string>();
        if (observation.Game is { Length: > 0 }) fields["game"] = observation.Game;
        if (observation.Media is { Length: > 0 }) fields["media"] = observation.Media;
        if (observation.Downloads > 0) fields["downloads"] = observation.Downloads + " 个下载正在进行";
        if (observation.Cpu >= 75 || explicitQuery && observation.Cpu.HasValue) fields["cpu"] = $"{observation.Cpu:0}%";
        if (observation.Memory >= 80 || explicitQuery && observation.Memory.HasValue) fields["memory"] = $"{observation.Memory:0}%";
        if (DateTimeOffset.UtcNow - _privacyAt < TimeSpan.FromSeconds(10))
        {
            foreach (var group in _privacy.GroupBy(p => p.Capability)) fields[group.Key == PrivacyCapability.Camera ? "camera" : "microphone"] = string.Join("、", group.Select(p => p.AppName).Distinct().Take(4));
            if (_privacy.Count > 0 && _microphone.Available) fields["microphoneMuted"] = _microphone.Muted ? "默认通信麦克风已静音" : "默认通信麦克风未静音";
        }
        if (foregroundApp?.Length > 0) fields["foregroundApp"] = foregroundApp;
        if (explicitQuery) fields["privacyStatus"] = DateTimeOffset.UtcNow - _privacyAt < TimeSpan.FromSeconds(10)
            ? _privacy.Count == 0 ? "当前未观察到麦克风或摄像头使用" : "已提供当前可用的设备占用记录" : "设备占用状态未知";
        foreach (string key in fields.Keys.ToArray()) { string value = SensitiveTextDetector.Inspect(fields[key]).Redacted; fields[key] = value[..Math.Min(180, value.Length)]; }
        var snapshot = new DesktopContextSnapshot(DateTimeOffset.UtcNow, fields);
        return explicitQuery ? snapshot : DesktopContextPolicy.Filter(snapshot, prompt);
    }
    private Task RequestSample()
    { if (_sampleTask.IsCompleted) _sampleTask = SampleAsync(); return _sampleTask; }
    internal async ValueTask RefreshContextAsync(string prompt, bool explicitQuery, CancellationToken token)
    {
        if (_disposed || _blocked || _file == null) return;
        var check = new DesktopContextSnapshot(DateTimeOffset.UtcNow, new() { ["camera"] = "checking", ["microphone"] = "checking" });
        if (DateTimeOffset.UtcNow - _privacyAt > TimeSpan.FromSeconds(3) && (explicitQuery || DesktopContextPolicy.Filter(check, prompt).Fields.Count > 0))
        {
            try { await RequestSample().WaitAsync(TimeSpan.FromMilliseconds(explicitQuery ? 1000 : 500), token); }
            catch (TimeoutException) { /* Optional context must never hold up a conversation on a slow device. */ }
        }
    }
    internal void Clear() { _ledger.Clear(DateTimeOffset.UtcNow); Save(); Changed?.Invoke(); }
    internal Task ExportAsync(string path, CancellationToken token) => File.WriteAllTextAsync(path,
        JsonSerializer.Serialize(_ledger.Snapshot(), new JsonSerializerOptions { WriteIndented = true }), token);
    private void Save() { _lastSave = DateTimeOffset.UtcNow; _file?.Write(_ledger.Snapshot()); }
    private void SetBlocked(bool blocked)
    {
        if (_blocked == blocked || _disposed) return;
        _blocked = blocked; if (blocked) { _ledger.CloseAll(DateTimeOffset.UtcNow); Save(); }
        _privacyAt = default;
        Configure(_ledger.RetentionDays);
    }
    private void SessionChanged(object sender, SessionSwitchEventArgs args)
    { if (args.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock) _tickDispatcher(() => { _locked = args.Reason == SessionSwitchReason.SessionLock; SetBlocked(_locked || _sleeping); }); }
    private void PowerChanged(object sender, PowerModeChangedEventArgs args)
    { if (args.Mode is PowerModes.Suspend or PowerModes.Resume) _tickDispatcher(() => { _sleeping = args.Mode == PowerModes.Suspend; SetBlocked(_locked || _sleeping); }); }
    private readonly Dispatcher _dispatcher;
    private void _tickDispatcher(Action action) { if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(action); }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _tick?.Dispose();
        SystemEvents.SessionSwitch -= SessionChanged; SystemEvents.PowerModeChanged -= PowerChanged;
        _ledger.CloseAll(DateTimeOffset.UtcNow); Save(); _file?.Dispose(); Changed = null;
    }
}
