using System.Windows;
using System.Windows.Controls;
using alpha_island.Controls;
using alpha_island.Core;

internal static partial class Program
{
    private static void SemanticNotificationUiChecks(alpha_island.MainWindow window, Grid root)
    {
        var pill = new DesktopNotificationPill();
        var radio = new DesktopNotification("semantic-radio", "", "Windows", "Wi-Fi 已开启", "无线网络状态", DateTimeOffset.Now,
            DesktopEventKind.WifiRadio, RadioEnabled: true);
        pill.SetNotification(radio);
        Check(pill.Icon.Shape == IconShape.Wifi && pill.Icon.Active && pill.Icon.Source == null,
            "无线通知使用明确的 Wi-Fi 内部笔划与开启状态");
        pill.SetNotification(radio with { RadioEnabled = false });
        Check(pill.Icon.Shape == IconShape.Wifi && !pill.Icon.Active && pill.Detail.Text == "已关闭",
            "Wi-Fi 关闭状态同时传入语义图标与提示文字");
        pill.SetNotification(radio with { Kind = DesktopEventKind.BluetoothRadio, RadioEnabled = false });
        Check(pill.Icon.Shape == IconShape.Bluetooth && !pill.Icon.Active && pill.Title.Text == "蓝牙",
            "蓝牙通知使用连接笔划和真实开关状态");

        var power = radio with { Kind = DesktopEventKind.PowerConnected, RadioEnabled = null, Body = "当前电量 56%" };
        pill.SetNotification(power);
        Check(pill.Icon.Shape == IconShape.Battery && pill.Icon.Active && Math.Abs(pill.Icon.Level - .56) < .0001,
            "接电事件的电池图标显示真实电量并带充电状态");
        pill.SetNotification(power with { Kind = DesktopEventKind.PowerDisconnected, Body = "当前电量 37%" });
        Check(!pill.Icon.Active && Math.Abs(pill.Icon.Level - .37) < .0001,
            "拔出电源取消电池充电状态并保留实际比例");
        pill.SetNotification(power with { Kind = DesktopEventKind.CriticalBattery, Body = "当前电量 3%" });
        Check(!pill.Icon.Active && Math.Abs(pill.Icon.Level - .03) < .0001,
            "严重低电量使用事件中的实际电量，不设置假定比例");
        pill.SetNotification(power with { Kind = DesktopEventKind.Volume, Title = "系统音量已静音", Body = "当前音量 0%" });
        Check(pill.Icon.Shape == IconShape.Volume && !pill.Icon.Active && pill.Icon.Level == 0,
            "静音事件的喇叭内部状态与数值一致");
        pill.SetNotification(power with { Kind = DesktopEventKind.BootTiming });
        Check(pill.Icon.Shape == IconShape.Timer, "启动耗时通知使用时钟语义图标");

        var message = new DesktopNotification("semantic-logo", "SemanticIcon.Test", "图标测试", "保留应用图标", "通知内容", DateTimeOffset.Now,
            Logo: IconMotionTestLogo());
        pill.SetNotification(message);
        Check(pill.Icon.Shape == IconShape.Notification && pill.Icon.Source != null
            && ReferenceEquals(pill.Icon.Source, pill.IconImage.Source)
            && pill.Icon.Visibility == Visibility.Visible && pill.IconImage.Visibility == Visibility.Collapsed,
            "应用 Logo 由语义槽绘制且保留原图片数据，避免叠加第二个展示图标");
        pill.SetNotification(radio);
        Check(pill.Icon.Source == null && pill.IconImage.Source == null, "应用通知切换为无线事件时清除上一应用 Logo");

        foreach (var (key, shape) in new[]
        {
            ("agentstatus", IconShape.Agent), ("card/focus", IconShape.Timer),
            ("volume", IconShape.Volume), ("battery", IconShape.Battery),
            ("clipboard", IconShape.Clipboard), ("download", IconShape.Download),
            ("approval", IconShape.Approval), ("bluetooth", window.BtDeviceIcon.Shape),
        })
        {
            var orb = (Border)PrivateCall(window, "CreateSatelliteOrb", key)!;
            var icon = ((Grid)orb.Child).Children.OfType<SemanticIcon>().Single(i => i.Name == "OrbGlyph");
            Check(icon.Shape == shape && icon.Width == 26 && icon.Height == 26,
                key + " 卫星使用固定占位的语义图标");
            if (key == "agentstatus") Check(icon.Active == AgentStatus.IsBusy, "Agent 卫星内部状态与实际忙碌状态一致");
            if (key == "card/focus") Check(((Grid)orb.Child).Children.OfType<TextBlock>().Any(t => t.Name == "OrbTime"),
                "番茄钟时间保持普通文本，不随语义图标描画变化");
        }
    }
}
