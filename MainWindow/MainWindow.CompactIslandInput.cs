using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using alpha_island.Core;
using alpha_island.Interop;
using Point = System.Windows.Point;

namespace alpha_island;

public partial class MainWindow
{
    private sealed record CompactIslandPress(FrameworkElement View, string Key, bool OpenCard, Point Start);
    private CompactIslandPress? _compactIslandPress;
    private long _compactIslandClickTick;
    private Func<Point?> _compactIslandPointer = static () => NativeMethods.TryGetCursorPos(out var point) ? new Point(point.X, point.Y) : null;

    private void RegisterCompactIslandInput(FrameworkElement view, Func<string?> resolveKey, bool openCard = false)
    {
        view.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (IsCardControlSource(e.OriginalSource as DependencyObject)) return;
            e.Handled = true;
            if (_exiting || GameDoNotDisturbActive || VoiceIslandVisible || resolveKey() is not { } key) return;
            if (e.ClickCount > 1 && Environment.TickCount64 - _compactIslandClickTick < System.Windows.Forms.SystemInformation.DoubleClickTime) return;
            CancelCompactIslandPress();
            _compactIslandPress = new(view, key, openCard, CompactPointerPosition(view, e));
            if (!view.CaptureMouse()) _compactIslandPress = null;
        };
        void Release(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_compactIslandPress is not { } press || !ReferenceEquals(press.View, view)) return;
            e.Handled = true;
            var dpi = VisualTreeHelper.GetDpi(view);
            bool click = (CompactPointerPosition(view, e) - press.Start).Length <= 8 * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY);
            CancelCompactIslandPress();
            if (!click || _exiting || GameDoNotDisturbActive || VoiceIslandVisible) return;
            _compactIslandClickTick = Environment.TickCount64;
            _hoverOutTicks = 0;
            if (TryOpenMessengerNotification(press.Key) || TryOpenAgentIsland(press.Key) || TryOpenBatteryIsland(press.Key)) return;
            if (press.OpenCard && PageForCompactIsland(press.Key) is { } page) SelectComponent(page);
            else SwitchMainIsland(press.Key);
        }
        view.PreviewMouseLeftButtonUp += Release;
        view.MouseLeftButtonUp += Release;
        view.LostMouseCapture += (_, _) =>
        {
            if (_compactIslandPress?.View == view) _compactIslandPress = null;
        };
        view.Unloaded += (_, _) =>
        {
            if (_compactIslandPress?.View == view) CancelCompactIslandPress();
        };
    }

    private Point CompactPointerPosition(FrameworkElement view, System.Windows.Input.MouseEventArgs e)
        => _compactIslandPointer() ?? view.PointToScreen(e.GetPosition(view));

    private void CancelCompactIslandPress()
    {
        var press = _compactIslandPress;
        _compactIslandPress = null;
        if (press?.View.IsMouseCaptured == true) press.View.ReleaseMouseCapture();
    }

    private bool IsCompactIslandDoubleClick(System.Windows.Input.MouseButtonEventArgs e)
        => e.ClickCount > 1 && Environment.TickCount64 - _compactIslandClickTick < System.Windows.Forms.SystemInformation.DoubleClickTime;
}
