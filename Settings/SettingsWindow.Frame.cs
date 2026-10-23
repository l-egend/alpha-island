using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using alpha_island.Interop;

namespace alpha_island.Settings;

public partial class SettingsWindow
{
    private void InitializeSettingsFrame()
    {
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            int dark = 1, rounded = 2;
            NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref rounded, sizeof(int));
            UpdateSettingsFrame();
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) FitSettingsToWorkArea(); };
        System.ComponentModel.PropertyChangedEventHandler workAreaChanged = (_, args) =>
        {
            if (args.PropertyName == nameof(SystemParameters.WorkArea) && IsVisible)
                Dispatcher.BeginInvoke(new Action(() => FitSettingsToWorkArea()));
        };
        SystemParameters.StaticPropertyChanged += workAreaChanged;
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= workAreaChanged;
        RootGrid.SizeChanged += (_, _) => UpdateSettingsFrame();
        StateChanged += (_, _) => UpdateSettingsFrame();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() => { FitSettingsToWorkArea(); UpdateSettingsFrame(); }));
    }

    private void FitSettingsToWorkArea(Rect? area = null)
    {
        WindowWorkArea.Fit(this, area, 780, 540);
        if (Width < 780 && _navExpanded) ApplyNavExpanded(false, false);
    }

    private void UpdateSettingsFrame()
    {
        bool maximized = WindowState == WindowState.Maximized;
        double radius = maximized ? 0 : 8;
        SettingsShell.CornerRadius = new CornerRadius(radius);
        RootGrid.Clip = new RectangleGeometry(new Rect(0, 0, RootGrid.ActualWidth, RootGrid.ActualHeight), radius, radius);
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
        var handle = new WindowInteropHelper(this).Handle;
        // Win11 保留 DWM 的原生圆角和阴影；Win10 用窗口区域裁切，避免透明窗口的软件合成开销。
        if (handle != IntPtr.Zero && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            WindowCorners.ApplyLegacyRegion(handle, radius, VisualTreeHelper.GetDpi(this).DpiScaleX);
    }
}
