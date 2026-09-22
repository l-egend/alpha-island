using System;
using System.Runtime.InteropServices;

namespace alpha_island.Interop
{
    /// <summary>
    /// 所有 Win32 调用集中在这里。原先这些 DllImport 直接堆在 MainWindow.xaml.cs 末尾，
    /// 和界面逻辑混在一起；抽出来后任何模块都能用，也方便单独核对签名。
    /// </summary>
    internal static class NativeMethods
    {
        // ---- 低级鼠标钩子 ----
        public const int WH_MOUSE_LL = 14;

        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_NCLBUTTONDOWN = 0x00A1;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);

        /// <summary>取当前光标位置。悬停热区判定必须每次现取 —— 钩子缓存的坐标不可信。</summary>
        public static bool TryGetCursorPos(out POINT pt) => GetCursorPos(out pt);

        // ---- 窗口模糊（DWM）----
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWCP_DEFAULT = 0;
        public const int DWMWCP_DONOTROUND = 1;

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        // ---- 窗口合成（SetWindowCompositionAttribute，实现真实亚克力/模糊）----
        public enum AccentState
        {
            Disabled = 0,
            EnableGradient = 1,
            EnableTransparentGradient = 2,
            EnableBlurBehind = 3,
            EnableAcrylicBlurBehind = 4,
            EnableHostBackdrop = 5
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct AccentPolicy
        {
            public AccentState AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        public const int WCA_ACCENT_POLICY = 19;

        [DllImport("user32.dll")]
        public static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        // ---- 截屏折射层要用的合成同步 ----
        /// <summary>
        /// 等一次 DWM 合成走完。抓屏前把岛隐身为 alpha=0 之后必须调它，
        /// 否则 BitBlt 到的还是上一帧（含岛自己）的桌面。
        /// </summary>
        [DllImport("dwmapi.dll")]
        public static extern int DwmFlush();
    }
}
