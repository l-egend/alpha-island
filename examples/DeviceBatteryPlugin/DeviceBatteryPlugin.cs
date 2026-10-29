using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Windows.Threading;
using AlphaIsland.PluginSdk;
using Windows.Devices.Enumeration;
using Windows.Gaming.Input;

namespace AlphaIsland.Plugins.DeviceBattery;

public sealed class DeviceBatteryPlugin : IIslandPlugin, IPluginSuspendGuard
{
    public bool CanSuspend => Volatile.Read(ref _busy) == 0;
    private IPluginContext _context = null!;
    private IPluginScheduledTask? _poll;
    private DeviceWatcher? _watcher;
    private DispatcherTimer? _debounce;
    private CancellationTokenSource? _run;
    private int _busy;
    private bool _pending;
    private bool _disposed;
    private IReadOnlyList<HidInterface>? _inventory;
    private long _inventoryUntil;
    private int _inventoryRevision;
    public void Initialize(IPluginContext context)
    {
        _context = context;
        context.RegisterSetting(new("state-refresh-seconds", L.T("设备状态刷新间隔"), PluginSettingKind.Number, 2,
            L.T("快速识别外设开关、充电和电量变化；设备资料缓存复用，插拔后立即重新检测"), 1, 10, 1));
        context.RegisterAction(new("device-battery-refresh", L.T("刷新设备电量"), InvalidateInventory));
        context.RegisterService(new("device-battery", Start, Stop));
    }
    private void Start(CancellationToken token)
    {
        _run = CancellationTokenSource.CreateLinkedTokenSource(token);
        _debounce = new DispatcherTimer(DispatcherPriority.Background, _context.Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };
        _poll = _context.Schedule("battery-poll", TimeSpan.FromSeconds(Interval()), Refresh);
        try
        {
            _watcher = DeviceInformation.CreateWatcher("System.Devices.InterfaceClassGuid:=\"{4D1E55B2-F16F-11CF-88CB-001111000030}\"");
            _watcher.Added += (_, _) => InvalidateInventory(); _watcher.Removed += (_, _) => InvalidateInventory();
            _watcher.Updated += (_, _) => InvalidateInventory(); _watcher.Start();
            RawGameController.RawGameControllerAdded += OnControllerAdded;
            RawGameController.RawGameControllerRemoved += OnControllerRemoved;
            _ = RawGameController.RawGameControllers.Count;
        }
        catch (Exception ex) { _context.Log(L.T("设备变更监听不可用，将使用定期刷新：") + ex.Message); }
        Refresh();
    }
    private int Interval() => Math.Clamp(_context.GetSetting("state-refresh-seconds", 2), 1, 10);
    public void OnSettingsChanged() => _poll?.SetInterval(TimeSpan.FromSeconds(Interval()));
    private void OnControllerAdded(object? sender, RawGameController controller) => QueueRefresh();
    private void OnControllerRemoved(object? sender, RawGameController controller) => QueueRefresh();
    private void InvalidateInventory() { Interlocked.Increment(ref _inventoryRevision); Interlocked.Exchange(ref _inventoryUntil, 0); QueueRefresh(); }
    private void QueueRefresh()
    {
        if (_disposed || _run?.IsCancellationRequested != false) return;
        _context.Dispatcher.BeginInvoke(new Action(() => { if (_disposed) return; _debounce?.Stop(); _debounce?.Start(); }));
    }
    private async void Refresh()
    {
        var run = _run;
        if (_disposed || run?.IsCancellationRequested != false) return;
        var token = run.Token;
        if (Interlocked.Exchange(ref _busy, 1) != 0) { _pending = true; return; }
        try
        {
            int revision = Volatile.Read(ref _inventoryRevision);
            var readings = await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                var inventory = _inventory;
                if (inventory == null || Environment.TickCount64 >= Volatile.Read(ref _inventoryUntil))
                {
                    inventory = HidInventory.Enumerate(); token.ThrowIfCancellationRequested();
                    _inventory = inventory; Interlocked.Exchange(ref _inventoryUntil, Environment.TickCount64 + 30000);
                }
                return await BatteryScanner.ReadAsync(token, inventory);
            });
            if (revision != Volatile.Read(ref _inventoryRevision)) { _inventoryUntil = 0; _pending = true; }
            if (!_disposed && ReferenceEquals(_run, run) && !token.IsCancellationRequested) _context.ReportDeviceBatteries(readings);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed && ReferenceEquals(_run, run)) { _context.ReportDeviceBatteries([]); _context.Log(L.T("电量读取失败：") + ex.Message); } }
        finally { Interlocked.Exchange(ref _busy, 0); if (!ReferenceEquals(_run, run)) run.Dispose(); if (_pending) { _pending = false; QueueRefresh(); } }
    }
    private void Stop()
    {
        var run = _run; _run = null; run?.Cancel();
        if (Volatile.Read(ref _busy) == 0) run?.Dispose();
        _poll?.Dispose(); _poll = null; _debounce?.Stop(); _inventory = null; _inventoryUntil = 0;
        if (_watcher?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) _watcher.Stop();
        _watcher = null;
        RawGameController.RawGameControllerAdded -= OnControllerAdded;
        RawGameController.RawGameControllerRemoved -= OnControllerRemoved;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Stop(); }
}
