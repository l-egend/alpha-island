using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.ViewModels;
using Point = System.Windows.Point;
using ListBox = System.Windows.Controls.ListBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

internal static partial class Program
{
    private static void CardWheelInputChecks(alpha_island.MainWindow window, Grid root)
    {
        var pages = AgentField<IslandPageHost>(window, "_pages");
        var rows = new StackPanel();
        var first = new TextBlock { Text = "卡片内滚轮测试", Height = 35 };
        rows.Children.Add(first);
        rows.Children.Add(new Border { Height = 1200 });
        var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            RenderTransform = new System.Windows.Media.TranslateTransform() };
        window.CardViewport.Children.Add(scroll);
        const string id = "regression.card-scroll";
        pages.Register(new DelegateIslandPage(id, "滚动卡片", scroll, 2, IslandMetrics.CardHeightLyrics));
        try
        {
            PrivateCall(window, "SelectComponent", id); root.UpdateLayout();
            Check(scroll.ScrollableHeight > 100, "使用真实卡片视口创建可滚动页面");
            var scrollbar = Descendants<System.Windows.Controls.Primitives.ScrollBar>(scroll)
                .First(bar => bar.Orientation == System.Windows.Controls.Orientation.Vertical && bar.IsVisible);
            var thumb = Descendants<System.Windows.Controls.Primitives.Thumb>(scrollbar).First();
            PrivateCall(window, "IslandBody_PreviewMouseLeftButtonDown", window.IslandBody,
                new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = thumb });
            Check(!AgentField<bool>(window, "_isSwiping"), "拖动真实滚动条滑块不会启动整岛上滑收起手势");
            PrivateCall(window, "IslandBody_PreviewMouseLeftButtonUp", window.IslandBody,
                new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = thumb });
            Check(AgentField<IslandState>(window, "_state") == IslandState.Card, "滚动条释放鼠标后卡片保持展开");
            Check(thumb.CaptureMouse(), "真实滚动条滑块可以捕获拖动鼠标");
            try
            {
                SetAgentField(window, "_hoverOutTicks", 1000);
                PrivateCall(window, "HoverTimer_Tick", null, EventArgs.Empty);
                Check(AgentField<IslandState>(window, "_state") == IslandState.Card,
                    "滑块捕获鼠标时，即使光标离开热区也不触发悬停自动收起");
            }
            finally { thumb.ReleaseMouseCapture(); }
            void Wheel(UIElement target, int delta)
            {
                var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                target.RaiseEvent(args);
                if (!args.Handled) { args.RoutedEvent = Mouse.MouseWheelEvent; target.RaiseEvent(args); }
                PumpAnimations(40); root.UpdateLayout();
            }
            Wheel(first, -120);
            Check(pages.ActiveId == id && scroll.VerticalOffset > 0, "卡片正文滚轮滚动当前列表，不切换组件");
            scroll.ScrollToEnd(); PumpAnimations(40); Wheel(scroll, -120);
            Check(pages.ActiveId == id && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1,
                "列表到底后继续向下滚动仍留在当前卡片");
            scroll.ScrollToHome(); PumpAnimations(40); Wheel(scroll, 120);
            Check(pages.ActiveId == id && scroll.VerticalOffset == 0, "列表到顶后继续向上滚动仍留在当前卡片");
            Wheel(window.CardCarouselHost, -120);
            Check(pages.ActiveId == id, "卡片内没有滚动条的空白区域也不触发切页");
            Wheel(window.ComponentHandle, -120);
            Check(pages.ActiveId != id && AgentField<IslandState>(window, "_state") == IslandState.Card,
                "只有卡片外切换条接收滚轮切换组件");

            var pluginScroll = new ScrollViewer { Content = new Border { Height = 1000 } };
            var plugin = new alpha_island.Plugins.PluginIslandPage("wheel-probe",
                new AlphaIsland.PluginSdk.PluginPage("scroll", "插件滚动", () => pluginScroll), error => throw error);
            window.CardViewport.Children.Add(plugin.View); pages.Register(plugin);
            try
            {
                PrivateCall(window, "SelectComponent", plugin.Id); root.UpdateLayout();
                Wheel(pluginScroll, -120);
                Check(pages.ActiveId == plugin.Id && pluginScroll.VerticalOffset > 0,
                    "插件卡片在真实宿主中也接收到正文滚轮");
                pluginScroll.ScrollToEnd(); PumpAnimations(30); Wheel(pluginScroll, -120);
                Check(pages.ActiveId == plugin.Id, "插件卡片到底后不把滚轮交给组件切页");
                pluginScroll.ScrollToHome(); PumpAnimations(30); Wheel(pluginScroll, 120);
                Check(pages.ActiveId == plugin.Id, "插件卡片到顶后不把滚轮交给组件切页");
            }
            finally { pages.Unregister(plugin.Id); window.CardViewport.Children.Remove(plugin.View); }
        }
        finally { pages.Unregister(id); window.CardViewport.Children.Remove(scroll); PrivateCall(window, "SetState", IslandState.Bar, true); }
    }

    private static void ComponentMenuInputChecks(alpha_island.MainWindow window, Grid root)
    {
        PrivateCall(window, "SetState", IslandState.Pill, true); root.UpdateLayout();
        PrivateCall(window, "BeginComponentHold", new Point(), false);
        PrivateCall(window, "OpenComponentMenu", false);
        window.ComponentHandle.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = Mouse.LostMouseCaptureEvent });
        Check(AgentField<bool>(window, "_componentMenuVisible"), "长按菜单显示后临时丢失鼠标捕获不会令图标闪退");
        PrivateCall(window, "BeginComponentKeyboardNavigation");
        window.ComponentHandle.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        Check(AgentField<bool>(window, "_componentMenuVisible"), "长按后松手可保留菜单，继续单击选择组件");
        var menu = AgentField<ComponentRadialMenu>(window, "_componentMenu");
        menu.Measure(menu.Layout.Size); menu.Arrange(new Rect(menu.Layout.Size)); menu.UpdateLayout();
        PrivateCall(window, "UpdateComponentHover", menu.Layout.Centers[1], true);
        Check(!AgentField<bool>(window, "_componentMenuVisible") && AgentField<IslandState>(window, "_state") == IslandState.Card,
            "松手保留的菜单仍能选择图标并打开对应卡片");
        PrivateCall(window, "OpenComponentMenu", false);
        PrivateCall(window, "UpdateComponentHover", new Point(-10000, -10000), false);
        Check(!AgentField<bool>(window, "_componentMenuVisible"), "明确移出菜单交互区域仍可取消菜单");
        var previousScale = root.LayoutTransform;
        try
        {
            foreach (double scale in new[] { 1d, 1.25, 1.5, 2 })
            {
                root.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
                PrivateCall(window, "SetState", IslandState.Pill, true); root.UpdateLayout();
                PrivateCall(window, "BeginComponentHold", new Point(), false);
                PrivateCall(window, "OpenComponentMenu", true);
                PrivateCall(window, "BeginComponentKeyboardNavigation");
                AgentField<System.Windows.Threading.DispatcherTimer>(window, "_componentPointerTimer").Stop();
                PumpAnimations(80);
                var popup = AgentField<System.Windows.Controls.Primitives.Popup>(window, "_componentPopup");
                Check(popup.IsOpen && PresentationSource.FromVisual(menu) != null && menu.ActualWidth > 0,
                    $"{scale:0.##} 倍布局的真实弹出菜单完成独立窗口呈现");
                window.ComponentHandle.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                    { RoutedEvent = Mouse.LostMouseCaptureEvent });
                var icon = menu.PointFromScreen(menu.PointToScreen(menu.Layout.Centers[1]));
                Check(popup.IsOpen && menu.ItemAt(icon) == menu.ItemIds[1],
                    $"{scale:0.##} 倍布局下图标不因捕获切换消失且坐标命中一致");
                PrivateCall(window, "UpdateComponentHover", icon, true);
                Check(!popup.IsOpen && AgentField<IslandState>(window, "_state") == IslandState.Card,
                    $"{scale:0.##} 倍布局的实际菜单可选中并打开卡片");
            }
        }
        finally { PrivateCall(window, "CloseComponentSwitcher"); root.LayoutTransform = previousScale; }
    }

    private static void LauncherInputChecks(alpha_island.MainWindow window, Grid root)
    {
        var pages = AgentField<IslandPageHost>(window, "_pages");
        var target = new LauncherEntry("page:" + IslandPageIds.Volume, "音量", "岛内组件", LauncherKind.Component, IslandPageIds.Volume);
        int executions = 0;
        var model = new LauncherViewModel(async item => { executions++; await (Task)PrivateCall(window, "ExecuteLauncherAsync", item)!; });
        model.SetEntries(new[] { target });
        var launcher = new QuickLauncherWindow(model);
        var list = (ListBox)launcher.FindName("ResultList");
        var launcherRoot = (FrameworkElement)launcher.Content;
        launcherRoot.DataContext = model;
        launcher.Content = null;
        using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Launcher pointer regression")
            { Width = 520, Height = 440, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        source.RootVisual = launcherRoot; launcherRoot.UpdateLayout(); PumpAnimations(40); launcherRoot.UpdateLayout();
        try
        {
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            Check(row != null, "快速启动使用真实列表项执行指针交互");
            list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = list });
            list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = list });
            Check(executions == 0, "点击列表空白区域不会误启动当前选中项");
            list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = row });
            list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = list });
            Check(executions == 0, "从列表项拖到空白处松手不会误启动");
            void Click()
            {
                list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = row });
                list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = row });
                PumpAnimations(40); root.UpdateLayout();
            }
            Click();
            Check(executions == 1 && pages.ActiveId == target.Target && AgentField<IslandState>(window, "_state") == IslandState.Card,
                "单击快速启动列表项即可打开目标组件");
            Click();
            Check(executions == 1, "同一次快速双击不会重复执行启动操作");
            PrivateCall(window, "OnGlobalButtonDown", null, new MouseButtonDownEventArgs(1000000, 1000000, NativeMethods.WM_LBUTTONDOWN));
            PumpAnimations(40);
            Check(AgentField<IslandState>(window, "_state") == IslandState.Card, "快速启动交接期间的后续双击不会把刚打开的卡片收起");
            SetAgentField(window, "_componentSelectedTick", Environment.TickCount64 - 6000);
            Check((bool)PrivateCall(window, "HoldSelectedComponentCard", false)!, "启动器打开的卡片等待鼠标进入，不在短暂超时后自行收回");
            PrivateCall(window, "HoldSelectedComponentCard", true);
            Check(!AgentField<bool>(window, "_componentCardAwaitingPointer"), "进入卡片后恢复正常的离开收起行为");
            PrivateCall(window, "OnGlobalButtonDown", null, new MouseButtonDownEventArgs(1000000, 1000000, NativeMethods.WM_LBUTTONDOWN));
            PumpAnimations(40);
            Check(AgentField<IslandState>(window, "_state") != IslandState.Card, "交接结束后明确点击岛外仍正常收起卡片");
        }
        finally { launcher.Close(); PrivateCall(window, "SetState", IslandState.Bar, true); }
    }
}
