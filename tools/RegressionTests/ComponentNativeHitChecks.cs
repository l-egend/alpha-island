using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using alpha_island.Animation;
using alpha_island.Controls;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;

internal static partial class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ComponentProbePoint { public int X, Y; }
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(ComponentProbePoint point);
    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    private static void ComponentNativeHitChecks(bool includeBody = false, double contourHeight = 48)
    {
        bool reduced = MotionPolicy.ReduceMotion, saving = MotionPolicy.PowerSave;
        int tier = MotionPolicy.RenderTier;
        var area = SystemParameters.WorkArea;
        var canvas = new Canvas { Width = 180, Height = contourHeight, Margin = new Thickness(20, 24, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        Border? body = null;
        if (includeBody)
        {
            body = new Border { Width = 180, Height = contourHeight, CornerRadius = new CornerRadius(24), Background = new SolidColorBrush(Color.FromArgb(220, 22, 28, 40)) };
            canvas.Children.Add(body);
        }
        var handle = new IslandContourHandle { Width = IslandContourHandle.TrackWidth, Height = contourHeight, ContourCornerRadius = new CornerRadius(24) };
        Canvas.SetLeft(handle, 180); Canvas.SetTop(handle, 0); canvas.Children.Add(handle);
        // Exercise the production kind of window, including Windows' layered pixel hit testing.
        var popup = new System.Windows.Window { Width = 300, Height = contourHeight + 56, Left = area.Left + 24, Top = area.Bottom - contourHeight - 80,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = null,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true, Content = canvas };
        HwndSource? source = null;
        IntPtr hwnd = IntPtr.Zero;
        var failures = new List<string>();
        void Probe(Point local, string label, bool expected = true)
        {
            var screen = handle.PointToScreen(local);
            var point = new ComponentProbePoint { X = (int)Math.Round(screen.X), Y = (int)Math.Round(screen.Y) };
            bool logical = handle.ContainsHandlePoint(local);
            bool receiver = WindowFromPoint(point) == hwnd;
            bool inputTarget = ReferenceEquals(popup.InputHitTest(popup.PointFromScreen(screen)), handle);
            Console.WriteLine($"Native handle {label}: logical={logical}, receivesMouse={receiver}, wpfTarget={inputTarget}, expected={expected}, offset={handle.HintOffset:0.00}");
            if (logical != expected || receiver != expected || inputTarget != expected) failures.Add(label);
        }
        try
        {
            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(true, false);
            popup.Show(); hwnd = new WindowInteropHelper(popup).Handle; source = HwndSource.FromHwnd(hwnd);
            source.AddHook((IntPtr _, int message, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (message != 0x0084) return IntPtr.Zero;
                var screen = new Point(unchecked((short)(l.ToInt64() & 0xffff)), unchecked((short)((l.ToInt64() >> 16) & 0xffff)));
                bool inside = handle.ContainsHandlePoint(handle.PointFromScreen(screen)) || (body != null && new Rect(body.RenderSize).Contains(body.PointFromScreen(screen)));
                handled = true; return inside ? new IntPtr(1) : new IntPtr(-1);
            });
            popup.UpdateLayout(); PumpAnimations(100); DwmFlush();
            double middle = contourHeight / 2;
            var bounds = handle.HitBounds;
            Console.WriteLine($"Native scenario: body={includeBody}, height={contourHeight:0}");
            Probe(new Point(IslandContourHandle.ContourGap, middle), "resting visible line");
            Probe(new Point(20, middle), "fixed area outside line");
            Probe(new Point(26, middle), "far edge of fixed area");
            Probe(new Point(20, bounds.Top + 2), "upper padding");
            Probe(new Point(20, bounds.Bottom - 2), "lower padding");
            Probe(new Point(26, bounds.Top + 2), "fixed rectangle corner");
            Probe(new Point(32, middle), "desktop beyond rectangle", false);
            MotionPolicy.Apply(false, false); handle.PlayHint(); PumpAnimations(430); DwmFlush();
            Probe(new Point(IslandContourHandle.ContourGap, middle), "original line position while hint moves");
            Probe(new Point(20, middle), "same fixed area while hint moves");
            Probe(new Point(IslandContourHandle.ContourGap + handle.HintOffset, middle), "moving visible line");
            Probe(new Point(26, bounds.Top + 2), "same rectangle corner while hint moves");
            PumpAnimations(900); DwmFlush();
            Probe(new Point(20, middle), "fixed area after hint returns");
            Probe(new Point(20, bounds.Bottom + 4), "desktop below padding", false);
            Check(failures.Count == 0, "真实透明窗口中，线条内外及动画期间的固定区域都接收鼠标；失败位置：" + string.Join(", ", failures));
        }
        finally
        {
            handle.StopHint(); popup.Close();
            MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduced, saving);
        }
    }
}
