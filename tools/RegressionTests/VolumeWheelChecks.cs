using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using alpha_island.Core;
using alpha_island.Services;

internal static partial class Program
{
    private static void VolumeWheelUiChecks(alpha_island.MainWindow window, Grid root)
    {
        var oldService = AgentField<VolumeService?>(window, "_volumeService");
        var pages = AgentField<IslandPageHost>(window, "_pages");
        var oldState = AgentField<IslandState>(window, "_state");
        string oldPage = pages.ActiveId;
        using var service = new VolumeService(window.Dispatcher); // Unstarted; real audio stays untouched.
        SetAgentField(service, "_sessionCache", Enumerable.Range(1, 8).Select(index =>
            new AudioAppSessionViewModel { ProcessId = 100 + index, Name = "滚轮测试应用 " + index }).ToList());
        void Wheel(UIElement target, int delta)
        {
            var e = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            target.RaiseEvent(e);
            if (!e.Handled) { e.RoutedEvent = Mouse.MouseWheelEvent; target.RaiseEvent(e); }
            PumpAnimations(70); root.UpdateLayout();
        }
        try
        {
            SetAgentField(window, "_volumeService", service); service.SetVolume(55);
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.Volume);
            PrivateCall(window, "RefreshVolumeAppSessionsList");
            PrivateCall(window, "VolumeMixerHeader_MouseLeftButtonDown", window.VolumeMixerHeader,
                new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            root.UpdateLayout(); PumpAnimations(350); root.UpdateLayout();
            var scroll = Descendants<ScrollViewer>(window.VolumeMixerPanel).Single();
            var rowText = Descendants<TextBlock>(window.VolumeAppSessionsList).First(text => text.Text == "滚轮测试应用 1");
            Check(scroll.ScrollableHeight > 100 && pages.ActiveId == IslandPageIds.Volume,
                "音量展开列表存在可滚动内容，复现使用实际卡片事件路由");
            Wheel(rowText, -120);
            Console.WriteLine($"Wheel over app row: page={pages.ActiveId}, scroll={scroll.VerticalOffset:F1}, master volume={service.CurrentPercent}");
            Check(pages.ActiveId == IslandPageIds.Volume && scroll.VerticalOffset > 0 && service.CurrentPercent == 55,
                "应用行上的滚轮向下滚动展开列表，不切换卡片或改变主音量");
            Wheel(rowText, 120);
            Check(pages.ActiveId == IslandPageIds.Volume && scroll.VerticalOffset == 0, "向上滚动返回列表顶部，仍留在音量卡片");
            Wheel(rowText, 120);
            Check(pages.ActiveId == IslandPageIds.Volume && service.CurrentPercent == 55, "应用列表到达顶部后不会误翻到上一张卡片");
            scroll.ScrollToEnd(); PumpAnimations(30); Wheel(rowText, -120);
            Check(pages.ActiveId == IslandPageIds.Volume && scroll.VerticalOffset == scroll.ScrollableHeight,
                "应用列表到达底部后不会误翻到下一张卡片");
            scroll.ScrollToHome(); PumpAnimations(30);
            var slider = Descendants<Slider>(window.VolumeAppSessionsList).First();
            slider.Value = 40; double appVolume = slider.Value; Wheel(slider, 120);
            Check(slider.Value == appVolume + 2 && service.CurrentPercent == 55 && pages.ActiveId == IslandPageIds.Volume
                && scroll.VerticalOffset == 0, "滚轮只在应用音量条上调节对应应用，不滚动列表或改变主音量");
            Wheel(window.CardVolumeSliderTrack, 120);
            Check(service.CurrentPercent == 57 && pages.ActiveId == IslandPageIds.Volume, "主音量条区域仍支持滚轮调节音量");
            Wheel(window.CardVolumePercentText, -120);
            Check(service.CurrentPercent == 57, "音量百分比等正文区域的滚轮不再调节主音量");
            PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.Volume);
            SetAgentField(service, "_deviceCache", Enumerable.Range(1, 8).Select(index =>
                new AudioDeviceInfo("wheel-device-" + index, "滚轮输出设备 " + index, index == 1)).ToList());
            PrivateCall(window, "AudioDeviceHeader_MouseLeftButtonDown", window.AudioDeviceHeader,
                new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            root.UpdateLayout(); PumpAnimations(350); root.UpdateLayout();
            var deviceText = Descendants<TextBlock>(window.AudioDeviceList).First(text => text.Text == "滚轮输出设备 1");
            var deviceScroll = Descendants<ScrollViewer>(window.AudioDevicePanel).Single();
            Check(deviceScroll.ScrollableHeight > 0, "输出设备展开列表也存在可滚动内容");
            Wheel(deviceText, -120);
            Check(deviceScroll.VerticalOffset > 0 && pages.ActiveId == IslandPageIds.Volume && service.CurrentPercent == 57,
                "输出设备列表优先滚动，不切换卡片或改变系统音量");
            deviceScroll.ScrollToEnd(); PumpAnimations(30); Wheel(deviceScroll, -120);
            Check(pages.ActiveId == IslandPageIds.Volume, "滚轮落在输出设备滚动条区域或底部边界时仍保留当前卡片");
        }
        finally
        {
            PrivateCall(window, "ResetCardDrawers"); SetAgentField(window, "_volumeService", oldService);
            PrivateCall(window, "SetState", oldState, true, oldPage);
        }
    }
}
