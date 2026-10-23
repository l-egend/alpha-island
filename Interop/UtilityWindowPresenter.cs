using System.Windows;
using System.Windows.Interop;

namespace alpha_island.Interop;

/// <summary>Restores modeless tools independently of the island's hidden/minimized state.</summary>
internal static class UtilityWindowPresenter
{
    internal static void Show(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        var handle = new WindowInteropHelper(window).Handle;
        if (NativeMethods.GetWindowRect(handle, out var bounds))
        {
            var rectangle = System.Drawing.Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            bool reachable = System.Windows.Forms.Screen.AllScreens.Any(screen =>
            {
                var visible = System.Drawing.Rectangle.Intersect(screen.WorkingArea, rectangle);
                return visible.Width >= 100 && visible.Height >= 32;
            });
            if (!reachable)
            {
                var area = SystemParameters.WorkArea;
                window.Left = area.Left + Math.Max(0, (area.Width - window.Width) / 2);
                window.Top = area.Top + Math.Max(0, (area.Height - window.Height) / 2);
            }
        }
        WindowWorkArea.Fit(window); window.Activate(); NativeMethods.SetForegroundWindow(handle);
    }
}
