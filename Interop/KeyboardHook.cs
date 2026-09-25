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
    /// 运行在系统钩子线程上，只抛出按键事件，绝不阻塞或同步等待 UI 线程。
    /// </summary>
    public sealed class KeyboardHook : IDisposable
    {
        private NativeMethods.LowLevelKeyboardProc? _proc;
        private IntPtr _handle = IntPtr.Zero;
        private bool _disposed;

        /// <summary>键盘按下或松开事件。</summary>
        public event EventHandler<KeyboardHookEventArgs>? KeyAction;

        public bool IsHooked => _handle != IntPtr.Zero;

        public bool Hook()
        {
            if (_disposed || _handle != IntPtr.Zero) return _handle != IntPtr.Zero;

            _proc = HookCallback;
            IntPtr module = NativeMethods.GetModuleHandle(null);
            _handle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, module, 0);
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
                int msg = wParam.ToInt32();
                bool isKeyDown = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
                bool isKeyUp = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;

                if (isKeyDown || isKeyUp)
                {
                    var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
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

                    KeyAction?.Invoke(this, new KeyboardHookEventArgs(key, vk, modifiers, isKeyDown));
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
