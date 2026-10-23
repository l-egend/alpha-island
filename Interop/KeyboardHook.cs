using System;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace alpha_island.Interop
{
    public sealed class KeyboardHookEventArgs : EventArgs
    {
        public Key Key { get; }
        public int VirtualKey { get; }
        public ModifierKeys Modifiers { get; }
        public bool IsKeyDown { get; }
        public bool IsInjected { get; init; }
        /// <summary>Only synchronous hook subscribers can suppress an event (used during shortcut recording).</summary>
        public bool Handled { get; set; }

        public KeyboardHookEventArgs(Key key, int virtualKey, ModifierKeys modifiers, bool isKeyDown)
        {
            Key = key;
            VirtualKey = virtualKey;
            Modifiers = modifiers;
            IsKeyDown = isKeyDown;
        }
    }

    /// <summary>
    /// 全局低级键盘钩子。
    /// 装在**自己的宿主线程**上（见 <see cref="HookThread"/>），只抛出按键事件，
    /// 绝不阻塞或同步等待 UI 线程 —— 装到 UI 线程上会让整机键盘跟着 UI 卡顿一起停
    /// （低级钩子的回调由安装线程抽消息时执行，理由同 MouseHook）。
    /// 订阅方必须自己切到 UI 线程（HotkeyService 已经这么做了）。
    /// </summary>
    public sealed class KeyboardHook : IDisposable
    {
        private readonly HookThread _host = new("alpha-island.KeyboardHook");
        private NativeMethods.LowLevelKeyboardProc? _proc;
        private IntPtr _handle = IntPtr.Zero;
        private bool _disposed;

        /// <summary>键盘按下或松开事件。</summary>
        public event EventHandler<KeyboardHookEventArgs>? KeyAction;

        public bool IsHooked => _host.IsRunning;

        public bool Hook()
        {
            if (_disposed) return false;
            if (_host.IsRunning) return true;

            _proc = HookCallback;
            return _host.Start(
                install: () =>
                {
                    IntPtr module = NativeMethods.GetModuleHandle(null);
                    _handle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, module, 0);
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

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                bool isKeyDown = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
                bool isKeyUp = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;

                if (isKeyDown || isKeyUp)
                {
                    var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                    // Do not feed our outbound shortcut into island hotkeys or approval handlers.
                    if (info.dwExtraInfo == NativeMethods.ExternalShortcutInputTag)
                        return NativeMethods.CallNextHookEx(_handle, nCode, wParam, lParam);
                    int vk = (int)info.vkCode;
                    Key key = KeyInterop.KeyFromVirtualKey(vk);

                    ModifierKeys modifiers = ModifierKeys.None;
                    if ((NativeMethods.GetKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0)
                        modifiers |= ModifierKeys.Control;
                    if ((NativeMethods.GetKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0)
                        modifiers |= ModifierKeys.Shift;
                    if ((NativeMethods.GetKeyState(NativeMethods.VK_MENU) & 0x8000) != 0)
                        modifiers |= ModifierKeys.Alt;
                    if ((NativeMethods.GetKeyState(NativeMethods.VK_LWIN) & 0x8000) != 0 ||
                        (NativeMethods.GetKeyState(NativeMethods.VK_RWIN) & 0x8000) != 0)
                        modifiers |= ModifierKeys.Windows;

                    var args = new KeyboardHookEventArgs(key, vk, modifiers, isKeyDown) { IsInjected = (info.flags & 0x10) != 0 };
                    KeyAction?.Invoke(this, args);
                    if (args.Handled) return new IntPtr(1);
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
