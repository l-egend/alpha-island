using System;
using System.Threading;
using alpha_island.Core;

namespace alpha_island.Interop
{
    /// <summary>
    /// 低级钩子（<c>WH_MOUSE_LL</c> / <c>WH_KEYBOARD_LL</c>）的宿主线程。
    ///
    /// 为什么必须有这个类（alpha_74 实测的整机卡顿事故）：
    ///   低级钩子的回调不是"系统随便挑个线程"调用的，而是被投递到**安装钩子的那个线程的
    ///   消息队列**上，由那个线程抽消息时执行。所以只要把钩子装在 UI 线程上，
    ///   UI 线程任何一次卡顿（哪怕只是 downloads 采样里一次 800ms 的同步文件 I/O）
    ///   都会让回调无法返回，表现为**整机鼠标/键盘一起停住**（画面由合成器继续显示，所以
    ///   用户看到的是"只有鼠标不动、画面正常"）。
    ///
    /// 这个线程只做一件事：抽消息。上面不跑任何业务代码，因此 UI 线程再怎么卡，
    /// 钩子回调也照常返回，输入永远不卡。
    ///
    /// 用法：<c>Start(安装回调, 卸载回调)</c>；回调和卸载都跑在钩子线程上。
    /// </summary>
    internal sealed class HookThread : IDisposable
    {
        private readonly string _name;
        private readonly ManualResetEventSlim _ready = new(false);
        private Thread? _thread;
        private uint _threadId;
        private volatile bool _running;
        private bool _disposed;

        public HookThread(string name)
        {
            _name = name;
        }

        /// <summary>钩子是否已经装上（安装回调返回了非 0 句柄）。</summary>
        public bool IsRunning => _running;

        /// <summary>
        /// 起线程装钩子。返回是否装成功（安装回调返回 0 = 失败）。
        /// <paramref name="install"/> 与 <paramref name="uninstall"/> 都在钩子线程上执行。
        /// </summary>
        public bool Start(Func<IntPtr> install, Action? uninstall = null)
        {
            if (_disposed || _running) return _running;

            _ready.Reset();
            var thread = new Thread(() => Loop(install, uninstall))
            {
                IsBackground = true,
                Name = _name
            };
            _thread = thread;
            thread.Start();

            // 等安装结果：最多 3 秒，避免安装失败时把调用方卡死。
            return _ready.Wait(3000) && _running;
        }

        /// <summary>停线程并卸载钩子；重复调用是安全的。</summary>
        public void Stop()
        {
            Thread? thread = _thread;
            if (thread == null) return;

            // PostThreadMessage 投 WM_QUIT 结束消息循环；队列在 Loop 里安装钩子前就建好了。
            if (_threadId != 0)
            {
                NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }

            // 线程自己在 finally 里 UnhookWindowsHookEx（跨线程卸载虽然也允许，但这样更干净）。
            if (!thread.Join(1500))
            {
                // 极端情况：线程没退出也别让退出流程卡住 —— 它是后台线程，进程退出会收掉它。
                IslandLog.Debug("Hook", $"{_name} 线程未在 1.5s 内退出");
            }

            _thread = null;
            _threadId = 0;
            _running = false;
        }

        private void Loop(Func<IntPtr> install, Action? uninstall)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                _threadId = NativeMethods.GetCurrentThreadId();

                // 先把本线程的消息队列建出来：SetWindowsHookEx 之后系统要往这个队列投递回调，
                // 而 PostThreadMessage(WM_QUIT) 也要求队列已存在。
                NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, NativeMethods.PM_NOREMOVE);

                handle = install();
                _running = handle != IntPtr.Zero;
                _ready.Set();
                if (!_running) return;

                // 只抽消息，什么都不干 —— 这一条就是"整机输入不再被 UI 卡顿拖住"的全部秘密。
                while (NativeMethods.GetMessage(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    NativeMethods.TranslateMessage(ref msg);
                    NativeMethods.DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                IslandLog.Error("Hook", ex);
            }
            finally
            {
                _running = false;
                try
                {
                    uninstall?.Invoke();
                }
                catch (Exception ex)
                {
                    IslandLog.Error("Hook", ex);
                }

                _ready.Set();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _ready.Dispose();
        }
    }
}
