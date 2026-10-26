using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using alpha_island.Core;
using alpha_island.Interop;
using Size = System.Windows.Size;

internal static partial class Program
{
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);

    private static void MultiDisplayUiChecks(alpha_island.MainWindow window)
    {
        foreach (var area in new[] { new Rect(0, 0, 1920, 1032), new Rect(0, 48, 1920, 1032),
            new Rect(64, 0, 1856, 1080), new Rect(-2560, -200, 2560, 1400),
            new Rect(1920, 300, 1080, 1920), new Rect(0, 0, 720, 480) })
        foreach (double scale in new[] { 1d, 1.25, 1.5, 1.75, 2, 2.5 })
        {
            var placement = DisplayPlacement.TopCentered(area, new DpiScale(scale, scale), new Size(1200, 340));
            Check(area.Contains(placement) && placement.Top == area.Top
                && Math.Abs(placement.Left + placement.Width / 2 - (area.Left + area.Width / 2)) <= .5
                && placement.Width == Math.Min(area.Width, 1200 * scale),
                $"工作区 {area}、缩放 {scale:P0}：透明宿主贴顶居中且完整留在目标屏幕内");
        }
        var handle = new WindowInteropHelper(window).EnsureHandle();
        Check(AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(handle), new IntPtr(-4)),
            "真实 WPF 窗口使用 PerMonitorV2，不依赖启动时的系统缩放比例");
        foreach (var (message, argument, reason) in new[] { (0x001A, 0x002F, "工作区变化"),
            (0x001A, 0, "任务栏设置变化"), (0x007E, 0, "主屏切换或拔插"), (0x02E0, 0, "窗口 DPI 变化") })
        {
            window.Left = 20; window.Top = 80;
            for (int i = 0; i < 12; i++) PrivateCall(window, "WndProc", handle, message, new IntPtr(argument), IntPtr.Zero, false);
            Check(AgentField<bool>(window, "_displayReflowPending"), reason + "合并为一次待处理重排");
            PumpAnimations(80);
            var primary = WindowWorkArea.PrimaryPixels();
            Check(NativeMethods.GetWindowRect(handle, out var bounds)
                && bounds.Top == primary.Top && bounds.Left >= primary.Left && bounds.Right <= primary.Right
                && Math.Abs((bounds.Left + bounds.Right) / 2d - (primary.Left + primary.Width / 2)) <= 1,
                reason + "后真实 HWND 回到主屏顶部居中，透明操作区不伸到副屏");
            Check(!AgentField<bool>(window, "_displayReflowPending"), reason + "后释放重排标记");
        }
        var dpi = WindowWorkArea.Dpi(window);
        var current = WindowWorkArea.Current(window);
        var physical = WindowWorkArea.PrimaryPixels();
        Check(Math.Abs(current.Width * dpi.DpiScaleX - physical.Width) <= 1
            && Math.Abs(current.Height * dpi.DpiScaleY - physical.Height) <= 1,
            "工具窗口工作区按其当前 HWND 的 DPI 换算，避免混用主屏与副屏缩放");
        window.Left = 25;
        PrivateCall(window, "WndProc", handle, 0x001A, new IntPtr(0x0014), IntPtr.Zero, false);
        PumpAnimations(30);
        Check(Math.Abs(window.Left - 25) < 1, "无关的系统设置消息不会反复移动主岛");
    }
}
