using System.Runtime.InteropServices;
using alpha_island.Core;
using alpha_island.Interop;

internal static partial class Program
{
    private static void MessengerWindowJumpChecks()
    {
        using var fixture = new MessengerNativeWindows();
        var processes = new HashSet<int> { Environment.ProcessId };
        IntPtr main = fixture.Create("Qt51514QWindowIcon", "微信", 894, 647);
        fixture.Create("Qt51514WxTrayIconMessageWindowClass", "WxTrayIconMessageWindow", 1440, 753);
        fixture.Create("Qt51514QWindowIcon", "Weixin", 176, 199);
        IntPtr Selected(MessengerClient client = MessengerClient.WeChat) => MessengerWindowReader.FindPrimaryWindow(client, processes);
        Check(Selected() == main, "较大的微信内部托盘消息窗口不会抢占主窗口跳转");
        Check(!NativeMethods.IsWindowVisible(main) && Selected() == main, "隐藏到托盘的微信主窗口仍能被选中");
        fixture.Create("Qt51514WxTrayIconMessageWindowClass", "微信", 1600, 900);
        fixture.Create("Qt51514QWindowIcon", "WxTrayIconMessageWindow", 1800, 1000);
        fixture.Create("Base_PowerMessageWindow", "微信", 1900, 1000);
        Check(Selected() == main, "同时核验主窗口类名和标题，排除伪装成应用名称的内部窗口");
        fixture.Create("Qt51514QWindowIcon", "微信设置", 1700, 1000);
        fixture.Create("Qt51514QWindowIcon", "微信图片预览", 1700, 1000);
        Check(Selected() == main, "设置和图片预览窗口不会替代聊天主窗口");
        Check(MessengerWindowReader.FindPrimaryWindow(MessengerClient.WeChat, new HashSet<int>()) == IntPtr.Zero,
            "跳转选择器不会读取或激活未确认的其他进程窗口");
        IntPtr qq = fixture.Create("Chrome_WidgetWin_1", "QQ", 900, 650);
        fixture.Create("TXGuiFoundation", "QQ消息提醒", 1700, 1000);
        Check(Selected(MessengerClient.QQ) == qq && Selected() == main, "QQ 与微信各自选择真实主窗口");
        fixture.Destroy(main);
        IntPtr minimized = fixture.Create("Qt51514QWindowIcon", "微信(3)", 894, 647, 0x20000000);
        Check(NativeMethods.IsIconic(minimized) && Selected() == minimized, "最小化且标题带未读数量的微信主窗口仍可恢复");
        Check(MessengerWindowReader.IsPrimaryWindow(MessengerClient.WeChat, "WeChatMainWndForPC", "WeChat")
            && MessengerWindowReader.IsPrimaryWindow(MessengerClient.WeChat, "mmui::MainWindow", "微信"), "兼容旧版与新版微信主窗口类型");
    }

    // Hidden windows in this process exercise the EnumWindows selection used by TryOpen.
    private sealed class MessengerNativeWindows : IDisposable
    {
        private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            public uint Style;
            public WndProc Procedure;
            public int ClassExtra, WindowExtra;
            public IntPtr Instance, Icon, Cursor, Background;
            public string? Menu;
            public string Name;
        }
        private readonly WndProc _procedure = DefWindowProc;
        private readonly IntPtr _instance = GetModuleHandle(null);
        private readonly HashSet<string> _classes = new();
        private readonly List<IntPtr> _windows = new();
        internal IntPtr Create(string type, string title, int width, int height, uint extraStyle = 0)
        {
            if (_classes.Add(type))
            {
                var nativeClass = new WindowClass { Procedure = _procedure, Instance = _instance, Name = type };
                if (RegisterClass(ref nativeClass) == 0) throw new System.ComponentModel.Win32Exception();
            }
            IntPtr window = CreateWindowEx(0x80, type, title, 0x00CF0000 | extraStyle, -30000, -30000,
                width, height, IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
            if (window == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            _windows.Add(window); return window;
        }
        internal void Destroy(IntPtr window) { DestroyWindow(window); _windows.Remove(window); }
        public void Dispose()
        {
            foreach (var window in _windows) DestroyWindow(window);
            foreach (var type in _classes) UnregisterClass(type, _instance);
            GC.KeepAlive(_procedure);
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WindowClass type);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint extended, string type, string title, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string type, IntPtr instance);
    }
}
