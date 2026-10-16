using System.IO;
using System.Text;
using System.Threading.Channels;

namespace alpha_island.Core;

/// <summary>A bounded producer queue; only the consumer touches the filesystem.</summary>
internal sealed class RollingFileLogSink : IDisposable
{
    private sealed record Command(string? Text, TaskCompletionSource<bool>? Barrier = null);
    private readonly Channel<Command> _queue = Channel.CreateUnbounded<Command>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _files, _capacity;
    private readonly Task _consumer;
    private readonly Action? _beforeWrite;
    private static readonly UTF8Encoding Utf8 = new(false);
    private FileStream? _stream;
    private StreamWriter? _writer;
    private long _length, _dropped, _failures;
    private int _pending, _disposed;
    internal int Pending => Volatile.Read(ref _pending);
    internal long Dropped => Interlocked.Read(ref _dropped);
    internal long WriteFailures => Interlocked.Read(ref _failures);

    internal RollingFileLogSink(string path, long maxBytes, int files, int capacity = 2048, Action? beforeWrite = null)
    {
        if (maxBytes < 128 || files < 1 || capacity < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _path = path; _maxBytes = maxBytes; _files = files; _capacity = capacity;
        _beforeWrite = beforeWrite;
        _consumer = Task.Run(Consume);
    }
    internal bool TryWrite(string text, bool critical = false)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        int limit = critical ? _capacity : _capacity - Math.Min(128, _capacity / 8);
        if (Interlocked.Increment(ref _pending) > limit)
        {
            Interlocked.Decrement(ref _pending); Interlocked.Increment(ref _dropped); return false;
        }
        // A malformed plugin log cannot hold an arbitrarily large string in the queue.
        if (text.Length > 16_384) text = text[..16_384] + " [truncated]";
        if (_queue.Writer.TryWrite(new(text))) return true;
        Interlocked.Decrement(ref _pending); return false;
    }
    internal async Task<bool> FlushAsync(TimeSpan timeout)
    {
        if (_consumer.IsCompleted) return WriteFailures == 0;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new(null, completion))) return false;
        try { return await completion.Task.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { return false; }
    }
    private async Task Consume()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                int batch = 0;
                while (batch++ < 64 && _queue.Reader.TryRead(out var command))
                {
                    if (command.Barrier != null)
                    {
                        bool flushed = TryFlush();
                        command.Barrier.TrySetResult(flushed);
                        continue;
                    }
                    try { Append(command.Text!); }
                    catch { Interlocked.Increment(ref _failures); CloseWriter(); }
                    finally { Interlocked.Decrement(ref _pending); }
                }
                TryFlush();
            }
        }
        finally { TryFlush(); CloseWriter(); }
    }
    private void Append(string line)
    {
        _beforeWrite?.Invoke();
        int bytes = Utf8.GetByteCount(line) + Utf8.GetByteCount(Environment.NewLine);
        if (_writer == null) Open();
        if (_length > 0 && _length + bytes > _maxBytes)
        {
            CloseWriter();
            for (int i = _files - 1; i >= 1; i--)
            {
                string from = i == 1 ? _path : Rotated(i - 1);
                if (File.Exists(from)) File.Move(from, Rotated(i), overwrite: true);
            }
            if (_files == 1 && File.Exists(_path)) File.Delete(_path);
            Open();
        }
        _writer!.WriteLine(line); _length += bytes;
    }
    private string Rotated(int index) => Path.Combine(Path.GetDirectoryName(_path) ?? ".",
        $"{Path.GetFileNameWithoutExtension(_path)}.{index}{Path.GetExtension(_path)}");
    private void Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _length = _stream.Length;
        _writer = new StreamWriter(_stream, Utf8, 4096);
    }
    private bool TryFlush()
    {
        try { _writer?.Flush(); return true; }
        catch { Interlocked.Increment(ref _failures); CloseWriter(); return false; }
    }
    private void CloseWriter()
    {
        try { _writer?.Dispose(); } catch { }
        try { _stream?.Dispose(); } catch { }
        _writer = null; _stream = null;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        try { _consumer.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }
}
