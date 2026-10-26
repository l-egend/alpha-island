using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;
using CheckBox = System.Windows.Controls.CheckBox;
using Grid = System.Windows.Controls.Grid;

internal static partial class Program
{
    private sealed class StartupStoreFixture : IStartupRegistrationStore
    {
        internal string? Command;
        internal int Writes;
        internal bool Fail;
        internal bool IgnoreWrites;
        internal bool Disabled;
        public bool IsDisabledByWindows => Disabled;
        public string? Read() => Command;
        public void Write(string? command) { if (Fail) throw new UnauthorizedAccessException("模拟写入拒绝"); Writes++; if (!IgnoreWrites) Command = command; }
    }

    private static void LaunchAndLinksPolicyChecks(string root)
    {
        const string full = "https://example.com/很长的路径?q=first%20second&token=12345678901234567890#section";
        Check(WebLinkDetector.Detect(full)?.AbsoluteUri == new Uri(full).AbsoluteUri, "检测完整网页地址，保留路径、参数和片段，不使用通知摘要");
        Check(WebLinkDetector.Detect(" www.example.com/path ")?.Scheme == "https", "www 网页链接补齐 HTTPS");
        Check(WebLinkDetector.Detect("查看这里：https://example.com/path。")?.AbsoluteUri == "https://example.com/path", "正文中的单个链接去除句末标点");
        Check(WebLinkDetector.Detect("https://one.example https://two.example") == null, "多个链接不误选其中一个自动跳转");
        Check(WebLinkDetector.Detect("javascript:https://example.com") == null && WebLinkDetector.Detect("file:///C:/test.txt") == null
            && WebLinkDetector.Detect("mailto:user@example.com") == null && WebLinkDetector.Detect("普通复制文本") == null, "非网页协议及普通文本保持剪贴板行为");
        Check(WebLinkDetector.Detect(new string('x', 40_000)) == null, "大文本不触发无边界链接扫描");
        var launch = DefaultBrowserLauncher.CreateStartInfo(new Uri(full));
        Check(launch.UseShellExecute && launch.Verb == "open" && launch.FileName == new Uri(full).AbsoluteUri && launch.Arguments.Length == 0,
            "完整 URL 交给 Windows 默认关联，不经命令解释器");
        bool rejected = false; try { DefaultBrowserLauncher.CreateStartInfo(new Uri("file:///C:/test.txt")); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "浏览器启动入口再次拒绝非网页地址");
        var options = new IslandOptions().Normalized();
        Check(!SettingsRegistry.GetBool(options, "StartupEnabled", true) && SettingsRegistry.GetBool(options, "StartupBootTimeNotify", false)
            && SettingsRegistry.GetBool(options, "ClipboardLinkDetectionEnabled", false), "自启动默认关闭，开机耗时与链接识别偏好默认开启");
        SettingsRegistry.Write(options, "StartupEnabled", true); SettingsRegistry.Write(options, "ClipboardLinkDetectionEnabled", false);
        var imported = JsonSerializer.Deserialize<IslandOptions>(JsonSerializer.Serialize(options))!.Normalized();
        Check(SettingsRegistry.GetBool(imported, "StartupEnabled", false) && !SettingsRegistry.GetBool(imported, "ClipboardLinkDetectionEnabled", true), "新偏好随统一配置往返");
        StartupRegistrationRecoveryChecks(root);

        var boot = DateTimeOffset.UtcNow.AddMinutes(-2);
        string Performance(string start, string main, string total = "60000") => $"<Event><EventData><Data Name='BootStartTime'>{start}</Data><Data Name='MainPathBootTime'>{main}</Data><Data Name='BootTime'>{total}</Data></EventData></Event>";
        var exact = BootTimingService.FromPerformanceXml(Performance(boot.ToString("O"), "12345"), boot);
        Check(exact?.DesktopDuration == TimeSpan.FromMilliseconds(12345) && exact.DisplayText.Contains("12.3 秒"), "开机耗时采用精确主启动路径，排除桌面后台收尾");
        Check(BootTimingService.FromPerformanceXml(Performance(boot.AddHours(-1).ToString("O"), "12345"), boot) == null, "上一轮启动记录不冒充本次开机耗时");
        Check(BootTimingService.FromPerformanceXml(Performance(boot.ToString("O"), "-1"), boot) == null
            && BootTimingService.FromPerformanceXml(Performance(boot.ToString("O"), "0"), boot) == null, "无效或零启动耗时不会显示");

        using var identity = WindowsIdentity.GetCurrent(); var user = identity.User!;
        const string original = "O:BAG:SYD:(A;;0x7;;;SY)(A;;0x7;;;BA)(A;;0x1;;;LS)";
        string granted = BootLogAccess.GrantReadDescriptor(original, user);
        var acl = new RawSecurityDescriptor(granted).DiscretionaryAcl!;
        Check(acl.OfType<CommonAce>().Single(x => x.SecurityIdentifier == user).AccessMask == 1 && acl.Count == 4,
            "启动日志授权只增加当前用户只读权限，保留其他访问项");
        Check(new RawSecurityDescriptor(granted).Owner == new RawSecurityDescriptor(original).Owner
            && BootLogAccess.GrantReadDescriptor(granted, user) == granted, "日志所有者保持不变，重复授权不叠加 ACL");
        var actual = BootTimingService.Read();
        Console.WriteLine("Boot timing live: " + actual.DisplayText + " | " + actual.Reason);
        Check(actual.DesktopDuration == null || actual.DesktopDuration > TimeSpan.Zero, "本机读取保留明确的精确或参考结果，无有效时间时不伪造耗时");
    }

    private static void StartupRegistrationRecoveryChecks(string root)
    {
        string executable = Path.Combine(root, "startup-fixture.exe"); File.WriteAllBytes(executable, [0]);
        var store = new StartupStoreFixture(); var registration = new StartupRegistrationService(store, executable);
        Check(!registration.RestoreIfRequested(false) && store.Writes == 0, "没有开启偏好时不创建启动项");
        Check(registration.RestoreIfRequested(true) && store.Command == registration.Command && store.Writes == 1, "启动流程按已保存的开启偏好恢复登记");
        Check(registration.RestoreIfRequested(true) && store.Writes == 1, "登记有效时重复启动不重复写入");
        store.Command = "\"E:\\old-location\\alpha-island.exe\" --startup";
        Check(registration.RestoreIfRequested(true) && store.Command == registration.Command && store.Writes == 2, "程序路径变化时恢复为当前可执行文件");
        store.Disabled = true;
        Check(!registration.RestoreIfRequested(true) && store.Writes == 2, "Windows 禁用的启动项不会被自动恢复逻辑重新启用");
        bool blocked = false; try { registration.SetEnabled(true); } catch (InvalidOperationException) { blocked = true; }
        Check(blocked && store.Writes == 2, "Windows 禁用时明确报告，避免显示虚假已开启");
        registration.SetEnabled(false);
        Check(store.Command == null, "系统禁用时仍可移除本程序启动登记");
        store.Disabled = false; store.IgnoreWrites = true;
        bool unsaved = false; try { registration.SetEnabled(true); } catch (IOException) { unsaved = true; }
        Check(unsaved && !registration.IsEnabled, "启动项写入被拦截时读回核验失败，不声称保存成功");
        Check(!StartupRegistrationService.IsWindowsStartupDisabled(null)
            && !StartupRegistrationService.IsWindowsStartupDisabled(new byte[] { 2 })
            && !StartupRegistrationService.IsWindowsStartupDisabled(new byte[] { 6 })
            && StartupRegistrationService.IsWindowsStartupDisabled(new byte[] { 3 })
            && StartupRegistrationService.IsWindowsStartupDisabled(new byte[] { 7 })
            && StartupRegistrationService.IsWindowsStartupDisabled(new byte[] { 255 }), "系统启动审批区分启用、禁用，未知状态不主动绕过");
    }

    private static void LaunchAndLinksUiChecks(alpha_island.MainWindow window, Grid root, string testRoot)
    {
        uint sequence = 44;
        using var hotkeys = new HotkeyService(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        using var clipboard = new ClipboardService(System.Windows.Threading.Dispatcher.CurrentDispatcher, hotkeys,
            () => sequence, () => (string?)null, Path.Combine(testRoot, "web-link-history.json"));
        SetAgentField(window, "_clipboard", clipboard); clipboard.Start();
        clipboard.ContentCopied += (sender, e) => PrivateCall(window, "OnClipboardContentCopied", sender, e);
        clipboard.CopiedDismissed += (sender, e) => PrivateCall(window, "OnClipboardCopiedDismissed", sender, e);
        List<Uri> opened = []; SetAgentField(window, "_openClipboardWebLink", new Action<Uri>(opened.Add));
        var options = AgentField<IslandOptions>(window, "_options");
        const string full = "https://example.com/long/path?q=12345678901234567890&next=%E4%B8%AD%E6%96%87#anchor";
        void Copy(string value) { clipboard.ProcessClipboardData(new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText, value), false); PumpAnimations(350); }
        try
        {
            Copy(full);
            Check(window.ClipboardStatusTitle.Text == "点击打开链接" && window.ClipboardIcon.Text == "\uE71B" && window.ClipboardModeBadgeText.Text == "链接",
                "链接复制显示点击提示、链接图标和类型徽标");
            SaveThemePreview(window.IslandBody, "bin/launch-previews/clipboard-web-link.png");
            sequence++;
            Check(!(bool)PrivateCall(window, "TryOpenClipboardWebLink", new Uri(full))! && opened.Count == 0,
                "剪贴板已变化但新快照未到时不打开旧链接");
            sequence--;
            Check((bool)PrivateCall(window, "TryOpenClipboardWebLink", new Uri(full))! && opened.Single().AbsoluteUri == full,
                "点击传递完整链接给浏览器，未被摘要截断");
            PumpAnimations(100);
            Check(!clipboard.IsCopiedPending && clipboard.CurrentWebLink == null && !AgentField<bool>(window, "_clipboardLocked"), "打开后清除待粘贴驻留，避免继续触发快捷粘贴");
            Copy("普通剪贴板文字");
            Check(window.ClipboardStatusTitle.Text == "已复制" && clipboard.CurrentWebLink == null && (bool)PrivateCall(window, "CanUseClipboardQuickPaste")!,
                "普通文本继续保留原有复制与右键粘贴行为");
            SettingsRegistry.Write(options, "ClipboardLinkDetectionEnabled", false); Copy(full);
            Check(window.ClipboardStatusTitle.Text == "已复制" && !(bool)PrivateCall(window, "TryOpenClipboardWebLink", new Uri(full))! && opened.Count == 1,
                "关闭链接识别后恢复普通文本提示且不打开浏览器");
            SettingsRegistry.Write(options, "ClipboardLinkDetectionEnabled", true); PrivateCall(window, "SyncClipboardContent");
            PrivateCall(window, "SetState", IslandState.Card, false, IslandPageIds.Clipboard);
            Check(!(bool)PrivateCall(window, "TryOpenClipboardWebLink", new Uri(full))!, "查看剪贴板历史卡片时不会把普通卡片点击当作链接跳转");
            clipboard.DismissCopiedPending(); PumpAnimations(100); PrivateCall(window, "ClearDesktopInbox");
            SetAgentField(window, "_clipboardLocked", false); PrivateCall(window, "SetState", IslandState.Bar, true);
            var boot = DateTimeOffset.UtcNow.AddMinutes(-1);
            PrivateCall(window, "PresentBootTiming", new BootTimingReading(boot, TimeSpan.FromMilliseconds(12345), "", "test"));
            var pill = AgentField<alpha_island.Controls.DesktopNotificationPill>(window, "_notificationSummary");
            PumpAnimations(350); root.UpdateLayout();
            Check(pill.Title.Text == "本次开机" && pill.Detail.Text.Contains("12.3 秒"), "自启动耗时提示复用岛的图标与双行文字布局");
            SaveThemePreview(window.IslandBody, "bin/launch-previews/boot-time.png");
        }
        finally { SetAgentField(window, "_clipboard", null); SetAgentField(window, "_clipboardLocked", false); PrivateCall(window, "ClearDesktopInbox"); }
        StartupSettingsUiChecks(testRoot);
    }

    private static void StartupSettingsUiChecks(string root)
    {
        string directory = Path.Combine(root, "启动 & spaces"); Directory.CreateDirectory(directory);
        string executable = Path.Combine(directory, "alpha-island.exe"); File.WriteAllBytes(executable, [0]);
        var store = new StartupStoreFixture(); var registration = new StartupRegistrationService(store, executable);
        int authorizations = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Task> authorize = () => { authorizations++; return gate.Task; };
        var settings = new alpha_island.Settings.SettingsWindow(false, startupRegistration: registration, authorizeBootLog: () => authorize());
        settings.LoadFrom(new IslandOptions().Normalized());
        var panel = (Grid)settings.Content; settings.Content = null; panel.Resources = settings.Resources;
        using var source = new HwndSource(new HwndSourceParameters("Startup settings regression")
        { Width = 880, Height = 600, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        source.RootVisual = panel;
        var toggle = (CheckBox)settings.FindName("StartupEnabledToggle");
        var snapshot = (IslandOptions)typeof(alpha_island.Settings.SettingsWindow).GetField("_options",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(settings)!;
        void Toggle(bool value) { settings.Dispatcher.InvokeAsync(() => toggle.IsChecked = value); PumpAnimations(100); }
        try
        {
            Check(store.Writes == 0 && toggle.IsChecked == false && settings.FindName("ClipboardLinkToggle") is CheckBox
                && settings.FindName("StartupBootTimeToggle") is CheckBox, "设置首次加载提供三个新选项且不修改真实自启动状态");
            Toggle(true);
            Check(authorizations == 1 && store.Writes == 0 && !SettingsRegistry.GetBool(snapshot, "StartupEnabled", true) && !toggle.IsEnabled,
                "开启自启动先等待日志授权，不提前登记或保存已开启状态");
            gate.SetResult(); PumpAnimations(200);
            Check(store.Command == $"\"{executable}\" --startup" && registration.IsEnabled && SettingsRegistry.GetBool(snapshot, "StartupEnabled", false),
                "授权完成后登记带引号路径和自启动标记并保存偏好");
            var saved = JsonSerializer.Deserialize<IslandOptions>(JsonSerializer.Serialize(snapshot))!;
            int writes = store.Writes;
            settings.LoadFrom(saved);
            Check(toggle.IsChecked == true && store.Writes == writes, "重开设置保留已开启状态且不重复登记");
            store.Command = null;
            var restarted = new alpha_island.Settings.SettingsWindow(false, startupRegistration: registration, authorizeBootLog: () => Task.CompletedTask);
            try
            {
                restarted.LoadFrom(saved);
                var restored = AgentField<IslandOptions>(restarted, "_options");
                Check(((CheckBox)restarted.FindName("StartupEnabledToggle")).IsChecked == true
                    && SettingsRegistry.GetBool(restored, "StartupEnabled", false) && registration.IsEnabled,
                    "重启遇到启动登记缺失时恢复已开启的偏好，不静默改回关闭");
                store.Command = null; store.Fail = true;
                restarted.LoadFrom(saved);
                Check(((CheckBox)restarted.FindName("StartupEnabledToggle")).IsChecked == false
                    && SettingsRegistry.GetBool(restored, "StartupEnabled", false), "恢复失败显示实际关闭，但不覆盖已保存的开启偏好");
                store.Fail = false; store.Disabled = true;
                restarted.LoadFrom(saved);
                Check(((CheckBox)restarted.FindName("StartupEnabledToggle")).IsChecked == false
                    && SettingsRegistry.GetBool(restored, "StartupEnabled", false) && store.Command == null,
                    "Windows 禁用时设置保留用户偏好，不偷偷创建启动登记");
                store.Disabled = false; restarted.LoadFrom(saved);
                Check(((CheckBox)restarted.FindName("StartupEnabledToggle")).IsChecked == true && registration.IsEnabled,
                    "临时阻挡解除后可再次恢复，无需重新配置");
            }
            finally { restarted.ForceClose = true; restarted.Close(); }
            PrivateCall(settings, "SelectPageByTag", "behavior"); panel.UpdateLayout();
            SaveThemePreview(panel, "bin/launch-previews/settings-launch.png");
            Toggle(false);
            Check(store.Command == null && authorizations == 1 && !SettingsRegistry.GetBool(snapshot, "StartupEnabled", true), "关闭自启动移除本程序登记且不申请管理员权限");
            authorize = () => Task.FromException(new System.ComponentModel.Win32Exception(1223)); Toggle(true);
            Check(store.Command == null && toggle.IsChecked == false && toggle.IsEnabled && !SettingsRegistry.GetBool(snapshot, "StartupEnabled", true),
                "取消 Windows 授权回退开关，不留下虚假的启用状态");
            authorize = () => Task.CompletedTask; store.Fail = true; Toggle(true);
            Check(store.Command == null && toggle.IsChecked == false && !SettingsRegistry.GetBool(snapshot, "StartupEnabled", true), "登记失败时保留原偏好与开关状态");
        }
        finally { settings.ForceClose = true; settings.Close(); }
    }
}
