using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using alpha_island.Interop;

namespace alpha_island.Core;

/// <summary>Small bounded local stores: atomic replacement, serialized writes, optional current-user encryption.</summary>
internal sealed class LocalStateFile<T>(string path, int maximumBytes = 2 * 1024 * 1024, bool encrypted = false) : IDisposable
{
    private readonly object _gate = new();
    private Task _writer = Task.CompletedTask;
    private byte[]? _latest;
    private bool _writing, _disposed;
    internal T? Read()
    {
        try
        {
            if (!File.Exists(path)) return default;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > maximumBytes || file.Length == 0) return default;
            byte[] bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
            if (encrypted) bytes = Crypt(bytes, false);
            if (bytes.Length > maximumBytes) return default;
            return JsonSerializer.Deserialize<T>(bytes, new JsonSerializerOptions { MaxDepth = 16 });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
        { IslandLog.Warn("LocalState", "本机记录无法读取：" + ex.GetType().Name); return default; }
    }
    // Called with an immutable snapshot on the owning thread; file IO and encryption run off the UI thread.
    internal void Write(T snapshot)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        if (bytes.Length > maximumBytes) throw new InvalidDataException("本机记录超过容量限制");
        lock (_gate)
        {
            if (_disposed) return;
            _latest = bytes;
            if (!_writing) { _writing = true; _writer = Task.Run(Drain); }
        }
    }
    private void Drain()
    {
        while (true)
        {
            byte[] bytes;
            lock (_gate)
            {
                if (_latest == null) { _writing = false; return; }
                bytes = _latest; _latest = null;
            }
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                if (encrypted) bytes = Crypt(bytes, true);
                if (bytes.Length > maximumBytes) throw new InvalidDataException("加密记录超过容量限制");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { IslandLog.Warn("LocalState", "本机记录无法保存：" + ex.GetType().Name); }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
        }
    }
    internal bool Flush(TimeSpan timeout) { Task writer; lock (_gate) writer = _writer; return writer.Wait(timeout); }
    public void Dispose() { lock (_gate) _disposed = true; Flush(TimeSpan.FromSeconds(3)); }
    private static byte[] Crypt(byte[] bytes, bool protect)
    {
        IntPtr input = Marshal.AllocHGlobal(bytes.Length);
        NativeMethods.DATA_BLOB output = default;
        try
        {
            Marshal.Copy(bytes, 0, input, bytes.Length);
            var data = new NativeMethods.DATA_BLOB { cbData = bytes.Length, pbData = input };
            bool ok = protect ? NativeMethods.CryptProtectData(ref data, "Alpha Island notification history", IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, 1, out output) : NativeMethods.CryptUnprotectData(ref data, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.cbData]; Marshal.Copy(output.pbData, result, 0, result.Length); return result;
        }
        finally { Marshal.FreeHGlobal(input); if (output.pbData != IntPtr.Zero) NativeMethods.LocalFree(output.pbData); }
    }
}
