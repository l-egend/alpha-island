using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;

internal static partial class Program
{
    private sealed class ShortcutInputStub : IShortcutInputTransport
    {
        internal readonly List<NativeMethods.INPUT[]> Batches = new();
        internal uint? FirstResult;
        internal Func<int, bool> Held = _ => false;
        public bool IsKeyDown(int key) => Held(key);
        public uint Send(NativeMethods.INPUT[] inputs)
        {
            Batches.Add(inputs);
            return Batches.Count == 1 && FirstResult.HasValue ? FirstResult.Value : (uint)inputs.Length;
        }
    }

    private static async Task ShortcutSimulationChecks(string root)
    {
        foreach (string text in new[] { "Ctrl+Alt+D", " control + SHIFT + t ", "F8", "Ctrl+1", "Win+Shift+Left", "Alt+Enter" })
        {
            Check(ShortcutSimulationService.TryParse(text, out var shortcut), "解析有效快捷键 " + text);
            Check(ShortcutSimulationService.TryParse(ShortcutSimulationService.Format(shortcut), out var again) && again == shortcut,
                "快捷键格式化可重复读取 " + text);
        }
        foreach (string text in new[] { "", "D", "Ctrl", "Ctrl+Ctrl+D", "Ctrl++D", "D+Ctrl", "Ctrl+999", "Ctrl+Alt+Delete", "Ctrl+None", "Ctrl+LeftCtrl", "Ctrl+Alt+", "Alt+Sleep" })
            Check(!ShortcutSimulationService.TryParse(text, out _), "拒绝无效快捷键 " + text);
        ShortcutSimulationService.TryParse("Ctrl+Alt+D", out var youdao);
        var sequence = ShortcutSimulationService.CreateInputs(youdao);
        Check(sequence.Select(i => i.u.ki.wVk).SequenceEqual(new ushort[] { 0x11, 0x12, 0x44, 0x44, 0x12, 0x11 })
            && sequence.Take(3).All(i => i.u.ki.dwFlags == 0) && sequence.Skip(3).All(i => i.u.ki.dwFlags == NativeMethods.KEYEVENTF_KEYUP),
            "Ctrl+Alt+D 按下与逆序释放完整配对");
        Check(sequence.All(i => i.type == NativeMethods.INPUT_KEYBOARD && i.u.ki.dwExtraInfo == NativeMethods.ExternalShortcutInputTag),
            "模拟快捷键仅包含键盘输入且带岛内钩子排除标记");
        ShortcutSimulationService.TryParse("Win+Shift+Left", out var extended);
        Check(ShortcutSimulationService.CreateInputs(extended).Where(i => i.u.ki.wVk is 0x5B or 0x25).All(i => (i.u.ki.dwFlags & 1) != 0),
            "方向键与 Windows 键保留扩展标志");
        var success = new ShortcutInputStub();
        var sent = await new ShortcutSimulationService(success).SendAsync("Ctrl+Alt+D");
        Check(sent.Sent && success.Batches.Count == 1 && sent.Message.Contains("已发送"), "成功发送只报告快捷键已发送");
        var invalid = new ShortcutInputStub();
        Check(!(await new ShortcutSimulationService(invalid).SendAsync("bad shortcut")).Sent && invalid.Batches.Count == 0,
            "无效配置不向系统发送任何按键");
        var partial = new ShortcutInputStub { FirstResult = 2 };
        Check(!(await new ShortcutSimulationService(partial).SendAsync("Ctrl+Alt+D")).Sent
            && partial.Batches.Count == 2 && partial.Batches[1].Select(i => i.u.ki.wVk).SequenceEqual(new ushort[] { 0x12, 0x11 })
            && partial.Batches[1].All(i => i.u.ki.dwFlags == NativeMethods.KEYEVENTF_KEYUP), "部分发送失败仅释放已按下的修饰键");
        var blocked = new ShortcutInputStub { FirstResult = 0 };
        Check(!(await new ShortcutSimulationService(blocked).SendAsync("Ctrl+Alt+D")).Sent && blocked.Batches.Count == 1,
            "系统阻止输入时报告失败且不补发无关按键");
        var held = new ShortcutInputStub { Held = _ => true };
        Check(!(await new ShortcutSimulationService(held).SendAsync("Ctrl+Alt+D")).Sent && held.Batches.Count == 0,
            "物理按键未松开时超时且不覆盖按键状态");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool cancelled = false;
        try { await new ShortcutSimulationService(held).SendAsync("Ctrl+Alt+D", cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && held.Batches.Count == 0, "关闭或取消后不发送快捷键");
        Check((await new ShortcutSimulationService(new ShortcutInputStub()).SendAsync("F8")).Sent, "失败及取消后释放发送互斥锁");
        var options = new IslandOptions();
        SettingsRegistry.SetExtra(options, "plugin/example/enabled", true);
        SettingsRegistry.Write(options, "ExternalTranslationName", "测试翻译");
        SettingsRegistry.Write(options, "ExternalTranslationShortcut", "Ctrl+Shift+T");
        string config = Path.Combine(root, "config", "options.json"); options.Save(config);
        var loaded = IslandOptions.Load(config);
        Check(ExternalTranslationOptions.From(loaded) == new ExternalTranslationOptions(true, "测试翻译", "Ctrl+Shift+T")
            && SettingsRegistry.GetBool(loaded, "plugin/example/enabled", false), "外部翻译配置独立落盘并保留插件设置");
    }

    private static void ExternalTranslationUiChecks(alpha_island.MainWindow island, Grid islandRoot, string root)
    {
        var options = new IslandOptions();
        SettingsRegistry.Write(options, "ExternalTranslationName", "测试翻译");
        SettingsRegistry.Write(options, "ExternalTranslationShortcut", "Ctrl+Shift+T");
        var settings = new alpha_island.Settings.SettingsWindow(persistSettings: false) { ForceClose = true };
        settings.LoadFrom(options); settings.OptionsChanged += (_, changed) => options.CopyFrom(changed);
        var settingsRoot = (Grid)settings.Content; settings.Content = null; settingsRoot.Resources = settings.Resources;
        using var source = new HwndSource(new HwndSourceParameters("Translation settings regression")
        { Width = 880, Height = 720, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
        source.RootVisual = settingsRoot;
        try
        {
            settings.OpenExternalTranslationSettings(); PumpAnimations(30); settingsRoot.UpdateLayout();
            Check(((FrameworkElement)settings.FindName("Page_Ai")).Visibility == Visibility.Visible, "外部翻译设置入口定位 AI 页内独立分组");
            var box = (TextBox)settings.FindName("ExternalTranslationShortcutBox");
            var name = (TextBox)settings.FindName("ExternalTranslationNameBox");
            var enabled = (CheckBox)settings.FindName("ExternalTranslationToggle");
            Check(box.Text == "Ctrl+Shift+T" && name.Text == "测试翻译" && enabled.IsChecked == true, "快捷键与软件名称正确回填");
            box.Text = "Alt+F8";
            Check(ExternalTranslationOptions.From(options).Shortcut == "Alt+F8", "修改快捷键即时更新选项");
            enabled.IsChecked = false; settingsRoot.UpdateLayout();
            Check(!box.IsEnabled && !name.IsEnabled && !ExternalTranslationOptions.From(options).Enabled, "关闭外部翻译使配置控件禁用");
            enabled.IsChecked = true;
            Check(box.IsEnabled && name.IsEnabled, "启用后配置控件恢复");
            var record = Descendants<Button>(settingsRoot).Single(b => Equals(b.Content, "录制快捷键"));
            record.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var recorded = new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.F8)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            PrivateCall(settings, "RecordExternalShortcut", box, recorded);
            Check(recorded.Handled && ShortcutSimulationService.TryParse(box.Text, out var captured) && captured.Key == Key.F8
                && Equals(record.Content, "录制快捷键"), "录制功能保存组合键并结束录制");
            string beforeCancel = box.Text;
            record.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var escape = new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            PrivateCall(settings, "RecordExternalShortcut", box, escape);
            Check(escape.Handled && box.Text == beforeCancel && Equals(record.Content, "录制快捷键"), "Esc 取消录制且保留原快捷键");
            Check(SettingsRegistry.SearchWordsForPage(SettingsPageIds.Ai).Contains("外部翻译快捷键"), "设置搜索包含外部翻译配置");
            var input = new ShortcutInputStub(); int aiCalls = 0;
            var screen = new alpha_island.Controls.ScreenToolsWindow(() => { aiCalls++; throw new Exception("AI should not be called"); },
                () => ExternalTranslationOptions.From(options), shortcutSimulation: new ShortcutSimulationService(input));
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            try
            {
                var task = (Task)PrivateCall(screen, "InvokeExternalTranslationAsync")!;
                PumpAnimations(220); task.GetAwaiter().GetResult();
                Check(input.Batches.Count == 1 && aiCalls == 0 && !screen.IsVisible, "外部快捷键调用不要求原文或 AI 密钥且调用后不抢回前台");
                var result = (TextBox)typeof(alpha_island.Controls.ScreenToolsWindow).GetField("_result", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(screen)!;
                Check(result.Text == "", "发送快捷键不伪造岛内翻译结果");
                SettingsRegistry.Write(options, "ExternalTranslationEnabled", false); screen.RefreshExternalTranslation();
                var external = Descendants<Button>((FrameworkElement)screen.Content).Single(b => Equals(b.Content, "调用外部翻译"));
                Check(!external.IsEnabled, "已打开的屏幕翻译窗口跟随禁用设置更新");
                PrivateCall(screen, "InvokeExternalTranslationAsync");
                Check(input.Batches.Count == 1, "禁用后点击路径也不发送按键");
            }
            finally { screen.Close(); SynchronizationContext.SetSynchronizationContext(previousContext); }
            Check(Descendants<Button>(islandRoot).Any(b => Equals(b.Content, "调用外部翻译"))
                && Descendants<Button>(islandRoot).Any(b => Equals(b.Content, "快捷键设置")), "岛卡片同时提供外部调用和快捷键设置入口");
            Directory.CreateDirectory("bin/external-translation-previews");
            SaveThemePreview(settingsRoot, "bin/external-translation-previews/settings.png");
        }
        finally { settings.Close(); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    private static void ShortcutNativeReceiverCheck()
    {
        using var receiver = new HwndSource(new HwndSourceParameters("Alpha Island shortcut test receiver")
        { Width = 1, Height = 1, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x80000000) });
        int received = 0;
        receiver.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        { if (message == 0x0312 && wParam.ToInt32() == 0xA120) { received++; handled = true; } return IntPtr.Zero; });
        Check(RegisterHotKey(receiver.Handle, 0xA120, 0x0001 | 0x0002 | 0x4000, 0x87), "注册专用 Ctrl+Alt+F24 测试接收器");
        try
        {
            using var ownHook = new KeyboardHook(); int islandEvents = 0;
            ownHook.KeyAction += (_, e) => { if (e.VirtualKey == 0x87) Interlocked.Increment(ref islandEvents); };
            Check(ownHook.Hook(), "启动专用岛内按键事件测试钩子");
            var result = new ShortcutSimulationService().SendAsync("Ctrl+Alt+F24").GetAwaiter().GetResult();
            PumpAnimations(100);
            Console.WriteLine($"Native shortcut result: sent={result.Sent}, received={received}, {result.Message}");
            Check(result.Sent && received == 1, "Windows SendInput 实际触发专用测试接收器一次");
            Check(islandEvents == 0, "外部快捷键不会回流为岛内按键或审批事件");
            Check((NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) == 0
                && (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) == 0, "实际发送后 Ctrl 和 Alt 均已释放");
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources["BoolToVisConverter"] = new BooleanToVisibilityConverter();
            foreach (string resource in new[] { "LiquidGlass", "Win11Settings" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/alpha-island;component/Themes/{resource}.xaml") });
            var settings = new alpha_island.Settings.SettingsWindow(persistSettings: false) { ForceClose = true };
            try
            {
                settings.LoadFrom(new IslandOptions());
                var record = (Button)typeof(alpha_island.Settings.SettingsWindow).GetField("_recordShortcutButton", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(settings)!;
                record.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                ShortcutSimulationService.TryParse("Ctrl+Alt+F24", out var shortcut);
                var rawKeys = ShortcutSimulationService.CreateInputs(shortcut);
                for (int i = 0; i < rawKeys.Length; i++) rawKeys[i].u.ki.dwExtraInfo = IntPtr.Zero;
                Check(NativeMethods.SendInput((uint)rawKeys.Length, rawKeys, Marshal.SizeOf<NativeMethods.INPUT>()) == rawKeys.Length,
                    "向录制器发送未标记的专用测试组合键");
                PumpAnimations(120);
                string recordedText = ((TextBox)settings.FindName("ExternalTranslationShortcutBox")).Text;
                Check(recordedText == "Ctrl+Alt+F24", "真实低级按键捕获能录制已注册的全局快捷键");
                Check(received == 1, "录制期间不触发其他软件的全局热键");
                Check(typeof(alpha_island.Settings.SettingsWindow).GetField("_shortcutRecorder", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(settings) == null,
                    "录制完成立即释放临时按键钩子");
                Check((NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) == 0
                    && (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) == 0, "录制结束后物理修饰键状态仍正常释放");
            }
            finally { settings.Close(); app.Shutdown(); }
        }
        finally { UnregisterHotKey(receiver.Handle, 0xA120); }
    }
}
