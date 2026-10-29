using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Globalization;
using AlphaIsland.EarbudsV2.Communication.Abstractions;
using AlphaIsland.EarbudsV2.Control.Abstractions;
using AlphaIsland.EarbudsV2.Control.Core.Models;
using AlphaIsland.EarbudsV2.Control.Core.Features;
using AlphaIsland.EarbudsV2.Assets.Localization;

namespace AlphaIsland.Plugins.OppoPods;
internal sealed class MultiBrandSession : IEarbudSession
{
    private readonly EarbudDevice _device;
    private readonly EarbudOptions _options;
    private readonly IBrandManagerFactory _factory;
    private readonly Func<DeviceCandidate, ConnectionOptions, CancellationToken, Task<IRawConnection>> _open;
    private readonly CancellationTokenSource _stop = new();
    private IRawConnection? _connection;
    private IBrandManager? _manager;
    private int _disposed;
    private readonly object _gate = new();
    private Task? _connecting;
    private string _commandError = "";
    internal Task Cleanup { get; private set; } = Task.CompletedTask;
    internal MultiBrandSession(EarbudDevice device, EarbudOptions options, IBrandManagerFactory factory,
        Func<DeviceCandidate, ConnectionOptions, CancellationToken, Task<IRawConnection>> open)
    { _device = device; _options = options; _factory = factory; _open = open; }
    public Task ConnectAsync(CancellationToken token)
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed != 0, this); return _connecting ??= ConnectCoreAsync(token); }
    }
    private async Task ConnectCoreAsync(CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        var candidate = _device.Candidate ?? new DeviceCandidate(_device.Address.ToString("X12"), _device.Address.ToString("X12"),
            _device.Address.ToString("X12"), _device.Name, [], ["rfcomm"]);
        var transport = candidate.Transports.Contains(TransportNames.BleAdvertisement) ? TransportNames.BleAdvertisement : "rfcomm";
        var options = new ConnectionOptions(transport, _factory.ServiceId == Guid.Empty ? null : _factory.ServiceId,
            _factory.GetPreferredChannel(_device.Name));
        _connection = await _open(candidate, options, lifetime.Token).ConfigureAwait(false);
        lifetime.Token.ThrowIfCancellationRequested();
        _manager = await _factory.CreateAsync(new(candidate, options, _factory.Brand), _connection, lifetime.Token).ConfigureAwait(false);
        lifetime.Token.ThrowIfCancellationRequested();
        if (_options.ModelOverride != "自动识别" && _factory.IsCandidateName(_options.ModelOverride)) _manager.SetManualModel(_options.ModelOverride);
        _manager.SetInteractivePolling(true);
    }
    public void Pump(bool refresh) { }
    public EarbudSnapshot ReadSnapshot()
    {
        var manager = _manager;
        if (Volatile.Read(ref _disposed) != 0 || manager == null || !manager.Snapshot.IsConnected || _connection?.IsConnected != true)
            return EarbudSnapshot.Offline(L.T("耳机控制连接已断开"), _commandError);
        var live = manager.SessionLiveness;
        if (live is { } ticks && ticks.LastSendTicks > ticks.LastReceiveTicks
            && Environment.TickCount64 - ticks.LastReceiveTicks > 15000)
            return EarbudSnapshot.Offline(L.T("耳机已停止响应"), L.T("电量已清空，等待重新连接"));
        return MapSnapshot(manager.Snapshot, manager.Presentation, _device.Name, _commandError, manager.EqualizerProfile.ResolvePresetDisplayName);
    }
    internal static EarbudSnapshot MapSnapshot(BusinessSnapshot state, BrandPresentation view, string deviceName, string error = "", Func<string, string>? eqLabel = null)
    {
        eqLabel ??= key => TranslationCatalog.Get(key);
        bool Can(string key) => view.IsKnownModel && view.VisibleControls.Contains(key)
            && (!view.ControlEnabledStates.TryGetValue(key, out var ready) || ready);
        bool Value(string key) => view.ControlStates.GetValueOrDefault(key);
        EarbudBattery Power(BatteryLevel? value) => value is { Percent: <= 100 } battery
            && state.BatteryUpdatedUtc != default && DateTimeOffset.UtcNow - state.BatteryUpdatedUtc < TimeSpan.FromSeconds(10)
                ? new(battery.Percent, battery.IsCharging) : default;
        IEnumerable<NoiseOptionModel> Flatten(NoiseOptionModel item)
            => item.Children.Count > 0 ? item.Children.SelectMany(Flatten) : [item];
        var anc = view.NoiseOptions.SelectMany(Flatten).Select(x => new EarbudChoice(x.Key, DeviceText.NoiseModeName(x.Mode))).ToArray();
        return new()
        {
            Connected = state.IsConnected, DeviceName = deviceName, ModelName = view.ModelName,
            Status = L.T("已连接"), Hint = error.Length > 0 ? error : view.IsKnownModel ? L.T("设置与耳机状态同步") : L.T("基础信息 · 不支持的控制已隐藏"),
            LeftPower = Power(state.LeftBattery), RightPower = Power(state.RightBattery), CasePower = Power(state.CaseBattery),
            SingleBattery = view.BatteryLayout == BatteryLayout.SingleBattery,
            Firmware = state.Identity?.FirmwareVersion ?? "", Codec = state.Identity?.Codec ?? "",
            Wearing = state.Wear == WearSnapshot.Empty ? "" : L.F($"左：{WearText(state.Wear.Left)} · 右：{WearText(state.Wear.Right)}"),
            CanAnc = view.IsKnownModel && view.SupportsNoiseCancellation && anc.Length > 0, AncChoices = anc, Anc = view.CurrentNoiseModeKey,
            CanEq = Can("equalizer") && view.EqualizerPresets.Count > 0, EqChoices = view.EqualizerPresets.Select(eqLabel).ToArray(),
            Eq = state.Equalizer.PresetName is { } preset ? eqLabel(preset) : "?", CanGameMode = Can("game-mode"), GameMode = Value("game-mode"),
            CanGameSound = Can("game-sound"), GameSound = Value("game-sound"),
            CanDualDevice = Can("dual-device"), DualDevice = Value("dual-device"),
            CanSpatialSound = Can("spatial-sound"), SpatialSound = Value("spatial-sound"),
            CanSpatialAudio = view.IsKnownModel && view.SupportsSpatialAudio,
            SpatialChoices = view.SupportsSpatialAudio ? [new("Off", L.T("关闭")), new("Fixed", L.T("固定")), new("HeadTracking", L.T("头部追踪"))] : [],
            SpatialAudio = state.SpatialAudio.Mode.ToString(),
            Peers = state.MultiDevice.Devices.Select(x => new EarbudPeer(x.Address, x.Name, x.ConnectionState, x.IsCurrent, x.IsAudioActive)).ToArray(),
            // Leave additional peer operations hidden until their exact capability is declared.
            CanMultiControl = false, CanPriorityControl = false
        };
    }
    private static string WearText(EarWearState state) => state switch
    { EarWearState.Worn => L.T("佩戴中"), EarWearState.InCase => L.T("已入盒"), EarWearState.Removed => L.T("已取下"), EarWearState.Disconnected => L.T("未连接"), _ => L.T("未知") };
    public void Execute(EarbudCommand command)
    {
        var manager = _manager;
        if (manager == null || !ReadSnapshot().Allows(command)) return;
        Task<bool> result = command.Kind switch
        {
            EarbudCommandKind.Anc => manager.SetNoiseCancellationByKeyAsync(command.Value, _stop.Token),
            EarbudCommandKind.Eq => manager.SetEqualizerByNameAsync(manager.Presentation.EqualizerPresets.FirstOrDefault(x => manager.EqualizerProfile.ResolvePresetDisplayName(x) == command.Value) ?? command.Value, _stop.Token),
            EarbudCommandKind.GameMode => manager.SetGameModeAsync(command.Enabled, _stop.Token),
            EarbudCommandKind.GameSound => manager.SetGameSoundEnabledAsync(command.Enabled, _stop.Token),
            EarbudCommandKind.DualDevice => manager.SetDualDeviceAsync(command.Enabled, _stop.Token),
            EarbudCommandKind.SpatialSound => manager.SetSpatialSoundAsync(command.Enabled, _stop.Token),
            EarbudCommandKind.SpatialAudio when Enum.TryParse<SpatialAudioMode>(command.Value, out var mode) => manager.SetSpatialAudioAsync(mode, _stop.Token),
            _ => Task.FromResult(false)
        };
        _commandError = result.WaitAsync(TimeSpan.FromSeconds(10), _stop.Token).GetAwaiter().GetResult()
            ? "" : L.T("耳机未确认本次设置，请稍后重试");
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        Task? connecting;
        lock (_gate) connecting = _connecting;
        // Stop a current socket immediately, then also collect any connection that finishes late.
        Task disconnect = _connection?.DisconnectAsync(CancellationToken.None) ?? Task.CompletedTask;
        Cleanup = Task.Run(async () =>
        {
            try
            {
                try { if (connecting != null) await connecting; } catch { }
                try { await disconnect; } catch { }
                if (_manager != null) await _manager.DisposeAsync();
            }
            finally
            {
                try { if (_connection != null) await _connection.DisposeAsync(); }
                finally { _stop.Dispose(); }
            }
        });
    }
}
