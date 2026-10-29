using System.Diagnostics;
using System.Text;
using System.Windows.Interop;
using alpha_island.Interop;

internal static partial class Program
{
    private static void BackgroundWindowChecks(alpha_island.MainWindow window)
    {
        window.Left = window.Top = -32000;
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        void CheckNativeStyle(string name)
        {
            int style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            Check((style & NativeMethods.WS_EX_TOOLWINDOW) != 0 && (style & NativeMethods.WS_EX_APPWINDOW) == 0, name);
            Check((style & (NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT)) == 0,
                "隐藏切换入口时主岛仍支持激活和交互，不变为穿透窗口");
        }
        CheckNativeStyle("创建主窗口时使用工具窗口样式，排除 Alt+Tab 和任务栏");
        window.Show(); PumpAnimations(80);
        Check(NativeMethods.IsWindowVisible(hwnd) && !window.ShowInTaskbar && !window.ShowActivated, "后台启动仍显示主岛，但不抢前台焦点");
        CheckNativeStyle("WPF 显示窗口后仍保留 Alt+Tab 排除样式");
        window.Hide(); window.Show(); PumpAnimations(80);
        CheckNativeStyle("主岛隐藏后重新显示仍不进入 Alt+Tab");
        window.Hide();
    }

    // 只读核验已安装进程的真实 HWND，不开启 Alt+Tab，也不操作其他应用窗口。
    private static void BackgroundWindowLiveCheck()
    {
        var processes = Process.GetProcessesByName("alpha-island");
        string? installDirectory = Environment.GetEnvironmentVariable("ALPHA_ISLAND_TEST_INSTALL_DIRECTORY");
        Check(!string.IsNullOrWhiteSpace(installDirectory), "通过 ALPHA_ISLAND_TEST_INSTALL_DIRECTORY 指定实机检查的安装目录");
        string expectedExecutable = System.IO.Path.Combine(System.IO.Path.GetFullPath(installDirectory!), "alpha-island.exe");
        Check(processes.Length == 1 && string.Equals(processes[0].MainModule!.FileName,
            expectedExecutable, StringComparison.OrdinalIgnoreCase), "唯一运行实例来自指定测试安装");
        var titles = new StringBuilder(256);
        IntPtr main = IntPtr.Zero;
        NativeMethods.EnumWindowProc callback = (window, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(window, out uint process);
            if (process != processes[0].Id || !NativeMethods.IsWindowVisible(window)) return true;
            titles.Clear(); ReadBackgroundWindowTitle(window, titles, titles.Capacity);
            if (titles.ToString() == "Alpha Island") main = window;
            return true;
        };
        NativeMethods.EnumWindows(callback, IntPtr.Zero);
        Check(main != IntPtr.Zero, "实机主岛窗口仍可见并在后台运行");
        int style = NativeMethods.GetWindowLong(main, NativeMethods.GWL_EXSTYLE);
        Check((style & NativeMethods.WS_EX_TOOLWINDOW) != 0 && (style & NativeMethods.WS_EX_APPWINDOW) == 0,
            "实机主岛 HWND 已应用 Alt+Tab 排除样式");
        Check((style & NativeMethods.WS_EX_NOACTIVATE) == 0, "实机主岛仍允许激活并接收键盘输入");
        Console.WriteLine($"Native main window style: 0x{style:X8}");
        Console.WriteLine($"All {_checks} installed background window checks passed.");
        foreach (var process in processes) process.Dispose();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int ReadBackgroundWindowTitle(IntPtr window, StringBuilder title, int length);
}