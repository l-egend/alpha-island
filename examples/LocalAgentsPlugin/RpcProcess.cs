using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
namespace AlphaIsland.Plugins.LocalAgents;
internal sealed class RpcProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ConcurrentDictionary<int,TaskCompletionSource<JsonElement>> _waiting = new();
    private readonly SemaphoreSlim _write = new(1);
    private readonly Channel<JsonElement> _events = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(2048) { SingleReader=true, SingleWriter=true, FullMode=BoundedChannelFullMode.Wait });
    private readonly Task _reader, _errors;
    private int _id;
    internal ChannelReader<JsonElement> Events => _events.Reader;
    internal int ProcessId=>_process.Id;
    internal RpcProcess(string executable, IReadOnlyList<string> arguments, string cwd, string? home = null)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory=cwd, UseShellExecute=false, CreateNoWindow=true,
            RedirectStandardInput=true, RedirectStandardOutput=true, RedirectStandardError=true,
            StandardInputEncoding=new UTF8Encoding(false), StandardOutputEncoding=Encoding.UTF8, StandardErrorEncoding=Encoding.UTF8 };
        foreach (string arg in arguments) start.ArgumentList.Add(arg);
        if (home != null) start.Environment["DSH_HOME"] = home;
        _process = Process.Start(start) ?? throw new IOException(L.T("无法启动本机 Agent"));
        _reader = ReadAsync(); _errors = DrainErrorsAsync();
    }
    private async Task DrainErrorsAsync()
    { var buffer = new char[1024]; try { while (await _process.StandardError.ReadAsync(buffer)>0) { } } catch { } }
    private async Task ReadAsync()
    {
        Exception? error = null;
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length > 4_000_000) throw new IOException(L.T("Agent 返回的数据过大"));
                JsonElement message;
                try { using var document = JsonDocument.Parse(line); message = document.RootElement.Clone(); } catch (JsonException) { continue; }
                if (!message.TryGetProperty("method", out _) && message.TryGetProperty("id",out var id) && id.TryGetInt32(out int number) && _waiting.TryRemove(number,out var request))
                {
                    if (message.TryGetProperty("error",out var failure)) request.TrySetException(new IOException(L.T("Agent 接口拒绝请求：") + AgentProtocol.SafeError(failure)));
                    else request.TrySetResult(message.TryGetProperty("result",out var result) ? result : default);
                }
                else if(!_events.Writer.TryWrite(message)) throw new IOException(L.T("Agent 输出过快，请缩小任务后重试"));
            }
        }
        catch (Exception ex) { error=ex; }
        finally
        {
            var failure = error ?? new IOException(L.T("Agent 进程已退出，请检查对应应用的登录和版本后重试"));
            foreach (var request in _waiting.Values) request.TrySetException(failure);
            _waiting.Clear(); _events.Writer.TryComplete(error);
        }
    }
    internal async Task<JsonElement> CallAsync(string method, object? args, CancellationToken token)
    {
        int id=Interlocked.Increment(ref _id); var source=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously); _waiting[id]=source;
        try { await WriteAsync(new { jsonrpc="2.0", id, method, @params=args },token); return await source.Task.WaitAsync(TimeSpan.FromSeconds(45),token); }
        finally { _waiting.TryRemove(id,out _); }
    }
    internal Task NotifyAsync(string method,object? args,CancellationToken token) => WriteAsync(new { jsonrpc="2.0",method,@params=args },token);
    internal Task AnswerAsync(JsonElement id, object result, CancellationToken token) => WriteAsync(new { jsonrpc="2.0",id,result },token);
    internal async Task WriteAsync(object data, CancellationToken token)
    {
        await _write.WaitAsync(token);
        try { await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(data).AsMemory(),token); await _process.StandardInput.FlushAsync(token); }
        finally { _write.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        try { _process.StandardInput.Close(); } catch { }
        try { if (!_process.HasExited) _process.Kill(true); } catch { }
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        try { await Task.WhenAll(_reader,_errors).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        _process.Dispose();
    }
}
