using System.Windows;
using System.Windows.Controls;
using AlphaIsland.PluginSdk;
using alpha_island.Controls;
using alpha_island.Core;

internal static partial class Program
{
    private static void DeviceBatteryPriorityAndLayoutChecks(Grid root)
    {
        var now = DateTimeOffset.UtcNow;
        var unknown = new DeviceBatterySample("fixture", new("receiver", "A 接收器 · 未唤醒", DeviceBatteryTransport.WirelessReceiver, DeviceBatteryKind.Mouse, null, true), now);
        var known = new DeviceBatterySample("fixture", new("online", "Z 在线鼠标", DeviceBatteryTransport.WirelessReceiver, DeviceBatteryKind.Mouse, 78, true), now);
        var groups = DeviceBatteryCatalog.Group([unknown, known], now);
        var failures = new List<Exception>();
        void Case(Action action) { try { action(); } catch (Exception error) { failures.Add(error); Console.WriteLine("FAIL: " + error.Message); } }
        Case(() => Check(groups[0].Display.Device.Id == "online", "在线并能上报电量的设备排在仅接收器在线、未上报的设备之前"));
        var card = new DeviceBatteryCard { Width = 414, Height = 300, Visibility = Visibility.Visible };
        root.Children.Add(card);
        try
        {
            card.UpdateGroups(groups); root.UpdateLayout();
            var icons = Descendants<DeviceBatteryIndicator>(card).ToArray();
            Case(() => Check(icons.Length == 2 && Math.Abs(icons[0].TransformToAncestor(card).Transform(new System.Windows.Point()).X
                - icons[1].TransformToAncestor(card).Transform(new System.Windows.Point()).X) < .1, "百分比和未上报读数共用固定宽度，电池图形纵向对齐"));
            var missing = Descendants<System.Windows.Controls.TextBlock>(card).Single(text => text.Text == "未上报");
            Case(() => Check(missing.FontSize <= 10.5 && missing.TextAlignment == TextAlignment.Right, "未上报使用较小字体并靠右显示，不挤动图标"));
            var refreshed = DeviceBatteryCatalog.Group([unknown with { Device = unknown.Device with { Percent = 65 }, UpdatedAt = now.AddSeconds(1) }, known with { Device = known.Device with { Percent = null } }], now.AddSeconds(1));
            card.UpdateGroups(refreshed); root.UpdateLayout();
            Case(() => Check(refreshed[0].Display.Device.Id == "receiver", "设备唤醒或失去读数后即时重新排序"));
            SaveReminderPreview(card, "bin/GamingFix128/battery-alignment.png", 414, 300);
        }
        finally { root.Children.Remove(card); }
        if (failures.Count > 0) throw new AggregateException(failures);
    }
}
