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

        private bool _isLeftDown;
        private bool _isLeftDownInsideIsland;
        private bool _interceptNextRButtonUp;
        private int _dragStartX;
        private int _dragStartY;
        private bool _dragThresholdPassed;

        /// <summary>鼠标按下（左/右/非客户区左键）。已在钩子线程上，订阅方需自行切线程。</summary>
        public event EventHandler<MouseButtonDownEventArgs>? ButtonDown;

        /// <summary>在按住左键拖拽时光标移动事件（当前坐标与按下起点坐标）。已在钩子线程上。</summary>
        public event EventHandler<(int X, int Y, int StartX, int StartY)>? DragMove;

        /// <summary>在拖拽状态下左键抬起时触发。已在钩子线程上。</summary>
        public event EventHandler? DragEnd;

        /// <summary>
        /// 外部拦截右键回调。如果返回 true，则抑制本次右键按下事件穿透（阻止系统弹出右键菜单），
        /// 并由订阅方执行替换动作（如替换为自动粘贴）。
        /// </summary>
        public Func<int, int, bool>? ShouldInterceptRightClick { get; set; }

        /// <summary>判断坐标是否在灵动岛内部，用于彻底防止岛内点击或拖拽误触外部文件中转站。</summary>
        public Func<int, int, bool>? IsInsideIsland { get; set; }

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

                if (message == NativeMethods.WM_RBUTTONDOWN)
                {
                    var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    if (ShouldInterceptRightClick?.Invoke(info.pt.X, info.pt.Y) == true)
                    {
                        // 拦截并吃掉右键按下事件，同时标记吃掉后续的抬起事件
                        _interceptNextRButtonUp = true;
                        return (IntPtr)1;
                    }

                    ButtonDown?.Invoke(this, new MouseButtonDownEventArgs(info.pt.X, info.pt.Y, message));
                }
                else if (message == NativeMethods.WM_RBUTTONUP)
                {
                    if (_interceptNextRButtonUp)
                    {
                        _interceptNextRButtonUp = false;
                        return (IntPtr)1;
                    }
                }
                else if (message == NativeMethods.WM_LBUTTONDOWN || message == NativeMethods.WM_NCLBUTTONDOWN)
                {
                    _isLeftDown = true;
                    var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    _dragStartX = info.pt.X;
                    _dragStartY = info.pt.Y;
                    _dragThresholdPassed = false;
                    _isLeftDownInsideIsland = IsInsideIsland?.Invoke(info.pt.X, info.pt.Y) == true;
                    ButtonDown?.Invoke(this, new MouseButtonDownEventArgs(info.pt.X, info.pt.Y, message));
                }
                else if (message == NativeMethods.WM_LBUTTONUP || message == NativeMethods.WM_NCLBUTTONUP)
                {
                    bool wasDown = _isLeftDown;
                    _isLeftDown = false;
                    _isLeftDownInsideIsland = false;
                    _dragThresholdPassed = false;
                    if (wasDown)
                    {
                        DragEnd?.Invoke(this, EventArgs.Empty);
                    }
                }
                else if (message == NativeMethods.WM_MOUSEMOVE)
                {
                    bool isDown = _isLeftDown || ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0);
                    if (isDown)
                    {
                        _isLeftDown = true;
                        if (!_isLeftDownInsideIsland)
                        {
                            var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                            int dx = info.pt.X - _dragStartX;
                            int dy = info.pt.Y - _dragStartY;
                            if (!_dragThresholdPassed)
                            {
                                if (dx * dx + dy * dy >= 30 * 30) // 移动超过30像素才视为有效拖拽
                                {
                                    _dragThresholdPassed = true;
                                }
                            }

                            if (_dragThresholdPassed)
                            {
                                DragMove?.Invoke(this, (info.pt.X, info.pt.Y, _dragStartX, _dragStartY));
                            }
                        }
                    }
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
