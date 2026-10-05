using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using alpha_island.Interop;

namespace alpha_island.Services
{
    /// <summary>热键组合定义。</summary>
    public readonly record struct HotkeyCombo(ModifierKeys Modifiers, Key Key);

    /// <summary>
    /// 全局热键与模拟快捷键服务。
    ///  1. 基于 KeyboardHook 捕获全局热键并在 UI Dispatcher 触发注册的回调；
    ///  2. 提供底层按键事件总线；
    ///  3. 提供 SendInput 模拟按键能力（如模拟执行 Ctrl+V 粘贴）。
    /// </summary>
    public sealed class HotkeyService : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly KeyboardHook _hook = new();
        private readonly ConcurrentDictionary<HotkeyCombo, Action> _hotkeys = new();
        private bool _disposed;

        /// <summary>任意按键按下时触发（已封送至 UI Dispatcher）。</summary>
        public event EventHandler<KeyboardHookEventArgs>? KeyPressed;

        public HotkeyService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _hook.KeyAction += OnHookKeyAction;
        }

        public void Start()
        {
            if (_disposed) return;
            bool ok = _hook.Hook();
            Debug.WriteLine("[HotkeyService] 钩子装配状态: " + ok);
        }

        public void Stop()
        {
            _hook.Unhook();
        }

        /// <summary>注册全局热键组合与回调动作。</summary>
        public void Register(ModifierKeys modifiers, Key key, Action callback)
        {
            if (callback == null) return;
            var combo = new HotkeyCombo(modifiers, key);
            _hotkeys[combo] = callback;
        }

        /// <summary>注销指定全局热键组合。</summary>
        public void Unregister(ModifierKeys modifiers, Key key)
        {
            var combo = new HotkeyCombo(modifiers, key);
            _hotkeys.TryRemove(combo, out _);
        }

        private void OnHookKeyAction(object? sender, KeyboardHookEventArgs e)
        {
            if (_disposed || !e.IsKeyDown) return;

            // 检查是否有精确匹配的热键
            var combo = new HotkeyCombo(e.Modifiers, e.Key);
            if (_hotkeys.TryGetValue(combo, out var action))
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Input, action);
            }

            // 派发通用按键通知
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (!_disposed) KeyPressed?.Invoke(this, e);
            });
        }

        /// <summary>
        /// 模拟发送 Ctrl+V 组合键执行系统级粘贴操作。
        /// </summary>
        public static void SimulatePaste()
        {
            try
            {
                var inputs = CreatePasteInputs();

                int size = Marshal.SizeOf<NativeMethods.INPUT>();
                uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, size);
                Debug.WriteLine("[HotkeyService] 模拟粘贴已触发, 成功发送事件数: " + sent);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HotkeyService] 模拟粘贴异常: " + ex.Message);
            }
        }

        internal static NativeMethods.INPUT[] CreatePasteInputs()
        {
            var inputs = new NativeMethods.INPUT[4];

            // 1. Ctrl Down
            inputs[0].type = NativeMethods.INPUT_KEYBOARD;
            inputs[0].u.ki.wVk = NativeMethods.VK_CONTROL;

            // 2. V Down
            inputs[1].type = NativeMethods.INPUT_KEYBOARD;
            inputs[1].u.ki.wVk = NativeMethods.VK_V;

            // 3. V Up
            inputs[2].type = NativeMethods.INPUT_KEYBOARD;
            inputs[2].u.ki.wVk = NativeMethods.VK_V;
            inputs[2].u.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;

            // 4. Ctrl Up
            inputs[3].type = NativeMethods.INPUT_KEYBOARD;
            inputs[3].u.ki.wVk = NativeMethods.VK_CONTROL;
            inputs[3].u.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;
            return inputs;
        }

        /// <summary>
        /// 在指定屏幕物理坐标处执行光标定位与模拟粘贴操作（先左键单击聚焦目标控件，再发送 Ctrl+V）。
        /// </summary>
        public static void SimulateRightClickPasteAt(int x, int y)
        {
            try
            {
                // 1. 发送左键点击聚焦目标窗口并定位光标
                var clickInputs = new NativeMethods.INPUT[2];
                clickInputs[0].type = NativeMethods.INPUT_MOUSE;
                clickInputs[0].u.mi.dwFlags = NativeMethods.MOUSEEVENTF_LEFTDOWN;
                clickInputs[1].type = NativeMethods.INPUT_MOUSE;
                clickInputs[1].u.mi.dwFlags = NativeMethods.MOUSEEVENTF_LEFTUP;

                int size = Marshal.SizeOf<NativeMethods.INPUT>();
                NativeMethods.SendInput(2, clickInputs, size);

                // 2. 稍等片刻让目标窗口完成激活与输入焦点获取，然后发送 Ctrl+V
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    SimulatePaste();
                };
                timer.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HotkeyService] 模拟定位粘贴异常: " + ex.Message);
                SimulatePaste();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _hook.KeyAction -= OnHookKeyAction;
            _hook.Dispose();
            _hotkeys.Clear();
        }
    }
}
