using System;
using System.Runtime.InteropServices;
using alpha_island.Core;

namespace alpha_island.Interop
{
    public sealed class MouseButtonDownEventArgs : EventArgs
    {
        public MouseButtonDownEventArgs(int x, int y, int message)
        {
            X = x;
            Y = y;
            Message = message;
            Timestamp = Environment.TickCount64;
        }

        public int X { get; }
        public int Y { get; }
        public int Message { get; }
        public long Timestamp { get; }
    }

    /// <summary>
    /// 全局低级鼠标钩子。只负责"报告按下了哪个键、按在屏幕哪儿"。
    ///
    /// 三条铁律（都是踩过坑换来的）：
    ///   1. 钩子装在**自己的宿主线程**上（见 <see cref="HookThread"/>），绝不装到 UI 线程上 ——
    ///      低级钩子的回调由安装线程抽消息时执行，装到 UI 线程上就等于"UI 一卡，全机鼠标一起停"
    ///      （alpha_74 实测：downloads 采样占住 UI 线程 800ms，鼠标就冻结 800ms，每 2 秒一次）。
    ///   2. 回调里绝不阻塞：只做 O(1) 判断 + 把事件 Post 给 UI 线程，立刻 return。
    ///   3. 绝不能假定钩子能收到 WM_MOUSEMOVE 就等于"知道真实光标位置" ——
    ///      实测 SetCursorPos / 程序化移动根本走不到低级钩子，缓存下来的坐标永远是旧值。
    ///      热区判定一律现取 GetCursorPos。
    /// </summary>
    public sealed class MouseHook : IDisposable
    {
        private readonly HookThread _host = new("alpha-island.MouseHook");
        private NativeMethods.LowLevelMouseProc? _proc;
        private IntPtr _handle = IntPtr.Zero;
        private bool _disposed;

        private bool _isLeftDown;
        private bool _isLeftDownInsideIsland;
        private bool _interceptNextRButtonUp;
        private int _dragStartX;
        private int _dragStartY;
        private bool _dragThresholdPassed;

        /// <summary>鼠标按下（左/右/非客户区左键）。</summary>
        public event EventHandler<MouseButtonDownEventArgs>? ButtonDown;

        /// <summary>在按住左键拖拽时光标移动事件（当前坐标与按下起点坐标）。</summary>
        public event EventHandler<(int X, int Y, int StartX, int StartY)>? DragMove;

        /// <summary>在拖拽状态下左键抬起时触发。</summary>
        public event EventHandler? DragEnd;

        /// <summary>
        /// 把事件搬到别的线程（正常是 UI 线程的 Dispatcher）执行。
        /// 钩子线程只负责"立刻返回"，绝不在回调里跑订阅方的代码；
        /// 没设置时就在钩子线程上直接抛（订阅方必须自己保证不碰 UI 线程的东西）。
        /// </summary>
        public Action<Action>? Post { get; set; }

        /// <summary>
        /// 外部拦截右键回调。如果返回 true，则抑制本次右键按下事件穿透（阻止系统弹出右键菜单），
        /// 并由订阅方执行替换动作（如替换为自动粘贴）。
        /// ⚠ 这个回调是**同步**的（必须当场决定吃不吃这次右键），跑在钩子线程上，
        /// 必须 O(1) 返回、绝不 Dispatcher.Invoke 同步等 UI 线程。
        /// </summary>
        public Func<int, int, bool>? ShouldInterceptRightClick { get; set; }

        /// <summary>
        /// 判断坐标是否在灵动岛内部，用于彻底防止岛内点击或拖拽误触外部文件中转站。
        /// ⚠ 同样是同步回调，跑在钩子线程上：实现里必须走"缓存下来的岛屏幕矩形"，
        /// 不能去碰 WPF 元素（见 MainWindow.Interaction.cs 的 IsPointInsideIsland）。
        /// </summary>
        public Func<int, int, bool>? IsInsideIsland { get; set; }

        public bool IsHooked => _host.IsRunning;

        /// <summary>装上钩子；重复调用是安全的。返回是否成功。</summary>
        public bool Hook()
        {
            if (_disposed) return false;
            if (_host.IsRunning) return true;

            _proc = HookCallback;
            // 安装与卸载都在钩子线程上跑（HookThread.Loop），_handle 也因此只由那一个线程写。
            return _host.Start(
                install: () =>
                {
                    IntPtr module = NativeMethods.GetModuleHandle(null);
                    _handle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, module, 0);
                    return _handle;
                },
                uninstall: () =>
                {
                    if (_handle != IntPtr.Zero)
                    {
                        NativeMethods.UnhookWindowsHookEx(_handle);
                        _handle = IntPtr.Zero;
                    }
                    _proc = null;
                });
        }

        public void Unhook()
        {
            _host.Stop();
            _handle = IntPtr.Zero;
            _proc = null;
        }

        /// <summary>把订阅方的代码 Post 出去（没设 Post 就在钩子线程上直接跑）。</summary>
        private void Dispatch(Action action)
        {
            Action<Action>? post = Post;
            if (post == null)
            {
                action();
                return;
            }

            try
            {
                post(action);
            }
            catch (Exception ex)
            {
                // 宿主（UI 线程）已经在关：这一拍丢掉就行，绝不能让异常从钩子回调里冒出去。
                IslandLog.Debug("MouseHook", "事件投递失败（宿主可能正在退出）：" + ex.Message);
            }
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

                    int x = info.pt.X;
                    int y = info.pt.Y;
                    Dispatch(() => ButtonDown?.Invoke(this, new MouseButtonDownEventArgs(x, y, message)));
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
                    int x = info.pt.X;
                    int y = info.pt.Y;
                    Dispatch(() => ButtonDown?.Invoke(this, new MouseButtonDownEventArgs(x, y, message)));
                }
                else if (message == NativeMethods.WM_LBUTTONUP || message == NativeMethods.WM_NCLBUTTONUP)
                {
                    bool wasDown = _isLeftDown;
                    _isLeftDown = false;
                    _isLeftDownInsideIsland = false;
                    _dragThresholdPassed = false;
                    if (wasDown)
                    {
                        Dispatch(() => DragEnd?.Invoke(this, EventArgs.Empty));
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
                                int x = info.pt.X;
                                int y = info.pt.Y;
                                int sx = _dragStartX;
                                int sy = _dragStartY;
                                Dispatch(() => DragMove?.Invoke(this, (x, y, sx, sy)));
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
            _host.Dispose();
        }
    }
}
