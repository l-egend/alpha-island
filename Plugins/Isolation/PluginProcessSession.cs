using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Windows.Threading;

namespace alpha_island.Plugins.Isolation;

internal sealed partial class PluginProcessSession : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly string _pipeName = "AlphaIsland.Plugin." + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<PluginPacket>> _pending = new();
    private readonly ConcurrentQueue<RemoteEvent> _events = new();
    private int _eventCount, _eventScheduled;
    private readonly Dispatcher _dispatcher;
    private PluginProcessJob? _job;
    private Process? _process;
    private long _sequence;
    private int _disposed, _failed;
    private readonly System.Text.StringBuilder _startupError = new();
    private volatile bool _connected;
    internal event Action<RemoteEvent>? Event;
    internal event Action<Exception>? Failed;
    internal int ProcessId => _process?.Id ?? 0;
    internal bool IsAlive { get { try { return _process?.HasExited == false; } catch { return false; } } }
    internal PluginProcessSession(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }
    internal async Task<RemoteRegistry> StartAsync(PluginStart start, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string executable = Path.Combine(AppContext.BaseDirectory, "alpha-island.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("找不到插件隔离运行程序", executable);
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = start.Directory };
        foreach (string arg in new[] { "--plugin-worker", _pipeName, nonce }) info.ArgumentList.Add(arg);
        _job = new PluginProcessJob();
        _process = Process.Start(info) ?? throw new IOException("插件进程未能启动");
        _job.Assign(_process);
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => { if (Volatile.Read(ref _disposed) != 0) return; try { Fault(new IOException($"插件进程已退出（{_process.ExitCode}）")); } catch (InvalidOperationException) { } };
        _ = Drain(_process.StandardError, true); _ = Drain(_process.StandardOutput, false);
        try { await _pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!IsAlive)
        { lock (_startupError) throw new IOException("插件启动失败：" + _startupError, new IOException("退出码 " + _process.ExitCode)); }
        var hello = await PluginWire.Receive(_pipe, deadline.Token).ConfigureAwait(false);
        if (hello.Kind != "hello" || hello.Data.GetString() != nonce) throw new InvalidDataException("插件通信身份验证失败");
        _connected = true;
        _ = Receive();
        return await Request<RemoteRegistry>("initialize", start, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
    }
    private async Task Drain(StreamReader reader, bool startup)
    {
        var buffer = new char[2048];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                if (startup && !_connected) lock (_startupError)
                    if (_startupError.Length < 4096) _startupError.Append(buffer, 0, Math.Min(count, 4096 - _startupError.Length));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
    internal async Task<T> Request<T>(string kind, object value, TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_pending.Count >= 128) throw new InvalidOperationException("插件请求数量超过限制");
        long id = Interlocked.Increment(ref _sequence);
        var completion = new TaskCompletionSource<PluginPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException("插件通信序号冲突");
        try
        {
            await PluginWire.Write(_pipe, new(id, kind, PluginWire.Element(value)), _write, _lifetime.Token).ConfigureAwait(false);
            var answer = await completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(15), _lifetime.Token).ConfigureAwait(false);
            if (answer.Error != null) throw new InvalidOperationException(answer.Error);
            return PluginWire.Read<T>(answer.Data);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        { Fault(ex); throw; }
        finally { _pending.TryRemove(id, out _); }
    }
    private async Task Receive()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var packet = await PluginWire.Receive(_pipe, _lifetime.Token).ConfigureAwait(false);
                if (packet.Id != 0)
                { if (_pending.TryGetValue(packet.Id, out var completion)) completion.TrySetResult(packet); }
                else
                {
                    if (Interlocked.Increment(ref _eventCount) > 256) throw new InvalidDataException("插件事件过于频繁");
                    _events.Enqueue(ReadEvent(packet.Data));
                    if (Interlocked.Exchange(ref _eventScheduled, 1) == 0) Post(DispatchEvents);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Text.Json.JsonException or InvalidDataException)
        { if (!_lifetime.IsCancellationRequested) Fault(ex); }
    }
    private static RemoteEvent ReadEvent(System.Text.Json.JsonElement data)
    {
        var item = PluginWire.Read<RemoteEvent>(data);
        if (item.Name == null || item.Key == null || item.Text == null || item.Detail == null || item.Name.Length > 32
            || item.Key.Length > 256 || item.Text.Length > 4096 || item.Detail.Length > 8192 || item.Value?.GetRawText().Length > 65536)
            throw new InvalidDataException("插件事件超过容量限制");
        return item;
    }
    private void DispatchEvents()
    {
        for (int count = 0; count < 32 && _events.TryDequeue(out var item); count++)
        {
            Interlocked.Decrement(ref _eventCount);
            if (Volatile.Read(ref _disposed) == 0)
                try { Event?.Invoke(item); } catch (Exception ex) { Fault(ex); return; }
        }
        Volatile.Write(ref _eventScheduled, 0);
        if (!_events.IsEmpty && Interlocked.Exchange(ref _eventScheduled, 1) == 0) Post(DispatchEvents);
    }
    private void Fault(Exception error)
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _failed, 1) != 0) return;
        foreach (var request in _pending.Values) request.TrySetException(error);
        _lifetime.Cancel(); Post(() => Failed?.Invoke(error));
    }
    private void Post(Action action)
    {
        try { if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(DispatcherPriority.Background, action); }
        catch (InvalidOperationException) when (_dispatcher.HasShutdownStarted) { }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Event = null; Failed = null;
        foreach (var request in _pending.Values) request.TrySetCanceled();
        Completion = StopWorkerAsync();
    }
}