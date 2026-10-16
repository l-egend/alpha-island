using System.Text.Json;
using System.IO;
using Size = System.Windows.Size;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Devices.Radios;
using alpha_island.Core;
using alpha_island.Controls;
using alpha_island.Services;
using AlphaIsland.Hardware;
using CheckBox = System.Windows.Controls.CheckBox;
using Grid = System.Windows.Controls.Grid;
using Border = System.Windows.Controls.Border;
using Brushes = System.Windows.Media.Brushes;

internal static partial class Program
{
    private static void StatusDisplayPolicyChecks()
    {
        var options = new IslandOptions().Normalized();
        Check(StatusDisplayOptions.Selected(options).SequenceEqual(StatusDisplayOptions.All), "默认分别展示上传下载与 CPU、内存和硬件功耗");
        SettingsRegistry.SetExtra(options, "StatusDisplayOrder", "power,power,garbage,download,cpu");
        SettingsRegistry.Write(options, "StatusShowMemory", false);
        Check(StatusDisplayOptions.Selected(options).SequenceEqual(new[] { "power", "download", "cpu", "upload" }), "损坏或旧排序补齐已知项，过滤重复与未知项并尊重隐藏设置");
        var roundTrip = JsonSerializer.Deserialize<IslandOptions>(JsonSerializer.Serialize(options))!.Normalized();
        Check(StatusDisplayOptions.Selected(roundTrip).SequenceEqual(StatusDisplayOptions.Selected(options)), "顺序与显示选择随所有配置序列化往返");
        Check(StatusDisplayOptions.Width(5) > StatusDisplayOptions.Width(4) && StatusDisplayOptions.Width(0) > 0, "状态栏宽度随列数调整且空选择不会产生零尺寸");
        Check(new HardwarePowerReading(24.4, 18.1, "").TotalWatts is double total && Math.Abs(total - 42.5) < 0.001, "CPU 封装与独立 GPU 功耗相加");
        Check(new HardwarePowerReading(null, 18.1, "缺少 CPU").TotalWatts == null
            && HardwarePowerPolicy.SumComplete(new double?[] { 18.1, null }) == null, "CPU 或任一独显缺失时不冒充完整总功耗");
        Check(HardwarePowerPolicy.ValidWatts(double.NaN) == null && HardwarePowerPolicy.ValidWatts(-1) == null
            && HardwarePowerPolicy.ValidWatts(double.PositiveInfinity) == null && HardwarePowerPolicy.ValidWatts(0) == 0, "功率未知和非法数值不会显示成零，真实零值有效");
        Check(HardwarePowerPolicy.IsCpuPackageChannel("RAPL_Package0_PKG") && HardwarePowerPolicy.IsCpuPackageChannel("RAPL_Package1_PKG"), "选择完整 CPU 封装通道并支持多个封装");
        Check(!HardwarePowerPolicy.IsCpuPackageChannel("RAPL_Package0_PP0") && !HardwarePowerPolicy.IsCpuPackageChannel("RAPL_Package0_PP1")
            && !HardwarePowerPolicy.IsCpuPackageChannel("RAPL_Package0_DRAM"), "核显、核心和内存子项不重复计入 CPU 封装功耗");
        Check(HardwarePowerPolicy.EnergyWatts(0, 0, 10_000_000_000, 10_000_000) == 36, "EMI 能量差与时间差正确换算瓦数");
        Check(HardwarePowerPolicy.EnergyWatts(100, 100, 90, 200) == null && HardwarePowerPolicy.EnergyWatts(100, 100, 110, 100) == null,
            "能量计重置或时间未推进时重新建立基线");
        Check(new HardwarePowerReading(25, 15, "") { SampledUtc = DateTime.UtcNow.AddSeconds(-20) }.FreshAt(DateTime.UtcNow).TotalWatts == null,
            "超时读数自动失效，避免功耗停留在过期数值");
        var transitions = new WirelessRadioTransitions();
        Check(!transitions.Apply(RadioKind.WiFi, true) && !transitions.Apply(RadioKind.WiFi, true), "首次无线状态与重复读数保持静默");
        Check(transitions.Apply(RadioKind.WiFi, false) && !transitions.Apply(RadioKind.WiFi, false)
            && transitions.Apply(RadioKind.WiFi, true), "无线关闭与开启各提醒一次");
        Check(!transitions.Apply(RadioKind.Bluetooth, false) && transitions.Apply(RadioKind.Bluetooth, true), "蓝牙与 Wi-Fi 基线独立");
        Check(!transitions.Apply(RadioKind.WiFi, null) && !transitions.Apply(RadioKind.WiFi, false), "读取失败、设备消失和恢复不误报开关变化");
        Check(WirelessRadioTransitions.Aggregate(new[] { RadioState.Off, RadioState.On }) == true
            && WirelessRadioTransitions.Aggregate(new[] { RadioState.Disabled, RadioState.Off }) == false
            && WirelessRadioTransitions.Aggregate(new[] { RadioState.Unknown, RadioState.Off }) == null
            && WirelessRadioTransitions.Aggregate(Array.Empty<RadioState>()) == null, "多无线适配器聚合，不将未知或无硬件判为关闭");
        transitions.Reset(); Check(!transitions.Apply(RadioKind.Bluetooth, false), "重启监听重新建立静默基线");
    }
    private static async Task HardwarePowerLiveChecks()
    {
        using var service = new HardwarePowerService();
        await Task.Run(service.Sample); await Task.Delay(1000); await Task.Run(service.Sample);
        var reading = service.Current;
        System.Console.WriteLine("Hardware power live: " + reading.Description);
        Check(reading.TotalWatts == null || (reading.TotalWatts >= 0 && double.IsFinite(reading.TotalWatts.Value)), "真实硬件采样返回有效瓦数或明确的不可用状态");
        Check(reading.TotalWatts.HasValue || !string.IsNullOrWhiteSpace(reading.Reason), "功率无法读取时提供原因");
        Check(reading.Source == "Windows EMI + NVIDIA NVML" && reading.CpuWatts != 0, "原生接口采样拒绝 CPU 初始化假零");
    }
    private static void StatusDisplayUiChecks(alpha_island.MainWindow window, Grid root)
    {
        var options = AgentField<IslandOptions>(window, "_options");
        StatusSplitUiChecks(window, root);
        Directory.CreateDirectory("bin/status-previews");
        void Radios(RadioState wifi, RadioState bluetooth) => PrivateCall(window, "ApplyWirelessRadioStates", new[] { (RadioKind.WiFi, wifi), (RadioKind.Bluetooth, bluetooth) });
        PrivateCall(window, "ClearDesktopInbox"); SetAgentField(window, "_state", IslandState.Card);
        Radios(RadioState.On, RadioState.On);
        var inbox = AgentField<DesktopNotificationInbox>(window, "_desktopInbox");
        Check(inbox.History.Count == 0, "无线监听首轮不弹窗也不制造事件");
        Radios(RadioState.Off, RadioState.Off); Radios(RadioState.Off, RadioState.Off);
        Check(inbox.History.Count == 2 && inbox.PendingCount == 2 && inbox.History.Select(x => x.Kind).Distinct().Count() == 2,
            "Wi-Fi 与蓝牙同时关闭分别记录并排队，重复采样不重复弹窗");
        SetAgentField(window, "_state", IslandState.Bar); PrivateCall(window, "TickDesktopNotifications");
        Check(AgentField<DesktopNotification?>(window, "_desktopCurrent")?.Kind == DesktopEventKind.WifiRadio && inbox.PendingCount == 1
            && AgentField<IslandState>(window, "_state") == IslandState.Pill,
            "无线开关事件显示胶囊而不展开通知中心，后续事件保留在队列");
        var notification = AgentField<DesktopNotificationPill>(window, "_notificationSummary");
        PumpAnimations(400); root.UpdateLayout();
        Check(notification.Title.Text == "Wi-Fi" && notification.Detail.Text == "已关闭" && notification.Icon.ActualWidth > 0
            && notification.Title.TranslatePoint(new System.Windows.Point(), notification).X > notification.IconBadge.ActualWidth,
            "Wi-Fi 关闭提醒实际显示左侧图标与右侧名称、状态");
        SaveThemePreview(window.IslandBody, "bin/status-previews/wifi-off-notification.png");
        SetAgentField(window, "_desktopExpires", DateTimeOffset.Now.AddSeconds(-1)); PrivateCall(window, "TickDesktopNotifications");
        Check(AgentField<DesktopNotification?>(window, "_desktopCurrent")?.Kind == DesktopEventKind.BluetoothRadio
            && notification.Title.Text == "蓝牙" && notification.Detail.Text == "已关闭" && inbox.PendingCount == 0,
            "Wi-Fi 提醒结束后按队列切换蓝牙图标和文字");
        PumpAnimations(400); root.UpdateLayout(); SaveThemePreview(window.IslandBody, "bin/status-previews/bluetooth-off-notification.png");
        SetAgentField(window, "_desktopExpires", DateTimeOffset.Now.AddSeconds(-1)); PrivateCall(window, "TickDesktopNotifications");
        Check(AgentField<DesktopNotification?>(window, "_desktopCurrent") == null && AgentField<IslandState>(window, "_state") == IslandState.Bar,
            "待机无线提醒全部结束后恢复细条");
        PrivateCall(window, "SetLayerPinned", "netspeed", true); PrivateCall(window, "SwitchMainIsland", "netspeed");
        PrivateCall(window, "SetState", IslandState.Pill, true);
        Radios(RadioState.On, RadioState.Off);
        Check(notification.Title.Text == "Wi-Fi" && notification.Detail.Text == "已开启"
            && ((SolidColorBrush)notification.Icon.Foreground).Color == DesktopEventAppearance.For(DesktopEventKind.WifiRadio).Color,
            "开启状态显示主题彩色图标和准确文字");
        PumpAnimations(400); root.UpdateLayout(); SaveThemePreview(window.IslandBody, "bin/status-previews/wifi-on-notification.png");
        SetAgentField(window, "_desktopExpires", DateTimeOffset.Now.AddSeconds(-1)); PrivateCall(window, "TickDesktopNotifications");
        Check(AgentField<string>(window, "_activeMainIslandKey") == "netspeed"
            && AgentField<IslandLayerHost>(window, "_layers").IsPinned("netspeed") && AgentField<IslandState>(window, "_state") == IslandState.Pill,
            "提醒结束恢复原来的置顶岛并保留置顶状态");
        Radios(RadioState.On, RadioState.On); PumpAnimations(400); root.UpdateLayout();
        SaveThemePreview(window.IslandBody, "bin/status-previews/bluetooth-on-notification.png");
        PrivateCall(window, "SwitchMainIsland", "netspeed");
        SetAgentField(window, "_desktopExpires", DateTimeOffset.Now.AddSeconds(-1)); PrivateCall(window, "TickDesktopNotifications");
        Check(AgentField<string>(window, "_activeMainIslandKey") == "netspeed" && AgentField<IslandState>(window, "_state") == IslandState.Pill,
            "用户手动切回其他岛后提醒到期不抢回主岛");
        PrivateCall(window, "SetLayerPinned", "netspeed", false);
        PrivateCall(window, "SetState", IslandState.Card, true);
        Radios(RadioState.Off, RadioState.On);
        Check(AgentField<DesktopNotification?>(window, "_desktopCurrent") == null && inbox.PendingCount == 1
            && AgentField<IslandState>(window, "_state") == IslandState.Card,
            "查看其他卡片时无线提醒排队，不打断当前卡片");
        PrivateCall(window, "SetState", IslandState.Pill, true); PrivateCall(window, "TickDesktopNotifications");
        Check(AgentField<DesktopNotification?>(window, "_desktopCurrent")?.Kind == DesktopEventKind.WifiRadio,
            "离开卡片后显示排队的无线胶囊");
        PrivateCall(window, "ClearDesktopInbox");
        var message = new DesktopNotification("status-message-regression", "Chat!App", "聊天", "新消息", "正文", DateTimeOffset.Now);
        PrivateCall(window, "OnSystemToastsReceived", new[] { message }, false);
        Check(AgentField<IslandState>(window, "_state") == IslandState.Pill
            && AgentField<DesktopNotification?>(window, "_desktopCurrent")?.Key == message.Key
            && notification.Title.Text == message.AppName && notification.Detail.Text == message.PreviewText
            && AgentField<DesktopNotification?>(window, "_desktopCurrent")?.Body == message.Body,
            "应用消息也显示简短小岛并保留通知正文，完整内容可在通知页查看");
        PrivateCall(window, "ClearDesktopInbox");
        SettingsRegistry.Write(options, "NotificationQuietMode", true);
        AgentField<WirelessRadioTransitions>(window, "_radioTransitions").Reset(); Radios(RadioState.Off, RadioState.Off);
        Radios(RadioState.On, RadioState.On);
        Check(inbox.History.Count == 2 && inbox.PendingCount == 0, "无线提醒遵守静默模式并保留通知记录");
        SettingsRegistry.Write(options, "NotificationWifiRadio", false); SettingsRegistry.Write(options, "NotificationBluetoothRadio", false);
        Radios(RadioState.Off, RadioState.Off); Check(inbox.History.Count == 2, "关闭无线提醒设置后不再生成开关通知");
        StatusDisplaySettingsUiChecks();
    }
    private static void StatusDisplaySettingsUiChecks()
    {
        var options = new IslandOptions().Normalized();
        var settings = new alpha_island.Settings.SettingsWindow(persistSettings: false); settings.LoadFrom(options);
        settings.OptionsChanged += (_, updated) => options.CopyFrom(updated);
        var root = (Grid)settings.Content; settings.Content = null; root.Resources = settings.Resources;
        using var source = new HwndSource(new HwndSourceParameters("Status settings regression")
        { Width = 880, Height = 600, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        source.RootVisual = root;
        try
        {
            Check(StatusDisplayOptions.All.All(k => settings.FindName(StatusDisplayOptions.SettingKey(k) + "Toggle") is CheckBox), "设置中可选择全部五个显示项");
            Check(settings.FindName("HardwarePowerStatus") is TextBlock && settings.FindName("HardwarePowerEnableButton") == null
                && settings.FindName("HardwarePowerElevatedReaderToggle") == null, "设置保留实时读取状态并移除驱动安装与管理员授权入口");
            SettingsRegistry.SetExtra(options, "HardwarePowerElevatedReader", true);
            var exported = JsonSerializer.Deserialize<IslandOptions>(JsonSerializer.Serialize(options))!.Normalized();
            Check(!exported.Extras.ContainsKey("HardwarePowerElevatedReader") && SettingsRegistry.GetBool(exported, "StatusShowPower", true),
                "导入旧配置清理管理员读取偏好并保留功耗显示开关");
            string legacyPath = Path.Combine(Path.GetTempPath(), "alpha-island-old-power-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                SettingsRegistry.SetExtra(options, "CustomPluginPreference", "keep-me");
                File.WriteAllText(legacyPath, JsonSerializer.Serialize(options));
                IslandOptions.Load(legacyPath);
                var migrated = JsonSerializer.Deserialize<IslandOptions>(File.ReadAllText(legacyPath))!;
                Check(!migrated.Extras.ContainsKey("HardwarePowerElevatedReader") && migrated.Extras["CustomPluginPreference"].GetString() == "keep-me"
                    && SettingsRegistry.GetBool(migrated, "StatusShowPower", true) && File.Exists(legacyPath + ".bak"),
                    "旧配置加载后原子保存迁移并保留自定义配置和备份");
            }
            finally { File.Delete(legacyPath); File.Delete(legacyPath + ".bak"); }
            settings.UpdateHardwarePowerState(new(25, 15, ""));
            Check(((TextBlock)settings.FindName("HardwarePowerStatus")).Text.Contains("40.0 W"), "设置中的功耗状态随新读数更新");
            string[] networkOrder = StatusDisplayOptions.NetworkSelected(options);
            for (int i = 0; i < 4; i++) PrivateCall(settings, "MoveStatusDisplayItem", "power", -1);
            Check(StatusDisplayOptions.NetworkSelected(options).SequenceEqual(networkOrder), "资源组排序不会移动网速组的项目");
            Check(StatusDisplayOptions.ResourcesSelected(options)[0] == "power", "设置中排序按钮将功耗移到资源组首位并保存顺序");
            ((CheckBox)settings.FindName("StatusShowCpuToggle")).IsChecked = false;
            Check(!StatusDisplayOptions.Selected(options).Contains("cpu") && StatusDisplayOptions.ResourcesSelected(options)[0] == "power", "关闭显示项后保留其余显示順序");
            settings.LoadFrom(options);
            var order = (string[])typeof(alpha_island.Settings.SettingsWindow).GetField("_statusDisplayOrder", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(settings)!;
            Check(order.Where(key => key is "cpu" or "memory" or "power").First() == "power" && ((CheckBox)settings.FindName("StatusShowCpuToggle")).IsChecked == false,
                "重新加载设置恢复显示选择与顺序");
            Check(settings.FindName("NotificationWifiRadioToggle") is CheckBox && settings.FindName("NotificationBluetoothRadioToggle") is CheckBox,
                "Wi-Fi 与蓝牙提醒可分别设置");
            foreach (string page in new[] { "system", "notifications" })
            {
                PrivateCall(settings, "SelectPageByTag", page); root.UpdateLayout(); SaveThemePreview(root, "bin/status-previews/settings-" + page + ".png");
                if (page == "system")
                {
                    ((FrameworkElement)settings.FindName("HardwarePowerStatus")).BringIntoView(); root.UpdateLayout();
                    SaveThemePreview(root, "bin/status-previews/settings-hardware-power.png");
                }
            }
        }
        finally { settings.ForceClose = true; settings.Close(); }
    }
}
