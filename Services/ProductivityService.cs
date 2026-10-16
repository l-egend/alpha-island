using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Threading;
using Microsoft.Win32;
using alpha_island.Core;

namespace alpha_island.Services;

internal sealed class ProductivityService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ProductivityLedger _ledger;
    private readonly LocalStateFile<ProductivityDay[]>? _file;
    private readonly IslandTicker.Subscription _tick;
    private ProductivityActivity _activity;
    private bool _enabled, _locked, _sleeping, _disposed, _dirty;
    internal event Action? Changed;
    internal bool Enabled => _enabled;
    internal ProductivityDay[] Days => _ledger.Snapshot();
    internal ProductivityService(Dispatcher dispatcher, IslandTicker ticker, string? path, bool enabled)
    {
        _dispatcher = dispatcher; _ledger = new(TimeZoneInfo.Local); _enabled = enabled;
        if (path != null) { _file = new(path); _ledger.Restore(_file.Read() ?? []); }
        _tick = ticker.Subscribe("productivity", TimeSpan.FromSeconds(30), Tick);
        _dirty = _ledger.SetState(_activity, enabled, false, DateTimeOffset.UtcNow, Environment.TickCount64); Save();
        SystemEvents.SessionSwitch += OnSession; SystemEvents.PowerModeChanged += OnPower;
        Pace();
    }
    internal void SetActivity(ProductivityActivity kind, bool active)
    {
        var next = active ? _activity | kind : _activity & ~kind;
        if (_disposed || next == _activity) return;
        _activity = next; ChangeState();
    }
    internal void SetEnabled(bool value) { if (_disposed || _enabled == value) return; _enabled = value; ChangeState(); }
    private void ChangeState()
    {
        _dirty |= _ledger.SetState(_activity, _enabled, _locked || _sleeping, DateTimeOffset.UtcNow, Environment.TickCount64);
        Pace(); Save(); Changed?.Invoke();
    }
    private void Pace() => _tick.SetPaused(!_enabled || _locked || _sleeping || _activity == ProductivityActivity.None);
    private void Tick()
    { if (_disposed) return; _dirty |= _ledger.Advance(DateTimeOffset.UtcNow, Environment.TickCount64); Save(); Changed?.Invoke(); }
    private void Save() { if (_dirty) { _dirty = false; _file?.Write(_ledger.Snapshot()); } }
    private void OnSession(object sender, SessionSwitchEventArgs args)
    {
        if (args.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock)
            Post(() => { _locked = args.Reason == SessionSwitchReason.SessionLock; ChangeState(); Save(); });
    }
    private void OnPower(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode is PowerModes.Suspend or PowerModes.Resume)
            Post(() => { _sleeping = args.Mode == PowerModes.Suspend; ChangeState(); Save(); });
    }
    private void Post(Action action)
    { if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(() => { if (!_disposed) action(); }); }
    internal void Clear()
    { _ledger.Clear(DateTimeOffset.UtcNow, Environment.TickCount64); _dirty = true; Save(); Changed?.Invoke(); }
    internal async Task ExportAsync(string path, CancellationToken token)
    {
        _dirty |= _ledger.Advance(DateTimeOffset.UtcNow, Environment.TickCount64);
        var days = _ledger.Snapshot();
        string text = "日期,音乐秒数,游戏秒数,专注秒数,助手秒数\r\n" + string.Join("\r\n", days.Select(day =>
            string.Join(',', new[] { day.Date, day.MusicSeconds.ToString("0.###", CultureInfo.InvariantCulture), day.GameSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                day.FocusSeconds.ToString("0.###", CultureInfo.InvariantCulture), day.AgentSeconds.ToString("0.###", CultureInfo.InvariantCulture) })));
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(true), token);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _dirty |= _ledger.Advance(DateTimeOffset.UtcNow, Environment.TickCount64); Save(); _disposed = true;
        SystemEvents.SessionSwitch -= OnSession; SystemEvents.PowerModeChanged -= OnPower;
        _tick.Dispose(); _file?.Dispose(); Changed = null;
    }
}
