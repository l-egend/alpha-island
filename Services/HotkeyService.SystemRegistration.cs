using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace alpha_island.Services;

public sealed partial class HotkeyService
{
    /// <summary>Uses Windows registration so conflicts are reported and held keys do not repeat.</summary>
    internal static bool TryRegisterSystem(HotkeyCombo combo, Action callback, out IDisposable? registration, out string error)
    {
        registration = null; error = "";
        try { registration = new SystemRegistration(combo, callback); return true; }
        catch (Win32Exception ex) { error = ex.NativeErrorCode == 1409 ? "快捷键已被其他程序占用" : "快捷键注册失败：" + ex.Message; return false; }
    }
    private sealed class SystemRegistration : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr window, int id);
        private readonly HwndSource _source;
        private readonly Action _callback;
        private bool _disposed;
        internal SystemRegistration(HotkeyCombo combo, Action callback)
        {
            _callback = callback;
            _source = new HwndSource(new HwndSourceParameters("Alpha Island launcher hotkey") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
            _source.AddHook(OnMessage);
            if (!RegisterHotKey(_source.Handle, 1, (uint)combo.Modifiers | 0x4000, (uint)KeyInterop.VirtualKeyFromKey(combo.Key)))
            { int error = Marshal.GetLastWin32Error(); _source.Dispose(); throw new Win32Exception(error); }
        }
        private IntPtr OnMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        { if (message == 0x312 && wParam.ToInt32() == 1 && !_disposed) { handled = true; _callback(); } return IntPtr.Zero; }
        public void Dispose()
        { if (_disposed) return; _disposed = true; UnregisterHotKey(_source.Handle, 1); _source.RemoveHook(OnMessage); _source.Dispose(); }
    }
}
