using System;
using System.Runtime.InteropServices;

namespace alpha_island.Interop
{
    public sealed class MouseButtonDownEventArgs : EventArgs
    {
        public MouseButtonDownEventArgs(int x, int y, int message)
        {
            X = x;
            Y = y;
            Message = message;
        }

        public int X { get; }
        public int Y { get; }
        public int Message { get; }
    }

    /// <summary>
    /// 全局低级鼠标钩子。只负责"报告按下了哪个键、按在屏幕哪儿"。
    ///
    /// 两条铁律（踩过坑）：
    ///   1. 回调运行在钩子线程上，**绝不能同步等 UI 线程**，否则整机鼠标都会卡住；
    ///      所以这里只抛事件，由订阅方自己 Dispatcher.BeginInvoke。
    ///   2. 绝不能假定钩子能收到 WM_MOUSEMOVE 就等于"知道真实光标位置" ——
    ///      实测 SetCursorPos / 程序化移动根本走不到低级钩子，缓存下来的坐标永远是旧值。
    ///      热区判定一律现取 GetCursorPos。
    /// </summary>
    public sealed class MouseHook : IDisposable
    {
        private NativeMethods.LowLevelMouseProc? _proc;
        private IntPtr _handle = IntPtr.Zero;
        private bool _disposed;

        /// <summary>鼠标按下（左/右/非客户区左键）。已在钩子线程上，订阅方需自行切线程。</summary>
        public event EventHandler<MouseButtonDownEventArgs>? ButtonDown;

        public bool IsHooked => _handle != IntPtr.Zero;

        /// <summary>装上钩子；重复调用是安全的。返回是否成功。</summary>
        public bool Hook()
        {
            if (_disposed || _handle != IntPtr.Zero) return _handle != IntPtr.Zero;

            _proc = HookCallback;
            IntPtr module = NativeMethods.GetModuleHandle(null);
            _handle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, module, 0);
            if (_handle == IntPtr.Zero)
            {
                _proc = null;
                return false;
            }
            return true;
        }

        public void Unhook()
        {
            if (_handle != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_handle);
                _handle = IntPtr.Zero;
            }
            _proc = null;
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int message = wParam.ToInt32();
                if (message == NativeMethods.WM_LBUTTONDOWN
                    || message == NativeMethods.WM_RBUTTONDOWN
                    || message == NativeMethods.WM_NCLBUTTONDOWN)
                {
                    var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    // 只抛事件，绝不做任何等待 —— 这个回调会阻塞全系统的鼠标输入。
                    ButtonDown?.Invoke(this, new MouseButtonDownEventArgs(info.pt.X, info.pt.Y, message));
                }
            }
            return NativeMethods.CallNextHookEx(_handle, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Unhook();
        }
    }
}
