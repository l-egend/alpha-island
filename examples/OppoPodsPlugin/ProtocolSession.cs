using OppoPodsManager;
using System.IO;

namespace AlphaIsland.Plugins.OppoPods;

internal sealed class ProtocolSession : IEarbudSession
{
    private readonly IPodTransport _transport;
    private readonly PodManager _manager;
    private readonly EarbudDevice _device;
    private readonly Func<DateTime> _utcNow;
    private DateTime _lastResponse;
    internal static readonly TimeSpan BatteryLifetime = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(15);
    private readonly HashSet<ushort> _pendingReadbacks = [];
    private readonly HashSet<byte> _requiredFeatures = [];
    private EarbudSnapshot? _beforeWrite;
    private DateTime _readbackDeadline;
    private DateTime _nextRead;
    private int _pollCycle;
    private string _commandError = "";
    private int _disposed;

    internal ProtocolSession(EarbudDevice device, EarbudOptions options, IPodTransport? transport = null, Func<DateTime>? utcNow = null)
    {
        _device = device;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _lastResponse = _utcNow();
        _transport = transport ?? TransportFactory.Create(device.Address, device.Name);
        _manager = new PodManager(_transport, options.ModelOverride == "自动识别" ? null : options.ModelOverride, _utcNow);
        _manager.CommandFailed += CommandFailed;
        // The manager subscribes first, so its parsing finishes before readback is observed.
        _transport.FrameReceived += FrameReceived;
    }
    public async Task ConnectAsync(CancellationToken token)
    {
        await _manager.ConnectAsync().WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!_manager.IsConnected) throw new IOException(_manager.LastError ?? "无法建立耳机控制连接");
    }
    private void CommandFailed(string message) => _commandError = message;
    private void FrameReceived(PodFrame frame)
    {
        if (frame.Payload.Length > 0) _lastResponse = _utcNow();
        bool valid = frame.Cmd switch
        {
            OppoProtocol.CmdAncResp => frame.Payload.Length >= 4 && _manager.State.AncMode != "?"
                && !(frame.Payload.Length >= 5 && frame.Payload[1] == 4),
            OppoProtocol.CmdBatchQueryResp => frame.Payload.Length >= 2 && _requiredFeatures.All(id =>
                frame.Payload.Where((_, index) => index % 2 == 0).Contains(id)),
            _ => frame.Payload.Length >= 2 && frame.Payload[0] == 0
        };
        if (!valid) return;
        if (_pendingReadbacks.Remove(frame.Cmd) && _pendingReadbacks.Count == 0) _beforeWrite = null;
    }
    public void Pump(bool refresh)
    {
        if (Volatile.Read(ref _disposed) != 0 || !_manager.IsConnected) return;
        if (refresh || DateTime.UtcNow >= _nextRead)
        {
            _nextRead = DateTime.UtcNow.AddSeconds(2);
            var caps = _manager.Caps;
            _transport.Send(OppoProtocol.CmdBattery, OppoProtocol.PayEmpty);
            if (caps.AncOptions.Count > 0) _transport.Send(OppoProtocol.CmdQueryAnc, OppoProtocol.PayQueryAnc);
            if (caps.AncNameToIndex.ContainsKey("Smart")) _transport.Send(OppoProtocol.CmdQueryAnc, OppoProtocol.PayQueryAncIntelligent);
            if (refresh || ++_pollCycle % 3 == 0)
            {
                var query = OppoProtocol.BuildFeatureQuery(caps);
                _manager.State.ProbedFeatures = new HashSet<byte>(query.Skip(1));
                _transport.Send(OppoProtocol.CmdBatchQuery, query);
                if (caps.EqPresets.Count > 0) _transport.Send(OppoProtocol.CmdQueryEq, OppoProtocol.PayEmpty);
                if (caps.HasGameSound) _transport.Send(OppoProtocol.CmdQueryGameSound, OppoProtocol.PayEmpty);
                if (caps.HasSpatialAudio) _transport.Send(OppoProtocol.CmdQueryHeadsetSpatial, OppoProtocol.PayEmpty);
                if (caps.HasDualDevice) _transport.Send(OppoProtocol.CmdMultiConnectInfo, OppoProtocol.PayEmpty);
                if (_manager.State.SupportedCommands.Contains(OppoProtocol.CmdQueryMultiPriority))
                    _transport.Send(OppoProtocol.CmdQueryMultiPriority, OppoProtocol.PayEmpty);
            }
        }
        _transport.Poll(40);
    }
    public EarbudSnapshot ReadSnapshot()
    {
        if (Volatile.Read(ref _disposed) != 0 || !_manager.IsConnected || !_transport.IsConnected)
            return EarbudSnapshot.Offline("耳机控制连接已断开", _manager.LastError ?? "");
        if (_utcNow() - _lastResponse >= ResponseTimeout)
            return EarbudSnapshot.Offline("耳机已停止响应", "电量已清空，等待重新连接");
        var state = _manager.State;
        var caps = _manager.Caps;
        bool known = caps.IsSupported && caps.ModelName is not ("Unknown" or "未识别型号");
        bool HasFeature(byte id) => known && state.FeatureProbeDone && state.ReportedFeatures.Contains(id);
        var anc = caps.AncOptions.SelectMany(a => a.Children.Count > 0
            ? a.Children.Where(c => c.Sendable).Select(c => new EarbudChoice(c.Key, a.Label + " · " + c.Label))
            : a.Sendable ? [new EarbudChoice(a.Key, a.Label)] : Array.Empty<EarbudChoice>()).ToArray();
        EarbudBattery Battery(string key) => state.BatteryUpdatedAt.TryGetValue(key, out var updated)
            && _utcNow() - updated < BatteryLifetime
            && state.Battery.TryGetValue(key, out var item) && item is { } value
            ? new(value.Level, value.Charging) : default;
        var snapshot = new EarbudSnapshot
        {
            Connected = true, DeviceName = _device.Name, ModelName = caps.ModelName,
            Status = "已连接", Hint = _commandError.Length > 0 ? _commandError
                : known ? state.IntelligentRealtime.Length > 0 ? "智能降噪 · " + state.IntelligentRealtime : "设置与耳机状态同步"
                : "型号未识别，仅显示已读取状态；可在插件设置中指定型号",
            LeftPower = Battery("L"), RightPower = Battery("R"), CasePower = Battery("C"),
            Wearing = string.Join(" · ", new[] { state.WearingL.Length > 0 ? "左耳 " + state.WearingL : "", state.WearingR.Length > 0 ? "右耳 " + state.WearingR : "" }.Where(s => s.Length > 0)),
            Firmware = state.FirmwareVersion, Codec = state.CodecType < 0 ? "" : OppoProtocol.CodecName(state.CodecType),
            AncChoices = anc, Anc = state.AncMode,
            CanAnc = known && anc.Length > 0 && (state.SupportedCommands.Contains(OppoProtocol.CmdAnc) || state.AncMode != "?"),
            EqChoices = caps.EqPresets.Keys.ToArray(), Eq = state.EqPreset,
            CanEq = known && caps.EqPresets.Count > 0 && (state.SupportedCommands.Contains(OppoProtocol.CmdSetEq) || state.EqPreset != "?"),
            CanGameMode = caps.HasGameMode && HasFeature(caps.GameModeFeatureId), GameMode = state.GameMode,
            CanGameSound = known && caps.HasGameSound && state.SupportedCommands.Contains(OppoProtocol.CmdQueryGameSound), GameSound = state.GameSound,
            CanDualDevice = caps.HasDualDevice && HasFeature(OppoProtocol.FeatureDualDevice), DualDevice = state.DualDevice,
            CanSpatialSound = caps.HasSpatialSound && HasFeature(OppoProtocol.FeatureSpatial), SpatialSound = state.SpatialSound,
            CanSpatialAudio = known && caps.HasSpatialAudio && state.SupportedCommands.Contains(OppoProtocol.CmdQueryHeadsetSpatial),
            SpatialChoices = caps.SpatialTypes.Where(x => x is >= 0 and <= 2).Select(x => new EarbudChoice(OppoProtocol.SpatialTypeToName(x), x switch { 1 => "固定", 2 => "头部追踪", _ => "关闭" })).ToArray(),
            SpatialAudio = state.SpatialMode,
            Peers = state.ConnectedDevices.Select(p => new EarbudPeer(p.Address, p.DeviceName, p.ConnectionState, p.IsCurrentDevice, p.IsAudioActive)).ToArray(),
            CanMultiControl = known && caps.HasDualDevice && state.SupportedCommands.Contains(OppoProtocol.CmdOperateHandheld),
            CanPriorityControl = known && caps.HasDualDevice && state.SupportedCommands.Contains(OppoProtocol.CmdOperateHandheld)
                && state.SupportedCommands.Contains(OppoProtocol.CmdQueryMultiPriority),
            PriorityAddress = state.PriorityDeviceAddress, AutoPriority = state.MultiConnectAutoMode
        };
        // Upstream setters use optimistic state. Keep controls at their previous values until actual query responses arrive.
        if (_beforeWrite is { } previous)
            snapshot = snapshot with
            {
                Anc = previous.Anc, Eq = previous.Eq, GameMode = previous.GameMode, GameSound = previous.GameSound,
                DualDevice = previous.DualDevice, SpatialSound = previous.SpatialSound, SpatialAudio = previous.SpatialAudio,
                Peers = previous.Peers, PriorityAddress = previous.PriorityAddress, AutoPriority = previous.AutoPriority,
                WaitingForReadback = DateTime.UtcNow < _readbackDeadline,
                Hint = DateTime.UtcNow < _readbackDeadline ? "已发送，等待耳机回读…" : "设置回读超时，请刷新确认实际状态"
            };
        return snapshot;
    }
    public void Execute(EarbudCommand command)
    {
        var previous = ReadSnapshot();
        if (!previous.Allows(command)) throw new InvalidOperationException("该设备当前不支持此操作，或正在等待回读");
        _beforeWrite = previous;
        _pendingReadbacks.Clear();
        _requiredFeatures.Clear();
        _readbackDeadline = DateTime.UtcNow.AddSeconds(10);
        _commandError = "";
        try
        {
            switch (command.Kind)
            {
                case EarbudCommandKind.Anc: Await(OppoProtocol.CmdAncResp); _manager.SendAnc(command.Value); break;
                case EarbudCommandKind.Eq:
                    if (_manager.Caps.GameSoundMutexEq && previous.CanGameSound && previous.GameSound && command.Value != _manager.Caps.DefaultEqPreset)
                    { _manager.SendGameSound(false); Await((ushort)(OppoProtocol.CmdQueryGameSound | 0x8000)); }
                    Await(OppoProtocol.CmdEqResp); _manager.SendEq(command.Value); break;
                case EarbudCommandKind.GameMode: AwaitFeature(_manager.Caps.GameModeFeatureId); _manager.SendGameMode(command.Enabled); break;
                case EarbudCommandKind.DualDevice: AwaitFeature(OppoProtocol.FeatureDualDevice); _manager.SendDualDevice(command.Enabled); break;
                case EarbudCommandKind.SpatialSound:
                    PrepareSpatial(command.Enabled); AwaitFeature(OppoProtocol.FeatureSpatial); _manager.SendSpatial(command.Enabled); break;
                case EarbudCommandKind.SpatialAudio:
                    PrepareSpatial(command.Value != "Off"); Await(OppoProtocol.CmdHeadsetSpatialResp); _manager.SendSpatialAudio(command.Value); break;
                case EarbudCommandKind.GameSound:
                    if (command.Enabled)
                    {
                        if (_manager.Caps.GameSoundMutexSpatial && previous.CanSpatialSound) { AwaitFeature(OppoProtocol.FeatureSpatial); _manager.SendSpatial(false); }
                        if (_manager.Caps.GameSoundMutexSpatial && previous.CanSpatialAudio) { Await(OppoProtocol.CmdHeadsetSpatialResp); _manager.SendSpatialAudio("Off"); }
                        if (_manager.Caps.GameSoundMutexEq && previous.CanEq) { Await(OppoProtocol.CmdEqResp); _manager.SendEq(_manager.Caps.DefaultEqPreset); }
                    }
                    Await((ushort)(OppoProtocol.CmdQueryGameSound | 0x8000)); Await(OppoProtocol.CmdBatchQueryResp);
                    _manager.SendGameSound(command.Enabled); break;
                case EarbudCommandKind.MultiConnect: Await(OppoProtocol.CmdMultiConnectResp); _manager.SendMultiConnectConnect(command.Value); break;
                case EarbudCommandKind.MultiDisconnect: Await(OppoProtocol.CmdMultiConnectResp); _manager.SendMultiConnectDisconnect(command.Value); break;
                case EarbudCommandKind.MultiPriority: Await(OppoProtocol.CmdMultiPriorityResp); _manager.SendMultiConnectSetPriority(command.Value); break;
                case EarbudCommandKind.AutoPriority: Await(OppoProtocol.CmdMultiPriorityResp); _manager.SendMultiConnectAutoSwitch(); break;
            }
            // Delay the immediate query slightly; periodic reads continue if the device applies the setting later.
            _nextRead = DateTime.UtcNow.AddMilliseconds(300);
            _pollCycle = 2;
        }
        catch { _beforeWrite = null; _pendingReadbacks.Clear(); throw; }
    }
    private void Await(ushort response) => _pendingReadbacks.Add(response);
    private void AwaitFeature(byte feature) { _requiredFeatures.Add(feature); Await(OppoProtocol.CmdBatchQueryResp); }
    private void PrepareSpatial(bool enabled)
    {
        if (enabled && _manager.Caps.GameSoundMutexSpatial && ReadSnapshot().CanGameSound)
        { _manager.SendGameSound(false); Await((ushort)(OppoProtocol.CmdQueryGameSound | 0x8000)); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _transport.FrameReceived -= FrameReceived;
        _manager.CommandFailed -= CommandFailed;
        _manager.Dispose();
    }
}
