using System.Windows;
using System.Windows.Controls;
using alpha_island.Controls;
using Button = System.Windows.Controls.Button;

internal static partial class Program
{
    private static void ScreenToolsOpenChecks(alpha_island.MainWindow window, Grid root)
    {
        var button = Descendants<Button>(root).Single(view => System.Windows.Automation.AutomationProperties.GetName(view) == "文字翻译 / 扫码");
        try
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpAnimations(100);
            var tools = AgentField<ScreenToolsWindow?>(window, "_screenTools");
            Check(tools?.IsVisible == true && tools.IsLoaded, "点击真实翻译/扫码入口后窗口可见且完成加载，无需启用 API");
            Check(tools!.Title == "屏幕工具", "屏幕工具独立窗口使用统一名称");
            tools.WindowState = WindowState.Minimized;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpAnimations(50);
            Check(tools.WindowState != WindowState.Minimized, "窗口最小化后点击翻译/扫码入口能恢复为可操作窗口");
            tools.Left = -32000; tools.Top = -32000;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpAnimations(30);
            Check(tools.Left > -30000 && tools.Top > -30000, "显示器变化或窗口落在屏幕外时恢复到可见工作区");
            tools.Hide(); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpAnimations(30);
            Check(tools.IsVisible && ReferenceEquals(tools, AgentField<ScreenToolsWindow?>(window, "_screenTools")), "隐藏后再次点击能恢复原窗口");
            tools.Close(); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); PumpAnimations(30);
            Check(AgentField<ScreenToolsWindow?>(window, "_screenTools")?.IsVisible == true, "关闭后再次点击能够重新创建窗口");
        }
        finally { AgentField<ScreenToolsWindow?>(window, "_screenTools")?.Close(); }
        PersonalToolsUiChecks(window, root);
    }
}
