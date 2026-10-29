using System.IO;

namespace AlphaIsland.Plugins.DeviceBattery;

/// <summary>Separate overlapped read/write handles; pending I/O is drained before disposal.</summary>
internal sealed class HidSession : IDisposable
{
    internal static Action<string>? Trace { get; set; }
    private readonly FileStream _read, _write;
    private readonly int _input;
    internal HidSession(HidInterface device)
    {
        var read = HidNative.Open(device.Path, 0x80000000, true);
        if (read.IsInvalid) { read.Dispose(); throw new IOException("设备读取接口不可用"); }
        try { _read = new FileStream(read, FileAccess.Read, 1, true); } catch { read.Dispose(); throw; }
        var write = HidNative.Open(device.Path, 0x40000000, true);
        if (write.IsInvalid) { write.Dispose(); _read.Dispose(); throw new IOException("设备查询接口不可用"); }
        try { _write = new FileStream(write, FileAccess.Write, 1, true); } catch { write.Dispose(); _read.Dispose(); throw; }
        _input = Math.Clamp((int)device.Caps.InputBytes, 7, 4096);
    }
    internal async Task<byte[]?> Query(byte[] request, Func<byte[], bool> match, CancellationToken cancellation, int timeout = 500)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellation); bound.CancelAfter(timeout);
        byte[] buffer = new byte[_input]; Task<int>? pending = null;
        try
        {
            Trace?.Invoke("HID > " + Convert.ToHexString(request.AsSpan(0, Math.Min(8, request.Length))));
            pending = _read.ReadAsync(buffer.AsMemory(), bound.Token).AsTask();
            await _write.WriteAsync(request.AsMemory(), bound.Token);
            while (true)
            {
                int count = await pending;
                if (count == 0) return null;
                byte[] reply = buffer[..count]; Trace?.Invoke("HID < " + Convert.ToHexString(reply.AsSpan(0, Math.Min(8, reply.Length)))); if (match(reply)) return reply;
                pending = _read.ReadAsync(buffer.AsMemory(), bound.Token).AsTask();
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return null; }
        finally
        {
            bound.Cancel(); if (pending != null) { try { await pending; } catch (OperationCanceledException) { } }
        }
    }
    public void Dispose() { _read.Dispose(); _write.Dispose(); }
}
