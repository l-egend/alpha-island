using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using alpha_island.Plugins.Isolation;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using Orientation = System.Windows.Controls.Orientation;

internal static partial class Program
{
    private static void PluginScrollingChecks()
    {
        var editor = new TextBox { Name = "ShortPrompt", Height = 64, AcceptsReturn = true,
            Text = "单行系统提示词", VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var content = new StackPanel();
        content.Children.Add(editor);
        content.Children.Add(new Border { Height = 800 });
        content.Children.Add(new CheckBox { Name = "LastPluginOption", Content = "最下方的选项", Height = 38 });
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Plugin scroll regression")
            { Width = 480, Height = 280, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        source.RootVisual = scroll;
        var surface = new PluginWorkerSurface(scroll);
        var frame = surface.Capture(480, 280);
        surface.Input(new("$settings", "wheel", Number: -120, X: 100, Y: 30));
        surface.Capture(480, 280);
        Console.WriteLine($"Plugin wheel over short prompt: outerOffset={scroll.VerticalOffset:F1}; extent={scroll.ScrollableHeight:F1}");
        Check(scroll.VerticalOffset > 0, "插件提示词内部无可滚内容时，滚轮继续滚动外层设置而不被吞掉");
        var commands = new List<RemoteCommand>();
        RemotePluginView? remote = null;
        remote = new RemotePluginView("$settings", command =>
        {
            commands.Add(command); surface.Input(command);
            remote!.Apply(surface.Capture(480, 280));
            return Task.CompletedTask;
        }, error => throw error);
        try
        {
            remote.Apply(surface.Capture(480, 280));
            remote.Measure(new System.Windows.Size(480, 280)); remote.Arrange(new Rect(0, 0, 480, 280)); remote.UpdateLayout();
            var bars = AgentField<Dictionary<int, FrameworkElement>>(remote, "_controls").Values.OfType<ScrollBar>().ToArray();
            Check(bars.Any(bar => bar.Orientation == Orientation.Vertical && bar.Maximum > 0),
                "隔离插件的滚动条是宿主真实可操作的控件，不是无法拖动的截图");
            var vertical = bars.First(bar => bar.Orientation == Orientation.Vertical && bar.Maximum > 0);
            vertical.Value = vertical.Maximum;
            PumpAnimations(40); scroll.UpdateLayout();
            Check(commands.Count > 0 && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1,
                "拖动宿主滚动条可跨进程到达设置底部");
            var last = surface.Capture(480, 280).Elements.Single(item => item.Name == "LastPluginOption");
            Check(last.Y >= 0 && last.Y + last.Height <= 280, "拖到底部后最后一个选项完整可见");
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            remote.RaiseEvent(wheel);
            Check(!wheel.Handled, "插件滚到底部后将滚轮交还宿主设置页，避免嵌套滚动陷阱");
        }
        finally { remote.Close(); }
    }
}
