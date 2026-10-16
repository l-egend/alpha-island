using System.Threading.Channels;
using OppoPodsManager;

namespace AlphaIsland.Plugins.OppoPods;

internal sealed class EarbudController : IDisposable
{
    private sealed record Request(string Kind, EarbudDevice? Device = null, EarbudCommand? Command = null, EarbudOptions? Options = null);
    private readonly Func<IReadOnlyList<EarbudDevice>> _discover;
    private readonly Func<EarbudDevice, EarbudOptions, IEarbudSession> _factory;
    private readonly Action<string> _log;
    private readonly MultiBrandDiscovery? _multiDiscovery;
    private readonly object _lifecycle = new();
    private readonly Channel<Request> _queue = Channel.CreateBounded<Request>(new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _connectionCancellation;
    private Task _worker = Task.CompletedTask;
    private IEarbudSession? _session;
    private EarbudDevice? _target;
    private EarbudOptions _options = new();
    private volatile bool _manualDisconnect;
    private int _stopped, _disposed;
    private DateTime _nextRetry = DateTime.MaxValue;
    private EarbudSnapshot _snapshot = new();
    private IReadOnlyList<EarbudDevice> _devices = Array.Empty<EarbudDevice>();
    internal event Action? Changed;
    internal EarbudSnapshot Snapshot => Volatile.Read(ref _snapshot);
    internal IReadOnlyList<EarbudDevice> Devices => Volatile.Read(ref _devices);
    internal Task Completion => _worker;
    internal EarbudController(Action<string> log,
        Func<IReadOnlyList<EarbudDevice>>? discover = null,
        Func<EarbudDevice, EarbudOptions, IEarbudSession>? factory = null)
    {
        _log = log;
        _multiDiscovery = discover == null ? new MultiBrandDiscovery() : null;
        _discover = discover ?? (() => _multiDiscovery!.Discover());
        _factory = factory ?? ((device, options) => MultiBrandSupport.Create(device, options, _multiDiscovery!));
    }
    private static IReadOnlyList<EarbudDevice> DiscoverWindows()
    {
        var connected = DeviceDiscovery.ListConnected();
        return DeviceDiscovery.ListCandidates().Select(d => new EarbudDevice(d.addr, d.name, connected.Any(c => c.addr == d.addr))).ToArray();
    }
    internal void Start(CancellationToken lifetime, EarbudOptions options)
    {
        lock (_lifecycle)
        {
            if (_cancellation != null) return;
            _options = options;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            _worker = Task.Run(() => Run(_cancellation.Token));
        }
    }
    internal bool Refresh() => Enqueue(new("refresh"));
    internal bool Connect(EarbudDevice device) => device.Address != 0 && Enqueue(new("connect", device));
    internal bool Disconnect()
    {
        if (!Enqueue(new("disconnect"))) return false;
        _manualDisconnect = true;
        lock (_lifecycle) _connectionCancellation?.Cancel();
        Release();
        Publish(EarbudSnapshot.Offline("已断开控制连接", "耳机音频连接仍由 Windows 管理"));
        return true;
    }
    internal bool Execute(EarbudCommand command) => Snapshot.Allows(command) && Enqueue(new("command", Command: command));
    internal bool SetOptions(EarbudOptions options) => Enqueue(new("options", Options: options));
    private bool Enqueue(Request request)
    {
        lock (_lifecycle)
            return _cancellation is { IsCancellationRequested: false } && _queue.Writer.TryWrite(request);
    }
    private void Publish(EarbudSnapshot snapshot)
    {
        Volatile.Write(ref _snapshot, snapshot);
        Changed?.Invoke();
    }
    private async Task Run(CancellationToken token)
    {
        try
        {
            if (_options.AutoConnect) await RefreshDevices(token, autoConnect: true).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                while (_queue.Reader.TryRead(out var request))
                {
                    token.ThrowIfCancellationRequested();
                    try { await Handle(request, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        _log(error.GetBaseException().Message);
                        Publish(Snapshot with { Busy = false, Hint = error.GetBaseException().Message });
                    }
                }
                var session = CurrentSession();
                if (session != null)
                {
                    try
                    {
                        session.Pump(false);
                        var state = session.ReadSnapshot();
                        Publish(state);
                        if (!state.Connected) { Release(); _nextRetry = DateTime.UtcNow.AddSeconds(10); }
                    }
                    catch (Exception error) when (!token.IsCancellationRequested)
                    {
                        Release(); _log(error.GetBaseException().Message);
                        Publish(EarbudSnapshot.Offline("耳机控制连接已断开", error.GetBaseException().Message));
                        _nextRetry = DateTime.UtcNow.AddSeconds(10);
                    }
                }
                else if (!_manualDisconnect && _options.AutoReconnect && DateTime.UtcNow >= _nextRetry)
                {
                    _nextRetry = DateTime.UtcNow.AddSeconds(15);
                    if (_target != null) await Open(_target, token).ConfigureAwait(false);
                    else if (_options.AutoConnect) await RefreshDevices(token, autoConnect: true).ConfigureAwait(false);
                }
                await Task.Delay(120, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { _log(error.GetBaseException().Message); }
        finally
        {
            Release();
            while (_queue.Reader.TryRead(out _)) { }
            Publish(EarbudSnapshot.Offline("插件已停止"));
        }
    }
    private async Task Handle(Request request, CancellationToken token)
    {
        switch (request.Kind)
        {
            case "refresh":
                if (CurrentSession() is { } existing) { existing.Pump(true); Publish(existing.ReadSnapshot()); }
                else await RefreshDevices(token, autoConnect: _options.AutoConnect && !_manualDisconnect).ConfigureAwait(false);
                break;
            case "connect": _manualDisconnect = false; _target = request.Device; await Open(request.Device!, token).ConfigureAwait(false); break;
            case "disconnect":
                _manualDisconnect = true; _nextRetry = DateTime.MaxValue; Release(); Publish(EarbudSnapshot.Offline("已断开控制连接", "耳机音频连接仍由 Windows 管理")); break;
            case "command":
                if (CurrentSession() is { } session && session.ReadSnapshot().Allows(request.Command!))
                { session.Execute(request.Command!); Publish(session.ReadSnapshot()); }
                break;
            case "options":
                var old = _options; _options = request.Options!;
                if (old == _options) break;
                if (old.ModelOverride != _options.ModelOverride && _target != null && CurrentSession() != null)
                    await Open(_target, token).ConfigureAwait(false);
                else if (!old.AutoConnect && _options.AutoConnect && CurrentSession() == null)
                { _manualDisconnect = false; await RefreshDevices(token, autoConnect: true).ConfigureAwait(false); }
                break;
        }
    }
    private async Task RefreshDevices(CancellationToken token, bool autoConnect)
    {
        Publish(Snapshot with { Busy = true, Status = "正在查找耳机…", Hint = "" });
        // Discovery is synchronous and already running on this plugin's worker, never the dispatcher.
        var devices = _discover().GroupBy(d => d.Address).Select(g => g.First()).Where(d => d.Address != 0).ToArray();
        token.ThrowIfCancellationRequested();
        Volatile.Write(ref _devices, devices);
        Publish(EarbudSnapshot.Offline(devices.Length == 0 ? "未发现已配对的支持型号" : "请选择耳机并连接",
            devices.Length == 0 ? "在 Windows 中配对，或退出原耳机管理程序后重试" : "连接后读取电量与可用设置"));
        if (!autoConnect) return;
        var connected = devices.Where(d => d.Connected).ToArray();
        var preferred = connected.Where(d => string.Equals(d.Name, _options.PreferredName, StringComparison.OrdinalIgnoreCase)).ToArray();
        EarbudDevice? chosen = _options.PreferredName.Length > 0 ? preferred.Length == 1 ? preferred[0] : null
            : connected.Length == 1 ? connected[0] : null;
        if (chosen != null) { _target = chosen; await Open(chosen, token).ConfigureAwait(false); }
        else _nextRetry = DateTime.UtcNow.AddSeconds(15);
    }
    private async Task Open(EarbudDevice device, CancellationToken token)
    {
        Release();
        Publish(EarbudSnapshot.Offline("正在连接耳机…", device.Name) with { Busy = true, DeviceName = device.Name });
        IEarbudSession session = _factory(device, _options);
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (_lifecycle)
        {
            if (token.IsCancellationRequested) { session.Dispose(); token.ThrowIfCancellationRequested(); }
            _session = session;
            _connectionCancellation = connectionCancellation;
        }
        try
        {
            await session.ConnectAsync(connectionCancellation.Token).ConfigureAwait(false);
            connectionCancellation.Token.ThrowIfCancellationRequested();
            Publish(session.ReadSnapshot());
            _nextRetry = DateTime.MaxValue;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Release(); throw; }
        catch (OperationCanceledException) when (connectionCancellation.IsCancellationRequested)
        { Release(); _nextRetry = DateTime.MaxValue; Publish(EarbudSnapshot.Offline("已取消控制连接")); }
        catch (Exception error)
        {
            Release(); _nextRetry = DateTime.UtcNow.AddSeconds(10);
            Publish(EarbudSnapshot.Offline("控制连接失败", error.GetBaseException().Message));
        }
        finally { lock (_lifecycle) if (ReferenceEquals(_connectionCancellation, connectionCancellation)) _connectionCancellation = null; }
    }
    private IEarbudSession? CurrentSession() { lock (_lifecycle) return _session; }
    private void Release()
    {
        IEarbudSession? old;
        lock (_lifecycle) { old = _session; _session = null; }
        try { old?.Dispose(); } catch (Exception error) { _log(error.Message); }
    }
    internal void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        lock (_lifecycle) _cancellation?.Cancel();
        _multiDiscovery?.Dispose();
        // Close transport immediately, including a connection attempt still in progress.
        Release();
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        Changed = null;
        var cancellation = _cancellation;
        if (cancellation != null)
            _ = _worker.ContinueWith(_ => cancellation.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
