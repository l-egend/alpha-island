using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using alpha_island.Core;
using alpha_island.Services;
using Size = System.Windows.Size;
using Point = System.Windows.Point;

internal static partial class Program
{
    private static void CompactStatusAlertChecks(alpha_island.MainWindow window, Grid root)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        var layers = AgentField<IslandLayerHost>(window, "_layers");
        SettingsRegistry.Write(options, "NetSpeedNotifyEnabled", true);
        SettingsRegistry.Write(options, "NetSpeedThresholdMbps", 10d);
        SettingsRegistry.Write(options, "MemoryMonitorEnabled", true);
        SettingsRegistry.Write(options, "MemoryNotifyPercent", 90);
        const double mb = 1024d * 1024;
        void Reset()
        {
            layers.Hide("system"); layers.Hide("netspeed");
            SetAgentField(window, "_netSpeedFastTicks", 0);
            SetAgentField(window, "_netSpeedNextAlertUtc", DateTime.MinValue);
            SetAgentField(window, "_memHighTicks", 0);
            SetAgentField(window, "_memNextAlertUtc", DateTime.MinValue);
            PrivateCall(window, "SetState", IslandState.Bar, true);
        }
        void Network(NetworkSpeedSample sample)
        {
            for (int i = 0; i < 3; i++)
            {
                PrivateCall(window, "MaybeAlertHighSpeed", sample);
                PrivateCall(window, "UpdateNetSpeedSample", sample);
            }
            PumpAnimations(400); root.UpdateLayout();
        }
        void Preview(string name)
        {
            var visual = new DrawingVisual();
            using (var draw = visual.RenderOpen())
            {
                draw.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(20, 25, 40)),
                    new Pen(new SolidColorBrush(Color.FromArgb(100, 160, 180, 210)), 1), new Rect(1, 1, 278, 46), 23, 23);
                draw.PushTransform(new TranslateTransform(8, 8));
                draw.DrawRectangle(new VisualBrush(window.SystemNotifyLayer), null, new Rect(0, 0, 264, 32));
                draw.Pop();
            }
            var bitmap = new RenderTargetBitmap(280, 48, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory("bin/status-alert-previews");
            using var file = File.Create("bin/status-alert-previews/" + name + ".png"); png.Save(file);
        }

        Reset();
        PrivateCall(window, "UpdateNetSpeedSample", new NetworkSpeedSample(15 * mb, 2 * mb));
        Check(!layers.IsActive("netspeed"), "速率超过阈值的单次采样不展开完整状态栏");
        Network(new(15 * mb, 2 * mb));
        Check(layers.IsActive("system") && !layers.IsActive("netspeed"), "网速异常只显示独立简短提醒");
        Check(window.SysNotifyTitle.Text.Contains("下载") && !window.SysNotifyTitle.Text.Contains("上传")
            && window.SysNotifyValue.Text.Contains("15") && !window.SysNotifyValue.Text.Contains("2"), "下载异常只显示下载与其速率");
        Check(window.SystemNotifyLayer.Visibility == Visibility.Visible && window.NetSpeedNotifyLayer.Visibility == Visibility.Collapsed
            && Math.Abs(window.IslandBody.Width - 280) < 1, "简短提醒宽度为280且完整状态栏隐藏");
        Check(layers.Peek("system")?.Title == window.SysNotifyTitle.Text && layers.Peek("system")?.Value == window.SysNotifyValue.Text,
            "旁路小岛使用同一异常标题和数值");
        Preview("download");
        PrivateCall(window, "UpdateNetSpeedSample", new NetworkSpeedSample(18 * mb, 3 * mb));
        Check(!layers.IsActive("netspeed") && window.SysNotifyTitle.Text.Contains("下载"), "持续高速采样不重新打开完整状态栏");

        Reset(); Network(new(1 * mb, 12 * mb));
        Check(window.SysNotifyTitle.Text.Contains("上传") && !window.SysNotifyTitle.Text.Contains("下载")
            && window.SysNotifyValue.Text.Contains("12"), "上传异常只显示上传与其速率");
        Preview("upload");
        Reset(); Network(new(15 * mb, 12 * mb));
        Check(window.SysNotifyTitle.Text.Contains("上传") && window.SysNotifyTitle.Text.Contains("下载")
            && window.SysNotifyValue.Text.Contains("15") && window.SysNotifyValue.Text.Contains("12"), "两方向同时异常时同一提醒显示两项");
        Preview("both");
        var cooldown = AgentField<DateTime>(window, "_netSpeedNextAlertUtc");
        Network(new(25 * mb, 25 * mb));
        Check(AgentField<DateTime>(window, "_netSpeedNextAlertUtc") == cooldown && !window.SysNotifyValue.Text.Contains("25"),
            "持续异常遵守原有冷却且不反复改写弹窗");

        Reset();
        var memory = new MemorySample { Valid = true, TotalBytes = 16UL << 30, AvailableBytes = 1UL << 30, UsedPercent = 93 };
        for (int i = 0; i < 3; i++) PrivateCall(window, "MaybeAlertHighMemory", memory);
        PumpAnimations(400); root.UpdateLayout();
        Check(window.SysNotifyTitle.Text == "内存占用偏高" && window.SysNotifyValue.Text == "93%"
            && window.SysNotifyDetail.Text.Contains("90"), "内存异常只显示占用、提醒及阈值");
        Check(!layers.IsActive("netspeed") && Math.Abs(window.IslandBody.Width - 280) < 1, "内存提醒也采用小尺寸且不展开状态栏");
        Preview("memory");
        PrivateCall(window, "SetState", IslandState.Card, true, IslandPageIds.Volume);
        PrivateCall(window, "UpdateSideBubbleHint");
        Check(window.SideBubbleCustomContent.Visibility == Visibility.Collapsed && window.SideBubbleText.Text == "内存占用偏高"
            && window.SideBubbleDetail.Text.Contains("93%"), "卡片态仅旁路显示对应内存异常，不复制状态栏");

        Reset();
        window.ShowSystemNotify("\uE701", "Wi-Fi 已开启", "无线网络可用", "");
        PumpAnimations(400);
        Check(Math.Abs(window.IslandBody.Width - IslandMetrics.PillWidth) < 1, "普通系统通知沿用原有尺寸");
        for (int i = 0; i < 3; i++) PrivateCall(window, "MaybeAlertHighMemory", memory);
        PumpAnimations(400);
        Check(Math.Abs(window.IslandBody.Width - 280) < 1, "同一系统提示从常规尺寸切为紧凑尺寸也会更新形态");
        window.ShowSystemNotify("\uE701", "Wi-Fi 已关闭", "无线网络已关闭", "");
        PumpAnimations(400);
        Check(Math.Abs(window.IslandBody.Width - IslandMetrics.PillWidth) < 1, "下一条普通系统通知恢复原有尺寸");
        Reset();
        layers.SetPinned("netspeed", true); PrivateCall(window, "SwitchMainIsland", "netspeed");
        Network(new(15 * mb, 2 * mb));
        Check(layers.IsPinned("netspeed") && window.SysNotifyTitle.Text.Contains("下载"), "简短提醒不取消状态栏顶置");
        layers.Hide("system"); PrivateCall(window, "SyncNotificationLayers"); PumpAnimations(400);
        Check(layers.IsPinned("netspeed") && window.NetSpeedNotifyLayer.Visibility == Visibility.Visible, "提醒结束后已顶置状态栏仍可见");
        layers.SetPinned("netspeed", false);

        Reset(); SettingsRegistry.Write(options, "NetSpeedNotifyEnabled", false);
        Network(new(15 * mb, 2 * mb));
        Check(!layers.IsActive("system") && !layers.IsActive("netspeed"), "关闭网速提醒后不触发简短或完整弹窗");
        SettingsRegistry.Write(options, "NetSpeedNotifyEnabled", true);
    }
}
