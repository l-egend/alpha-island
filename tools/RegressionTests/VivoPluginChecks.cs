using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AlphaIsland.Plugins.VivoOffice;
using alpha_island.Core;
using alpha_island.Plugins;
using alpha_island.Settings;
using alpha_island.Services;
using CheckBox = System.Windows.Controls.CheckBox;

internal static partial class Program
{
    private static void VivoPluginReaderChecks(string root)
    {
        string directory = Path.Combine(root, "vivo-reader"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "historyPhone.json"), config = Path.Combine(directory, "config.json");
        bool running = true;
        var reader = new VivoConnectionReader(path, config, () => running);
        Check(reader.Read().DeviceName == "" && !reader.Read().IsCurrent, "未安装或没有连接记录时显示空态");
        var stamp = DateTimeOffset.Parse("2026-10-04T09:30:00Z");
        var recent = new Dictionary<string, object?> { ["mobileDeviceName"] = "手机 A", ["device_id"] = "a",
            ["connectFlag"] = "WLAN", ["lastSuccessTime"] = stamp.ToUnixTimeMilliseconds(),
            ["ip"] = "never-expose-address", ["vivoAccount"] = "never-expose-account", ["totalStorage"] = 512 };
        void Write(object? live = null) => File.WriteAllText(path, JsonSerializer.Serialize(new { phone = live ?? Array.Empty<object>(), last_time_phone = recent }));
        Write(); var sample = reader.Read();
        Check(sample.DeviceName == "手机 A" && sample.ConnectionMethod == "Wi-Fi" && sample.ConnectedAt == stamp,
            "读取设备名称、连接方式和成功连接时间三个字段");
        Check(!sample.IsCurrent && sample.Summary.StartsWith("最近"), "空连接列表不会因历史时间较新被误判为在线");
        Check(sample.DisplayTime == stamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), "连接时间转换为本机时间并显示完整日期");
        Check(!sample.Summary.Contains("never-expose") && !typeof(VivoConnection).GetProperties().Any(p => p.Name is "IpAddress" or "Account" or "Storage"),
            "插件快照与小岛摘要没有账号、IP 或存储字段");
        Write(new[] { new { mobileDeviceName = "手机 B", device_id = "b", connectFlag = "USB", lastSuccessTime = stamp.AddMinutes(1).ToUnixTimeMilliseconds() } });
        sample = reader.Read();
        Check(sample.IsCurrent && sample.DeviceName == "手机 B" && sample.ConnectionMethod == "USB", "当前连接设备优先于历史设备");
        Write(new[] { new { mobileDeviceName = "手机 B", device_id = "b" } }); sample = reader.Read();
        Check(sample.ConnectionMethod == "" && sample.ConnectedAt == null, "不同设备缺失的方式与时间不会混入历史设备数据");
        Write(new[] { new { mobileDeviceName = "手机 A", device_id = "a" } }); sample = reader.Read();
        Check(sample.IsCurrent && sample.ConnectedAt == stamp && sample.ConnectionMethod == "Wi-Fi", "同一设备的精简连接条目可补全三个字段");
        running = false; sample = reader.Read();
        Check(!sample.IsCurrent && sample.DeviceName == "手机 A", "套件退出后停止在线断言并显示最近设备"); running = true;
        Write(); File.WriteAllText(config, "{\"getConnectedDeviceIdList\":[\"a\"]}");
        Check(reader.Read().IsCurrent, "当前 ID 列表确认刚连接但 phone 数组尚为空的设备");
        File.WriteAllText(config, "{\"getConnectedDeviceInfoList\":[{\"deviceId\":\"a\"}]}");
        Check(reader.Read().IsCurrent, "当前设备信息列表可确认对应设备");
        File.WriteAllText(config, "{\"getConnectedDeviceIdList\":[\"b\"]}");
        Check(!reader.Read().IsCurrent, "其他设备的连接列表不能把历史手机标成在线");
        File.WriteAllText(config, "{partial"); Check(!reader.Read().IsCurrent, "配置重写不完整时保守显示最近设备");
        File.Delete(config);
        File.WriteAllText(path, "{partial"); sample = reader.Read();
        Check(sample.DeviceName == "手机 A" && !sample.IsCurrent && sample.Notice.Length > 0, "记录写入中保留上次字段且撤掉在线标记");
        var writeTime = File.GetLastWriteTimeUtc(path); recent["mobileDeviceName"] = "手机 C"; Write(); File.SetLastWriteTimeUtc(path, writeTime);
        Check(reader.Read().DeviceName == "手机 C", "失败后相同修改时间的修复记录也能恢复更新");
        recent["lastSuccessTime"] = long.MaxValue; Write();
        Check(reader.Read().ConnectedAt == null && reader.Read().DisplayTime == "—", "非法连接时间显示未知并保持插件可用");
        recent.Remove("lastSuccessTime"); recent.Remove("connectFlag"); Write();
        Check(reader.Read().ConnectionMethod == "" && reader.Read().ConnectedAt == null, "缺失方式与时间不推算或填入假值");
        recent["connectFlag"] = "WLAN"; recent["otherConnectType"] = "FARAWAYWLAN"; Write();
        Check(reader.Read().ConnectionMethod == "远程连接", "明确的远程连接类型优先于底层 WLAN 标志");
        recent.Remove("otherConnectType"); recent["connectFlag"] = "P2P"; Write();
        Check(reader.Read().ConnectionMethod == "Wi-Fi 直连", "直连方式显示易读名称");
        recent["lastSuccessTime"] = stamp.ToUnixTimeMilliseconds().ToString(); Write();
        Check(reader.Read().ConnectedAt == stamp, "字符串格式时间戳正确解析");
        File.Delete(path); Check(reader.Read().DeviceName == "", "记录文件删除后清除旧字段");
        VivoConnectionEventChecks();
    }

    private static void VivoConnectionEventChecks()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new VivoConnection("手机 A", "Wi-Fi", now.AddHours(-1));
        var events = new VivoConnectionEvents();
        Check(!events.Observe(cache, now), "插件启动只记录连接基线，不补弹旧历史");
        var fresh = cache with { ConnectedAt = now };
        Check(events.Observe(fresh, now), "最新成功连接时间变化即使没有当前设备列表也会发出提醒");
        Check(!events.Observe(fresh, now.AddSeconds(2)), "两秒轮询同一连接记录不会重复提醒");
        var online = fresh with { IsCurrent = true };
        // 时间写入和当前列表写入可能错开，后续在线确认不应重复报同一次连接。
        Check(!events.Observe(online, now.AddSeconds(4)), "同一连接的稍后在线确认不重复弹出");
        Check(!events.Observe(online with { IsCurrent = false, Notice = "连接状态更新中" }, now.AddSeconds(6))
            && !events.Observe(online, now.AddSeconds(8)), "配置写入中的短暂未知不会制造断开再连接提醒");
        Check(!events.Observe(online with { IsCurrent = false }, now.AddSeconds(10))
            && events.Observe(online, now.AddSeconds(12)), "明确断开后同一设备再连接可以重新提醒");
        Check(events.Observe(online with { DeviceName = "手机 B" }, now.AddSeconds(14)), "当前连接设备切换到另一台手机时提醒");
        Check(!events.Observe(cache with { ConnectedAt = now.AddMinutes(-30) }, now.AddSeconds(16)), "旧历史时间的更新不会当作刚连接");
        var empty = new VivoConnectionEvents();
        Check(!empty.Observe(new(Notice: "暂无连接记录"), now)
            && empty.Observe(fresh, now.AddSeconds(2)), "没有历史记录时第一次新连接也会提醒");
        var startupOnline = new VivoConnectionEvents();
        Check(!startupOnline.Observe(online, now) && !startupOnline.Observe(online, now.AddSeconds(2)),
            "启用插件时已经在线的设备不反复补弹");
    }

    private static void VivoPluginUiChecks(alpha_island.MainWindow window, Grid root, string testRoot)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var pages = (IslandPageHost)typeof(alpha_island.MainWindow).GetField("_pages", flags)!.GetValue(window)!;
        var layers = (IslandLayerHost)typeof(alpha_island.MainWindow).GetField("_layers", flags)!.GetValue(window)!;
        Check(pages.Pages.All(p => p.Id != "link" && p.Title != "应用联动") && !layers.IsRegistered("vivo"), "旧应用联动卡片与内置提示层已移除");
        Check(!SettingsRegistry.All.Any(s => s.Key == "VivoLinkEnabled") && !IslandServiceIds.All.Contains("vivolink"), "旧 vivo 开关和内置监控已移除");
        string pluginRoot = Path.Combine(testRoot, "vivo-plugins"), folder = Path.Combine(pluginRoot, "vivo.office"); Directory.CreateDirectory(folder);
        string source = Path.GetFullPath("examples/VivoOfficePlugin/bin/Debug/net8.0-windows");
        foreach (string name in new[] { "AlphaIsland.Plugins.VivoOffice.dll", "AlphaIsland.Plugins.VivoOffice.deps.json", "plugin.json" })
            File.Copy(name.EndsWith(".dll") ? typeof(VivoOfficePlugin).Assembly.Location
                : name == "plugin.json" ? Path.GetFullPath("examples/VivoOfficePlugin/plugin.json") : Path.Combine(source, name),
                Path.Combine(folder, name), true);
        string history = Path.Combine(testRoot, "vivo-ui-history.json");
        File.WriteAllText(history, "{\"phone\":[],\"last_time_phone\":{\"mobileDeviceName\":\"测试手机\",\"connectFlag\":\"WLAN\",\"lastSuccessTime\":1791106200000}}");
        var previousPath = Environment.GetEnvironmentVariable("ALPHA_ISLAND_VIVO_PATH");
        Environment.SetEnvironmentVariable("ALPHA_ISLAND_VIVO_PATH", history);
        var options = new IslandOptions(); SettingsRegistry.SetExtra(options, PluginHost.EnabledKey("vivo.office"), true);
        var oldHost = (PluginHost?)typeof(alpha_island.MainWindow).GetField("_plugins", flags)!.GetValue(window); oldHost?.Dispose();
        using var host = new PluginHost(window.Dispatcher, options, new[] { pluginRoot },
            page => { window.CardViewport.Children.Add(page.View); pages.Register(page); },
            page => { PrivateCall(window, "RemoveCardPinLayer", page.Id); pages.Unregister(page.Id); window.CardViewport.Children.Remove(page.View); },
            id => PrivateCall(window, "SelectComponent", id));
        int notices = 0;
        host.PageNotificationRequested += (id, title, detail, seconds) =>
        { notices++; PrivateCall(window, "ShowPluginPageNotification", id, title, detail, seconds); };
        typeof(alpha_island.MainWindow).GetField("_plugins", flags)!.SetValue(window, host);
        SettingsWindow? settings = null;
        try
        {
            host.Discover(); var entry = host.Entries.Single();
            Check(entry.Running && entry.Error.Length == 0 && entry.Pages.Count == 1, "独立 vivo DLL 能通过插件宿主启动");
            Check(notices == 0, "加载 vivo 插件和首次创建卡片不补弹历史连接");
            var page = entry.Pages.Single(); string key = "card/" + page.Id;
            Check(entry.Context!.Settings.Count == 0 && entry.Context.Services.Count == 1, "插件仅注册连接读取服务，没有额外数据功能开关");
            PrivateCall(window, "SelectComponent", page.Id); PrivateCall(window, "SetState", IslandState.Card); root.UpdateLayout();
            var values = Descendants<TextBlock>(page.View).Where(t => t.Name.StartsWith("Vivo")).ToArray();
            Check(values.Length == 3 && values.Single(t => t.Name == "VivoDeviceName").Text == "测试手机"
                && values.Single(t => t.Name == "VivoConnectionMethod").Text == "Wi-Fi"
                && values.Single(t => t.Name == "VivoConnectionTime").Text.Contains("2026-10-04"), "卡片只显示名称、连接方式和连接时间");
            Check(Descendants<TextBlock>(page.View).Any(t => t.Text == "最近设备"), "历史卡片明确标明最近设备");
            SaveThemePreview((FrameworkElement)page.View, "docs/screens/vivo-plugin.png");
            PrivateCall(window, "CollapseToBar", true);
            File.WriteAllText(history, JsonSerializer.Serialize(new { phone = Array.Empty<object>(),
                last_time_phone = new { mobileDeviceName = "测试手机", connectFlag = "WLAN", lastSuccessTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } }));
            entry.Instance!.GetType().GetMethod("Refresh", flags)!.Invoke(entry.Instance, null); root.UpdateLayout();
            Check(notices == 1 && AgentField<IslandState>(window, "_state") == IslandState.Pill
                && !layers.IsPinned(key) && layers.IsVisible(key), "未置顶的手机插件在细条状态也能主动弹出新连接提醒");
            PrivateCall(window, "SelectComponent", page.Id);
            PrivateCall(window, "SetLayerPinned", "download", true, false);
            PrivateCall(window, "ToggleCardPin"); root.UpdateLayout();
            Check(layers.IsPinned(key) && layers.IsPinned("download"), "vivo 插件可以独立顶置并保留其他模块");
            var pill = (Grid)PrivateCall(window, "GetNotifyLayerElement", key)!;
            Check(Descendants<TextBlock>(pill).Any(t => t.Text.Contains("测试手机") && t.Text.Contains("Wi-Fi")), "顶置小岛显示插件的连接摘要");
            var pinView = AgentField<System.Collections.IDictionary>(window, "_cardPinViews")[key]!;
            pinView.GetType().GetField("NoticeUntilUtc", flags)!.SetValue(pinView, DateTime.UtcNow.AddSeconds(-1));
            PrivateCall(window, "SyncNotificationLayers");
            Check(Descendants<TextBlock>(pill).Any(t => t.Text == "vivo 手机"), "临时连接提醒到期后置顶小岛恢复实时卡片摘要");
            File.WriteAllText(history, "{\"phone\":[],\"last_time_phone\":{\"mobileDeviceName\":\"另一台手机\",\"connectFlag\":\"USB\",\"lastSuccessTime\":1791106260000}}");
            entry.Instance!.GetType().GetMethod("Refresh", flags)!.Invoke(entry.Instance, null);
            PrivateCall(window, "SyncNotificationLayers");
            Check(Descendants<TextBlock>(pill).Any(t => t.Text.Contains("另一台手机") && t.Text.Contains("USB")), "设备变化同步刷新顶置小岛摘要");
            var connectedAt = DateTimeOffset.UtcNow;
            int previousNotices = notices;
            File.WriteAllText(history, JsonSerializer.Serialize(new { phone = Array.Empty<object>(),
                last_time_phone = new { mobileDeviceName = "另一台手机", connectFlag = "USB", lastSuccessTime = connectedAt.ToUnixTimeMilliseconds() } }));
            entry.Instance!.GetType().GetMethod("Refresh", flags)!.Invoke(entry.Instance, null);
            PrivateCall(window, "SyncNotificationLayers"); root.UpdateLayout();
            Check(notices == previousNotices + 1 && layers.IsVisible(key) && layers.Peek(key)?.Title == "手机连接成功"
                && Descendants<TextBlock>(pill).Any(t => t.Text == "手机连接成功"), "连接时间变化从插件发出提醒并显示在主岛，周期刷新不会抹掉标题");
            Check(Descendants<TextBlock>(pill).Any(t => t.Text.Contains("另一台手机") && t.Text.Contains("USB")
                && t.Text.Contains(connectedAt.ToLocalTime().ToString("yyyy-MM-dd"))), "连接提醒只包含设备名称、连接方式和连接时间");
            entry.Instance!.GetType().GetMethod("Refresh", flags)!.Invoke(entry.Instance, null);
            Check(notices == previousNotices + 1, "相同连接记录的持续巡检不重复弹出");
            var notifyContext = entry.Context!;
            bool unknownPage = false;
            try { notifyContext.NotifyPage("approval", "非法跨模块提醒", ""); } catch (ArgumentException) { unknownPage = true; }
            Check(unknownPage && notices == previousNotices + 1, "插件提醒只能使用本插件注册的卡片，不能占用审批模块");
            bool badDuration = false;
            try { notifyContext.NotifyPage("phone", "无效提醒", "", double.NaN); } catch (ArgumentException) { badDuration = true; }
            Check(badDuration && notices == previousNotices + 1, "插件提醒拒绝无效持续时间");
            PrivateCall(window, "SelectComponent", IslandPageIds.Download);
            notifyContext.NotifyPage("phone", "手机连接成功", "卡片旁路测试", 6);
            PrivateCall(window, "SyncNotificationLayers");
            Check(AgentField<IslandState>(window, "_state") == IslandState.Card
                && AgentField<string?>(window, "_sideBubbleComponentKey") == key, "已经在卡片时连接提醒显示于旁侧，不抢走正在查看的卡片");
            SetAgentField(window, "_approvalCurrent", new ApprovalRequest("vivo-notice-approval", "test", "{}", "等待用户", 30, DateTime.UtcNow));
            layers.ShowSeconds("approval", 30); PrivateCall(window, "SetState", IslandState.Pill);
            notifyContext.NotifyPage("phone", "手机连接成功", "审批期间的连接提醒", 6);
            PrivateCall(window, "SyncNotificationLayers");
            Check(layers.IsVisible("approval") && !layers.IsVisible(key) && layers.IsActive(key),
                "手机连接提醒在审批期间作为卫星等待，不能覆盖待批准的请求");
            SetAgentField(window, "_approvalCurrent", null); layers.Hide("approval"); PrivateCall(window, "SyncNotificationLayers");
            settings = new SettingsWindow(persistSettings: false); settings.LoadFrom(options); settings.BindPluginHost(host);
            settings.OptionsChanged += (_, updated) => { options.CopyFrom(updated); host.Sync(options); };
            var editors = (StackPanel)settings.FindName("PluginRows");
            Check(Descendants<System.Windows.Controls.Button>(editors).Any(b => Equals(b.Tag, "plugin-folder:" + folder)),
                "插件管理页能打开实际部署目录以便删除内置目录中的插件");
            var toggle = Descendants<CheckBox>(editors).Single(c => Equals(c.Tag, PluginHost.EnabledKey("vivo.office")));
            var instance = entry.Instance; toggle.IsChecked = false; PumpAnimations(30);
            Check(!entry.Running && !layers.IsRegistered(key) && !layers.IsPinned(key) && pages.IndexOf(page.Id) < 0 && layers.IsPinned("download"),
                "设置中禁用插件立即撤下卡片和顶置且保留其他模块");
            Check(instance!.GetType().GetField("_timer", flags)!.GetValue(instance) == null, "禁用插件停止两秒刷新计时器");
            int stoppedNotices = notices;
            notifyContext.NotifyPage("phone", "已停用插件的迟到提醒", "");
            Check(notices == stoppedNotices && !layers.IsRegistered(key), "停用后的迟到提醒不能重新创建小岛");
            Descendants<CheckBox>(editors).Single(c => Equals(c.Tag, PluginHost.EnabledKey("vivo.office"))).IsChecked = true; PumpAnimations(30);
            Check(entry.Running && entry.Pages.Single().ReadSummary()!.Contains("另一台手机"), "重新启用插件重新读取最新记录");
            page = entry.Pages.Single(); key = "card/" + page.Id;
            PrivateCall(window, "SelectComponent", page.Id); PrivateCall(window, "ToggleCardPin");
            Check(layers.IsPinned(key), "卸载前 vivo 插件仍可重新顶置");
            instance = entry.Instance;
            Descendants<System.Windows.Controls.Button>(editors).Single(b => Equals(b.Tag, "plugin-uninstall:" + folder))
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            PumpAnimations(30);
            Check(host.Entries.Count == 0 && !Directory.Exists(folder) && pages.Pages.All(p => p.Id != page.Id)
                && !layers.IsRegistered(key) && !layers.IsPinned(key) && layers.IsPinned("download"),
                "一键卸载 vivo 插件删除目录、撤下卡片和顶置且保留其他顶置");
            Check(instance!.GetType().GetField("_timer", flags)!.GetValue(instance) == null
                && !options.Extras.ContainsKey(PluginHost.EnabledKey("vivo.office")), "一键卸载停止 vivo 刷新并移除开关设置");
            Exception? callbackError = null;
            var failing = new PluginIslandPage("summary.probe", new("card", "摘要错误测试", () => new Grid())
                { Summary = () => throw new InvalidOperationException("summary failed") }, ex => callbackError = ex);
            Check(failing.ReadSummary() == null && callbackError != null, "插件摘要异常由宿主隔离并回退默认摘要");
        }
        finally
        {
            SetAgentField(window, "_approvalCurrent", null); layers.Hide("approval");
            if (settings != null) { settings.ForceClose = true; settings.Close(); }
            Environment.SetEnvironmentVariable("ALPHA_ISLAND_VIVO_PATH", previousPath);
        }
    }
}
