using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Animation;
using alpha_island.Interop;
using Point = System.Windows.Point;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

internal static partial class Program
{
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr region);

    private static void SettingsAppearanceMotionChecks(alpha_island.Settings.SettingsWindow settings)
    {
        bool reduce = MotionPolicy.ReduceMotion, save = MotionPolicy.PowerSave;
        int tier = MotionPolicy.RenderTier;
        try
        {
            settings.LoadFrom(new alpha_island.Core.IslandOptions().Normalized());
            settings.Left = -32000; settings.Top = -32000;
            settings.ShowInForeground(requestFocus: false); PumpAnimations(35); settings.UpdateLayout();
            var own = new WindowInteropHelper(settings).Handle;
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                Check(NativeMethods.DwmGetWindowAttribute(own, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, out int round, sizeof(int)) == 0 && round == 2,
                    "设置窗口启用 Win11 原生圆角");
            var root = (Grid)settings.Content;
            var pixels = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            pixels.Render(root);
            var corner = new byte[4]; pixels.CopyPixels(new Int32Rect(0, 0, 1, 1), corner, 4, 0);
            var center = new byte[4]; pixels.CopyPixels(new Int32Rect(30, 30, 1, 1), center, 4, 0);
            Check(corner[3] == 0 && center[3] == 255, "设置界面裁去直角，内部背景保持不透明");
            Check(!settings.AllowsTransparency && System.Windows.Shell.WindowChrome.GetWindowChrome(settings) != null,
                "圆角窗口保留原生框架且无需透明窗口合成");

            MotionPolicy.OverrideRenderTierForDebug(2); MotionPolicy.Apply(true, false);
            // Establish the same initial page whether run alone or after the shared UI fixtures.
            ((TextBox)settings.FindName("SearchBox")).Text = "";
            PrivateCall(settings, "ApplyNavExpanded", true, false);
            PrivateCall(settings, "SelectPageByTag", "appearance");
            PumpAnimations(20); settings.UpdateLayout();
            MotionPolicy.Apply(false, false);
            var nav = (StackPanel)settings.FindName("NavPanel");
            var bar = (Border)settings.FindName("NavSelectionIndicator");
            var marker = (TranslateTransform)bar.RenderTransform;
            var focus = (FrameworkElement)settings.FindName("Page_Focus");
            var media = (FrameworkElement)settings.FindName("Page_Media");
            double previous = marker.Y;
            var pageElapsed = System.Diagnostics.Stopwatch.StartNew();
            PrivateCall(settings, "SelectPageByTag", "focus");
            bool markerStarted = marker.HasAnimatedProperties, pageStarted = focus.HasAnimatedProperties;
            PumpAnimations(40);
            var selected = nav.Children.OfType<RadioButton>().Single(i => Equals(i.Tag, "focus"));
            double target = selected.TranslatePoint(new Point(0, (selected.ActualHeight - bar.Height) / 2), (UIElement)settings.FindName("NavItemsSurface")).Y;
            if (SystemParameters.ClientAreaAnimation)
            {
                Check(markerStarted && pageStarted && target > previous
                    && marker.Y >= previous && marker.Y <= target
                    && (marker.Y < target || pageElapsed.ElapsedMilliseconds >= 200)
                    && focus.Opacity >= .2 && focus.Opacity <= 1
                    && ((TranslateTransform)focus.RenderTransform).Y >= 0
                    && (focus.Opacity < 1 || focus.HasAnimatedProperties || pageElapsed.ElapsedMilliseconds >= 180),
                    $"左侧选中标记平滑移动，同时内容页淡入并短距离移入（marker={marker.Y:0.00}/{target:0.00}, started={markerStarted}/{pageStarted}, elapsed={pageElapsed.ElapsedMilliseconds}ms）");
            }
            PrivateCall(settings, "SelectPageByTag", "system"); PumpAnimations(25);
            PrivateCall(settings, "SelectPageByTag", "media"); PumpAnimations(280);
            Check(media.Opacity == 1 && !media.HasAnimatedProperties && ((TranslateTransform)media.RenderTransform).Y == 0
                && focus.Visibility == Visibility.Collapsed && !marker.HasAnimatedProperties,
                "快速连续切页只保留最新页面，完成后释放动画时钟");
            var search = (TextBox)settings.FindName("SearchBox");
            search.Text = "番茄钟"; PumpAnimations(25);
            Check(bar.Visibility == Visibility.Collapsed, "搜索隐藏当前导航项时也隐藏选中标记");
            PrivateCall(settings, "SelectPageByTag", "focus"); PumpAnimations(250);
            Check(bar.Visibility == Visibility.Visible && marker.Y >= 0 && marker.Y < 36, "过滤后选中标记跟随重新排列的导航项");
            search.Text = ""; PumpAnimations(25);

            PrivateCall(settings, "SelectPageByTag", "focus"); PrivateCall(settings, "ApplyNavExpanded", false, true);
            PumpAnimations(30); MotionPolicy.Apply(true, false);
            Check(focus.Opacity == 1 && ((TranslateTransform)focus.RenderTransform).Y == 0 && !marker.HasAnimatedProperties
                && ((FrameworkElement)settings.FindName("NavPane")).Width == 48, "途中开启减弱动效立即结束内容、指示条和侧栏动画");
            MotionPolicy.Apply(false, true); PrivateCall(settings, "SelectPageByTag", "media");
            Check(media.Opacity == 1 && !media.HasAnimatedProperties && !marker.HasAnimatedProperties, "节能模式切页直接到位");
            MotionPolicy.Apply(false, false); PrivateCall(settings, "SelectPageByTag", "focus");
            PrivateCall(settings, "ApplyNavExpanded", true, true); settings.Hide();
            Check(!focus.HasAnimatedProperties && !marker.HasAnimatedProperties && ((FrameworkElement)settings.FindName("NavPane")).Width == 244,
                "设置隐藏时停止切页和侧栏动画");
            settings.ShowInForeground(requestFocus: false); PumpAnimations(30);
            Check(bar.Visibility == Visibility.Visible && focus.Opacity == 1 && !settings.Topmost, "再次打开恢复完整页面和选中标记，保持普通层级");
            PrivateCall(settings, "SelectPageByTag", "media"); settings.WindowState = WindowState.Minimized;
            Check(!media.HasAnimatedProperties && !marker.HasAnimatedProperties && !settings.Topmost, "最小化停止设置动画且不提升窗口层级");
            settings.ShowInForeground(requestFocus: false);
            settings.Hide();
            settings.MaximizeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(settings.WindowState == WindowState.Maximized && settings.SettingsShell.CornerRadius.TopLeft == 0
                && Equals(settings.MaximizeButton.ToolTip, "还原") && !settings.Topmost, "最大化按钮切换为还原并解除外框圆角");
            settings.MaximizeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(settings.WindowState == WindowState.Normal && settings.SettingsShell.CornerRadius.TopLeft == 8, "还原窗口恢复圆角");
            settings.ShowInForeground(requestFocus: false);

            using var legacy = new HwndSource(new HwndSourceParameters("Legacy round settings frame")
            { Width = 200, Height = 100, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
            WindowCorners.ApplyLegacyRegion(legacy.Handle, 8, 1.5);
            IntPtr region = CreateRectRgn(0, 0, 0, 0);
            try
            {
                Check(GetWindowRgn(legacy.Handle, region) != 0 && !PtInRegion(region, 0, 0) && PtInRegion(region, 50, 50),
                    "Win10 回退使用真实窗口圆角区域并按 DPI 缩放");
                WindowCorners.ApplyLegacyRegion(legacy.Handle, 0, 1.5);
                Check(GetWindowRgn(legacy.Handle, region) == 0, "最大化时解除圆角裁切以填满工作区");
            }
            finally { DeleteObject(region); }
            MotionPolicy.Apply(true, false);
            foreach (string page in new[] { "appearance", "focus", "media", "plugins" })
            {
                PrivateCall(settings, "SelectPageByTag", page); PumpAnimations(20); settings.UpdateLayout();
                string path = "bin/settings-win11-previews/" + page + ".png";
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                SaveThemePreview(root, path);
            }
        }
        finally { MotionPolicy.OverrideRenderTierForDebug(tier); MotionPolicy.Apply(reduce, save); }
    }
}
