using System;
using System.Runtime.InteropServices;
using System.Text;

namespace alpha_island.Interop;

/// <summary>复制原生文本快照，不创建需要在终结器线程释放的 OLE 剪切板对象。</summary>
internal static class ClipboardTextReader
{
    private const uint UnicodeText = 13;
    internal const int MaxTextBytes = 8 * 1024 * 1024;
    private static readonly IReadApi SystemApi = new NativeApi();

    internal interface IReadApi
    {
        bool Open();
        void Close();
        bool HasFiles();
        bool HasText();
        IntPtr GetTextHandle();
        ulong Size(IntPtr handle);
        IntPtr Lock(IntPtr handle);
        void Unlock(IntPtr handle);
    }

    public static string? ReadText() => ReadText(SystemApi);

    internal static string? ReadText(IReadApi api)
    {
        if (!api.Open()) throw ReadFailure("打开剪贴板失败");
        try
        {
            // 文件载荷可能附带文件名文本，不能把它作为可右键代粘贴的文本记录。
            if (api.HasFiles() || !api.HasText()) return null;
            IntPtr handle = api.GetTextHandle();
            if (handle == IntPtr.Zero) throw ReadFailure("获取剪贴板文本失败");
            ulong size = api.Size(handle);
            if (size < 2 || size > MaxTextBytes || (size & 1) != 0) return null;
            IntPtr pointer = api.Lock(handle);
            if (pointer == IntPtr.Zero) throw ReadFailure("锁定剪贴板文本失败");
            try
            {
                var bytes = new byte[(int)size];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                for (int i = 0; i < bytes.Length; i += 2)
                    if (bytes[i] == 0 && bytes[i + 1] == 0)
                        return Encoding.Unicode.GetString(bytes, 0, i);
                return null; // 缺少终止符的载荷不能越过 GlobalSize 去读取内存。
            }
            finally { api.Unlock(handle); }
        }
        finally { api.Close(); }
        // 句柄属于系统：只配对解锁，绝不 GlobalFree，也不在 CloseClipboard 后保存句柄。
    }

    private static ExternalException ReadFailure(string message)
        => new(message, unchecked((int)0x800401D0)); // CLIPBRD_E_CANT_OPEN，交由监听服务有限重试。

    private sealed class NativeApi : IReadApi
    {
        private readonly uint[] _fileFormats = { 15, NativeMethods.RegisterClipboardFormat("FileGroupDescriptorW"),
            NativeMethods.RegisterClipboardFormat("FileGroupDescriptor"), NativeMethods.RegisterClipboardFormat("FileContents") };
        public bool Open() => NativeMethods.OpenClipboard(IntPtr.Zero);
        public void Close() => NativeMethods.CloseClipboard();
        public bool HasFiles() => _fileFormats.Any(format => format != 0 && NativeMethods.IsClipboardFormatAvailable(format));
        // Windows 会把 ANSI/OEM 文本合成为 CF_UNICODETEXT，统一按 UTF-16 读取。
        public bool HasText() => NativeMethods.IsClipboardFormatAvailable(UnicodeText);
        public IntPtr GetTextHandle() => NativeMethods.GetClipboardData(UnicodeText);
        public ulong Size(IntPtr handle) => NativeMethods.GlobalSize(handle).ToUInt64();
        public IntPtr Lock(IntPtr handle) => NativeMethods.GlobalLock(handle);
        public void Unlock(IntPtr handle) => NativeMethods.GlobalUnlock(handle);
    }
}
