using System.Runtime.InteropServices;
namespace AlphaIsland.EarbudsV2.Communication.Windows;
// Every imported protocol connection and temporary SDP query owns a balanced reference.
internal sealed class WinsockLease : IDisposable
{
    private static readonly object Gate = new();
    private static int _references;
    private bool _disposed;
    internal static int References { get { lock (Gate) return _references; } }
    private WinsockLease() { }
    internal static WinsockLease Acquire()
    {
        lock (Gate)
        {
            if (_references == 0)
            {
                var buffer = Marshal.AllocHGlobal(512);
                try { if (WSAStartup(0x0202, buffer) != 0) throw new InvalidOperationException("WSAStartup failed."); }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            _references++;
            return new();
        }
    }
    public void Dispose()
    {
        lock (Gate) { if (_disposed) return; _disposed = true; if (--_references == 0) WSACleanup(); }
    }
    [DllImport("ws2_32.dll")] private static extern int WSAStartup(ushort version, IntPtr data);
    [DllImport("ws2_32.dll")] private static extern int WSACleanup();
}
