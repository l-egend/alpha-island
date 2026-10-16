using System.Runtime.InteropServices;
using System.Windows.Automation;
using alpha_island.Services;

namespace alpha_island.Interop;

/// <summary>Reads the explicit Windows battery indicator, without opening settings or changing charging limits.</summary>
internal static class WindowsSmartChargingReader
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    internal static SmartChargingState Read()
    {
        var window = FindWindow("Shell_TrayWnd", null);
        if (window == IntPtr.Zero) return SmartChargingState.Unknown;
        try
        {
            var queue = new Queue<(AutomationElement Element, int Depth)>();
            queue.Enqueue((AutomationElement.FromHandle(window), 0));
            var walker = TreeWalker.ControlViewWalker;
            int scheduled = 1;
            while (queue.Count > 0)
            {
                var (element, depth) = queue.Dequeue();
                try
                {
                    var current = element.Current;
                    if (current.AutomationId == "SystemTrayIcon" && current.ClassName == "SystemTray.OmniButtonRight")
                    {
                        var state = ParsePowerIndicator(current.Name);
                        if (state != SmartChargingState.Unknown) return state;
                    }
                    if (depth >= 12) continue;
                    for (var child = walker.GetFirstChild(element); child != null && scheduled < 500;
                        child = walker.GetNextSibling(child))
                    {
                        queue.Enqueue((child, depth + 1));
                        scheduled++;
                    }
                }
                catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
        return SmartChargingState.Unknown;
    }

    internal static SmartChargingState ParsePowerIndicator(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return SmartChargingState.Unknown;
        string text = name.Trim();
        // Only supported languages and the system power control. An app named Battery Flyout is not a source.
        bool powerControl = text.StartsWith("电源", StringComparison.Ordinal)
            || text.StartsWith("電源", StringComparison.Ordinal)
            || text.StartsWith("Power", StringComparison.OrdinalIgnoreCase);
        if (!powerControl) return SmartChargingState.Unknown;
        if (text.Contains("智能充电已关闭", StringComparison.Ordinal)
            || text.Contains("智慧充電已關閉", StringComparison.Ordinal)
            || text.Contains("Smart charging is off", StringComparison.OrdinalIgnoreCase))
            return SmartChargingState.NotActive;
        if (text.Contains("智能充电", StringComparison.Ordinal) || text.Contains("智慧充電", StringComparison.Ordinal)
            || text.Contains("Smart charged", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Smart charging", StringComparison.OrdinalIgnoreCase))
            return SmartChargingState.Active;
        // NotActive describes the current Windows indicator, not whether an OEM care setting is configured.
        if (text.Contains("电池状态", StringComparison.Ordinal) || text.Contains("電池狀態", StringComparison.Ordinal)
            || text.Contains("Battery", StringComparison.OrdinalIgnoreCase)
            || text.Contains("available", StringComparison.OrdinalIgnoreCase)
            || text.Contains("remaining", StringComparison.OrdinalIgnoreCase))
            return SmartChargingState.NotActive;
        return SmartChargingState.Unknown;
    }
}
