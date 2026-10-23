using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using alpha_island.Core;
using alpha_island.Interop;
using alpha_island.Services;
using DataFormats = System.Windows.DataFormats;

internal static partial class Program
{
    private static void ClipboardMusicChecks(alpha_island.MainWindow window, Grid root, string testRoot)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Call(string name, params object?[] values)
        {
            var method = typeof(alpha_island.MainWindow).GetMethod(name, flags)!;
            return method.Invoke(window, method.GetParameters().Select((p, i) => i < values.Length ? values[i] : p.DefaultValue).ToArray());
        }
        T Field<T>(string name) => (T)ReadWindowMember(window, name)!;
        void Set(string name, object? value) => WriteWindowMember(window, name, value);
        var layers = Field<IslandLayerHost>("_layers");
        uint sequence = 10;
        int reads = 0, copied = 0, pasted = 0, failures = 0;
        bool swapDuringRead = false;
        System.Windows.IDataObject payload = new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText, "旧剪切板");
        using var hotkeys = new HotkeyService(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        using var clipboard = new ClipboardService(System.Windows.Threading.Dispatcher.CurrentDispatcher, hotkeys,
            () => sequence, () =>
            {
                reads++;
                if (failures-- > 0) throw new System.Runtime.InteropServices.ExternalException("clipboard busy");
                var snapshot = payload;
                if (swapDuringRead)
                {
                    swapDuringRead = false;
                    sequence++;
                    payload = new System.Windows.DataObject(DataFormats.UnicodeText, "读取途中复制的新内容");
                }
                return snapshot;
            }, Path.Combine(testRoot, "clipboard-music-history.json"));
        using var media = new SmtcService(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        clipboard.ContentCopied += (_, e) => { copied++; Call("OnClipboardContentCopied", null, e); };
        clipboard.ContentPasted += (_, e) => { pasted++; Call("OnClipboardContentPasted", null, e); };
        clipboard.CopiedDismissed += (_, e) => Call("OnClipboardCopiedDismissed", null, e);
        void PressKey(Key key, ModifierKeys modifiers) => typeof(ClipboardService).GetMethod("OnKeyPressed", flags)!.Invoke(clipboard,
            new object?[] { null, new KeyboardHookEventArgs(key, KeyInterop.VirtualKeyFromKey(key), modifiers, true) });
        void Copy(string text, bool message = true)
        {
            int beforeCopied = copied;
            sequence++;
            payload = new System.Windows.DataObject(DataFormats.UnicodeText, text);
            if (message)
                typeof(ClipboardService).GetMethod("ClipboardWndProc", flags)!.Invoke(clipboard,
                    new object[] { IntPtr.Zero, NativeMethods.WM_CLIPBOARDUPDATE, IntPtr.Zero, IntPtr.Zero, false });
            // Dispatcher.Background 会受离屏渲染/构建负载影响，等待实际结果而非精确押中 250ms 巡检节拍。
            long deadline = Environment.TickCount64 + 1500;
            do { PumpAnimations(25); } while (copied == beforeCopied && Environment.TickCount64 < deadline);
            root.UpdateLayout();
        }
        bool ClipboardMain() => Field<string>("_activeMainIslandKey") == "clipboard" && layers.IsVisible("clipboard")
            && window.ClipboardNotifyLayer.Visibility == Visibility.Visible && !layers.PrimaryVisible;
        try
        {
            Set("_clipboard", clipboard); Set("_smtc", media);
            clipboard.Start(); PumpAnimations(300);
            Check(reads == 0 && copied == 0, "启动仅记录剪切板基线，不把已有内容补弹为新复制");
            PressKey(Key.C, ModifierKeys.Control); PumpAnimations(100);
            Check(reads == 0 && copied == 0, "只按复制热键但未新增剪切板内容时不误弹");

            Copy("右键复制的项目文字");
            Check(copied == 1 && clipboard.IsCopiedPending && ClipboardMain(), "无需复制热键，系统剪切板更新让复制提示直接占据主岛");
            Check((bool)Call("CanUseClipboardQuickPaste")!, "非热键复制后仍可直接使用右键快捷粘贴，无需修饰键");
            var quickPaste = HotkeyService.CreatePasteInputs();
            Check(quickPaste.Length == 4 && quickPaste[0].u.ki.wVk == NativeMethods.VK_CONTROL
                && quickPaste[1].u.ki.wVk == NativeMethods.VK_V && quickPaste[2].u.ki.wVk == NativeMethods.VK_V
                && quickPaste[2].u.ki.dwFlags == NativeMethods.KEYEVENTF_KEYUP
                && quickPaste[3].u.ki.wVk == NativeMethods.VK_CONTROL && quickPaste[3].u.ki.dwFlags == NativeMethods.KEYEVENTF_KEYUP,
                "右键快捷粘贴保留标准 Ctrl+V 序列，不发送真实按键");
            Check(window.MusicOrbBubble.Visibility == Visibility.Collapsed && !Field<List<string>>("_activeSatelliteOrder").Contains("music"),
                "无媒体时复制不带出默认音乐卫星");
            Call("SetMusicPinned", true);
            Check(!Field<bool>("_isPinned") && ClipboardMain(), "空播放器不能被误置顶成音乐岛");
            clipboard.CheckClipboardSequence(); PumpAnimations(100);
            Check(copied == 1 && reads == 1, "重复变化消息和巡检不会重复读取同一剪切板版本");
            Copy("菜单复制且没有系统消息", message: false);
            Check(copied == 2 && window.ClipboardPreviewText.Text == "菜单复制且没有系统消息", "系统消息缺失时序号巡检仍能识别菜单复制");

            layers.ShowUntil("clipboard", DateTime.UtcNow.AddDays(-1)); layers.ExpireAllStale();
            Call("CollapseToBar"); Call("HoverTimer_Tick", null, EventArgs.Empty);
            Check(Field<bool>("_clipboardLocked") && layers.IsActive("clipboard") && Field<IslandState>("_state") == IslandState.Pill,
                "未粘贴复制提示无时间上限，普通自动收回与悬停轮询不能撤下");

            media.SetDebugNowPlaying(new NowPlayingInfo { Title = "正在播放的示例", State = PlaybackState.Playing });
            Call("SetMusicPinned", true); Call("SwitchMainIsland", "music");
            Copy("菜单复制且没有系统消息");
            Check(copied == 3 && ClipboardMain() && Field<bool>("_isPinned") && window.MusicOrbBubble.Visibility == Visibility.Visible,
                "相同文本重新复制也接管主岛，置顶音乐保留为可切换卫星");
            Call("SwitchMainIsland", "music"); Call("SwitchMainIsland", "clipboard");
            Check(ClipboardMain() && clipboard.IsCopiedPending, "音乐和常驻剪切板可以双向切换且不会解除复制驻留");

            media.SetDebugNowPlaying(null);
            Call("OnNowPlayingChanged", null, null); PumpAnimations(650);
            Call("OnNowPlayingChanged", null, null); PumpAnimations(650);
            Check(!Field<System.Windows.Threading.DispatcherTimer>("_mediaLostTimer").IsEnabled && !Field<bool>("_isPinned")
                && window.MusicOrbBubble.Visibility == Visibility.Collapsed && ClipboardMain() && Field<bool>("_clipboardLocked"),
                "重复无媒体事件不会无限延后撤岛，音乐结束也不收掉常驻剪切板");
            SaveThemePreview(window.IslandBody, "docs/screens/clipboard-primary.png");

            Call("SetState", IslandState.Card, false, IslandPageIds.System);
            Copy("在完整卡片里复制");
            Check(Field<IslandState>("_state") == IslandState.Pill && ClipboardMain(), "在其它卡片里复制时也直接显示对应剪切板提示");
            PressKey(Key.V, ModifierKeys.Control); PumpAnimations(80);
            Check(!clipboard.IsCopiedPending && !Field<bool>("_clipboardLocked") && window.ClipboardStatusTitle.Text == "已粘贴"
                && layers.Peek("clipboard")?.Lifetime == NotificationLifetime.Timed, "粘贴后解除自动驻留并改为限时完成提示");
            Set("_clipboardPastedUntil", DateTime.MinValue); layers.ShowUntil("clipboard", DateTime.UtcNow.AddSeconds(-1));
            Call("HoverTimer_Tick", null, EventArgs.Empty);
            Check(Field<IslandState>("_state") == IslandState.Bar, "粘贴完成提示到期后无音乐时可正常缩回");

            Copy("延迟回调之前的复制");
            int previousPasted = pasted;
            clipboard.NotifyPasted();
            clipboard.ProcessClipboardData(new System.Windows.DataObject(DataFormats.UnicodeText, "新复制不能被旧粘贴回调解除"), false);
            PumpAnimations(80);
            Check(pasted == previousPasted && Field<bool>("_clipboardLocked") && clipboard.IsCopiedPending,
                "旧粘贴回调不能解除紧接着发生的新复制");

            sequence++; payload = new System.Windows.DataObject(DataFormats.UnicodeText, "历史列表代粘贴");
            clipboard.AcceptOwnClipboardWrite(sequence); clipboard.NotifyPasted(); PumpAnimations(350);
            Check(!clipboard.IsCopiedPending && !Field<bool>("_clipboardLocked") && !clipboard.History.Any(x => x.Text == "历史列表代粘贴"),
                "历史粘贴写入不会再被系统变化消息当作新复制而重新锁定");

            sequence++; payload = new System.Windows.DataObject(DataFormats.UnicodeText, "被替换的旧读取"); swapDuringRead = true;
            int previousCopied = copied;
            clipboard.CheckClipboardSequence(); PumpAnimations(180);
            Check(copied == previousCopied + 1 && window.ClipboardPreviewText.Text == "读取途中复制的新内容"
                && !clipboard.History.Any(x => x.Text == "被替换的旧读取"), "读取途中剪切板被替换时只展示和记录最终新快照");

            failures = 2; Copy("占用后恢复的复制"); PumpAnimations(150);
            Check(clipboard.IsCopiedPending && window.ClipboardPreviewText.Text == "占用后恢复的复制", "剪切板短暂占用后有限重试仍能及时识别新内容");
            sequence++; payload = new System.Windows.DataObject(DataFormats.UnicodeText, "复制后立即粘贴");
            clipboard.CheckClipboardSequence(); PressKey(Key.Insert, ModifierKeys.Shift); PumpAnimations(150);
            Check(!clipboard.IsCopiedPending && !Field<bool>("_clipboardLocked") && !clipboard.History.Any(x => x.Text == "复制后立即粘贴"),
                "读取等待期间的 Shift+Insert 粘贴会取消旧读取，避免粘贴之后再弹复制常驻");

            Call("ShowStartupGreeting"); Copy("欢迎动画期间复制"); Call("DismissGreeting");
            Check(ClipboardMain() && Field<bool>("_clipboardLocked"), "欢迎动画结束不能抢回已显示的剪切板主岛");
            Set("_approvalCurrent", new ApprovalRequest("clipboard-ui-approval", "test", "{}", "等待用户", 30, DateTime.UtcNow));
            layers.ShowSeconds("approval", 30); Copy("审批期间复制");
            Check(layers.IsVisible("approval") && layers.IsActive("clipboard") && Field<bool>("_clipboardLocked"),
                "复制驻留保留审批优先级，审批期间作为卫星等待而不覆盖裁决按钮");
            Set("_approvalCurrent", null); layers.Hide("approval"); Call("SyncNotificationLayers");
            Check(ClipboardMain(), "审批结束后尚未粘贴的剪切板可以恢复到主岛");
        }
        finally
        {
            Set("_approvalCurrent", null); Set("_clipboardLocked", false); Set("_clipboardPastedUntil", DateTime.MinValue);
            Set("_clipboard", null); Set("_smtc", null);
            Call("DismissGreeting"); layers.HideAll(); Call("CollapseToBar", true); root.UpdateLayout();
        }
    }
}
