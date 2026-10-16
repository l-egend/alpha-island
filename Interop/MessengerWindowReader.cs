using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using alpha_island.Core;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Rect = System.Windows.Rect;

namespace alpha_island.Interop;

internal sealed record MessengerProcess(MessengerClient Client, int Id, string Path, long Started);
internal sealed record MessengerPopup(IntPtr Window, MessengerClient Client, string Title, string Body, bool HasContent = true);
internal sealed record MessengerTrayIcon(MessengerClient Client, Rect Bounds, string Position);
internal sealed record MessengerSnapshot(IReadOnlyList<MessengerProcess> Processes,
    IReadOnlyDictionary<MessengerClient, int> Unread, IReadOnlyList<MessengerPopup> Popups,
    IReadOnlyList<MessengerTrayIcon> TrayIcons);

/// <summary>Read only notification surfaces. Never inspect chat history, databases or edit controls.</summary>
internal sealed class MessengerWindowReader
{
    private static readonly ConcurrentDictionary<MessengerClient, string> KnownPaths = new();
    private readonly Func<IReadOnlyList<MessengerProcess>>? _fixtureProcesses;
    internal MessengerWindowReader(Func<IReadOnlyList<MessengerProcess>>? fixtureProcesses = null) => _fixtureProcesses = fixtureProcesses;
    private IReadOnlyList<MessengerProcess> _processes = Array.Empty<MessengerProcess>();
    private DateTimeOffset _nextProcesses;
    internal IReadOnlyList<MessengerProcess> ReadProcesses()
    {
        if (_fixtureProcesses != null) return _fixtureProcesses();
        if (DateTimeOffset.UtcNow < _nextProcesses) return _processes;
        _nextProcesses = DateTimeOffset.UtcNow.AddSeconds(4);
        var found = new List<MessengerProcess>();
        foreach (string name in new[] { "QQ", "WeChat", "Weixin" })
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            try
            {
                string? path = process.MainModule?.FileName;
                if (path == null || !IsClientExecutable(path)) continue;
                var client = MessengerIdentity.FromProcess(name)!.Value;
                KnownPaths[client] = path;
                found.Add(new(client, process.Id, path, process.StartTime.ToUniversalTime().Ticks));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
        }
        return _processes = found;
    }
    internal static bool IsClientExecutable(string path)
    {
        if (MessengerIdentity.FromProcess(Path.GetFileNameWithoutExtension(path)) == null) return false;
        try
        {
            string company = FileVersionInfo.GetVersionInfo(path).CompanyName ?? "";
            return company.Contains("Tencent", StringComparison.OrdinalIgnoreCase) || company.Contains("腾讯", StringComparison.Ordinal);
        }
        catch { return false; }
    }
    internal MessengerSnapshot Read(IReadOnlySet<MessengerClient> enabled)
    {
        var processes = ReadProcesses().Where(p => enabled.Contains(p.Client)).ToArray();
        var byId = processes.ToDictionary(p => (uint)p.Id);
        var unread = new Dictionary<MessengerClient, int>();
        var popups = new List<MessengerPopup>();
        var icons = new List<MessengerTrayIcon>();
        NativeMethods.EnumWindows((window, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(window, out uint id);
            if (!byId.TryGetValue(id, out var process)) return true;
            string title = ReadTitle(window);
            if (MessengerIdentity.FromTrayName(title) == process.Client && MessengerIdentity.UnreadCount(title) is { } count)
                SetCount(unread, process.Client, count);
            if (!IsNotificationWindow(window, title)) return true;
            var text = ReadNotificationText(window);
            if (text != null) popups.Add(new(window, process.Client, text.FirstOrDefault() ?? "收到新消息",
                text.Length >= 2 ? string.Join("\n", text.Skip(1)) : "点击打开 " + MessengerIdentity.Name(process.Client) + " 查看", text.Length >= 2));
            return true;
        }, IntPtr.Zero);
        if (processes.Length > 0)
        foreach (string className in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland" })
        {
            IntPtr window = IntPtr.Zero;
            while ((window = FindWindowEx(IntPtr.Zero, window, className, null)) != IntPtr.Zero)
            {
                try
                {
                    foreach (var element in Walk(AutomationElement.FromHandle(window), 500))
                    {
                        var current = element.Current;
                        if (MessengerIdentity.FromTrayName(current.Name) is not { } client || !enabled.Contains(client)
                            || !processes.Any(p => p.Client == client)) continue;
                        if (MessengerIdentity.UnreadCount(current.Name + " " + current.ItemStatus) is { } count) SetCount(unread, client, count);
                        var rect = current.BoundingRectangle;
                        if (!current.IsOffscreen && rect.Width is >= 16 and <= 96 && rect.Height is >= 16 and <= 96)
                            icons.Add(new(client, rect, $"{window}/{rect.X:F0}/{rect.Y:F0}/{rect.Width:F0}/{rect.Height:F0}"));
                    }
                }
                catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
            }
        }
        return new(processes, unread, popups, icons.DistinctBy(i => i.Client).ToArray());
    }
    private static void SetCount(Dictionary<MessengerClient, int> counts, MessengerClient client, int count) =>
        counts[client] = Math.Max(count, counts.GetValueOrDefault(client));
    private static bool IsNotificationWindow(IntPtr window, string title)
    {
        // Tray message hosts can be made visible accidentally; they are never notification cards.
        if (IsInternalWindow(ReadClass(window), title)) return false;
        if (!NativeMethods.IsWindowVisible(window) || NativeMethods.IsIconic(window) || !GetWindowRect(window, out var rect)) return false;
        double scale = Math.Max(96, GetDpiForWindow(window)) / 96.0;
        double width = (rect.Right - rect.Left) / scale, height = (rect.Bottom - rect.Top) / scale;
        if (width is < 100 or > 600 || height is < 35 or > 360) return false;
        string type = ReadClass(window);
        if (type == "#32768" || System.Text.RegularExpressions.Regex.IsMatch(type, @"tooltip|QTipLabel", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(title, @"设置|登录|setting|login", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(type, @"Notify|Notification|Toast|WeChatTip|MessageTip", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return true;
        // Some QQ builds use a generic Electron/Qt class for their bottom-right banner.
        long extended = GetWindowLongPtr(window, -20).ToInt64();
        long style = GetWindowLongPtr(window, -16).ToInt64();
        if ((extended & 0x8) == 0 || (style & 0x40000) != 0) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(title, @"^(消息提醒|新消息|New notification)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return true;
        var monitor = NativeMethods.MonitorFromWindow(window, 2);
        var info = new NativeMethods.MONITORINFO { Size = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        return NativeMethods.GetMonitorInfo(monitor, ref info)
            && Math.Abs(rect.Right - info.Work.Right) <= 90 * scale && Math.Abs(rect.Bottom - info.Work.Bottom) <= 160 * scale;
    }
    private static string[]? ReadNotificationText(IntPtr window)
    {
        try
        {
            var text = new List<string>();
            foreach (var element in Walk(AutomationElement.FromHandle(window), 160, skipWindowChrome: true))
            {
                var current = element.Current;
                if (current.ControlType is var type && (type == ControlType.Menu || type == ControlType.MenuItem || type == ControlType.ToolTip)) return null;
                if (current.ControlType != ControlType.Text) continue;
                string name = current.Name.Trim();
                if (name.Length is > 0 and <= 4096) text.Add(name);
            }
            return text.ToArray();
        }
        catch { return Array.Empty<string>(); }
    }
    private static IEnumerable<AutomationElement> Walk(AutomationElement root, int limit, bool skipWindowChrome = false, bool raw = false)
    {
        var queue = new Queue<(AutomationElement Element, int Depth)>(); queue.Enqueue((root, 0));
        var walker = raw ? TreeWalker.RawViewWalker : TreeWalker.ControlViewWalker;
        int visited = 0, scheduled = 1;
        while (queue.Count > 0 && visited++ < limit)
        {
            var (element, depth) = queue.Dequeue(); yield return element;
            if (depth >= 12 || skipWindowChrome && element.Current.ControlType == ControlType.TitleBar) continue;
            for (var child = walker.GetFirstChild(element); child != null && scheduled < limit; child = walker.GetNextSibling(child))
            { queue.Enqueue((child, depth + 1)); scheduled++; }
        }
    }
    internal static byte[]? ReadTrayPixels(MessengerTrayIcon icon)
    {
        // Read only a known, unobscured 16px tray icon. No desktop/chat screenshots or OCR.
        int x = (int)(icon.Bounds.X + icon.Bounds.Width / 2), y = (int)(icon.Bounds.Y + icon.Bounds.Height / 2);
        if (NativeMethods.GetCursorPos(out var pointer) && icon.Bounds.Contains(pointer.X, pointer.Y)) return null;
        IntPtr under = WindowFromPoint(new NativeMethods.POINT { X = x, Y = y });
        string rootClass = ReadClass(GetAncestor(under, 2));
        if (rootClass is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland")) return null;
        try
        {
            using var bitmap = new Drawing.Bitmap(16, 16);
            using (var graphics = Drawing.Graphics.FromImage(bitmap)) graphics.CopyFromScreen(x - 8, y - 8, 0, 0, new Drawing.Size(16, 16));
            var pixels = new byte[64];
            for (int row = 0; row < 8; row++) for (int col = 0; col < 8; col++)
            {
                var color = bitmap.GetPixel(col * 2 + 1, row * 2 + 1);
                pixels[row * 8 + col] = (byte)((color.R >> 5) << 5 | (color.G >> 5) << 2 | color.B >> 6);
            }
            return pixels;
        }
        catch { return null; }
    }
    internal static byte[]? ReadLogo(string executable)
    {
        try
        {
            using var icon = Drawing.Icon.ExtractAssociatedIcon(executable);
            using var bitmap = icon?.ToBitmap();
            if (bitmap == null) return null;
            using var stream = new MemoryStream(); bitmap.Save(stream, Drawing.Imaging.ImageFormat.Png); return stream.ToArray();
        }
        catch { return null; }
    }
    internal static bool TryOpen(MessengerClient client, MessengerActivationOperations? operations = null)
    {
        operations ??= new(() => false, () => TryOpenTray(client), () => TryStartClient(client));
        return operations.OpenTray() || operations.StartClient();
    }
    internal static IEnumerable<AutomationElement> ReadTrayElements()
    {
        foreach (string className in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland" })
        {
            IntPtr window = IntPtr.Zero;
            while ((window = FindWindowEx(IntPtr.Zero, window, className, null)) != IntPtr.Zero)
            {
                AutomationElement[] elements;
                try { elements = Walk(AutomationElement.FromHandle(window), 1500, raw: true).ToArray(); }
                catch { continue; }
                foreach (var element in elements) yield return element;
            }
        }
    }
    internal static bool TryOpenTray(MessengerClient client, IEnumerable<AutomationElement>? elements = null)
    {
        if (elements != null) return TryInvokeTrayIcon(client, elements);
        var available = ReadTrayElements().ToArray();
        if (TryInvokeTrayIcon(client, available)) return true;
        // Windows 11 creates the hidden notification icons only after expanding the tray.
        foreach (var element in available)
        try
        {
            var current = element.Current;
            if (!current.IsEnabled || current.IsOffscreen || current.ControlType != ControlType.Button
                || !System.Text.RegularExpressions.Regex.IsMatch(current.Name, @"隐藏.*图标|隱藏.*圖示|hidden.*icons", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                || !element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)) continue;
            ((InvokePattern)pattern).Invoke();
            for (int attempt = 0; attempt < 10; attempt++)
            {
                Thread.Sleep(60);
                if (TryInvokeTrayIcon(client, ReadTrayElements())) return true;
            }
            break;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or ElementNotEnabledException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        return false;
    }
    private static bool TryInvokeTrayIcon(MessengerClient client, IEnumerable<AutomationElement> elements)
    {
        foreach (var element in elements)
        try
        {
            var current = element.Current;
            if (!current.IsEnabled || current.ControlType != ControlType.Button
                || current.ClassName.Contains("TaskList", StringComparison.OrdinalIgnoreCase)
                || current.AutomationId.StartsWith("Appid:", StringComparison.OrdinalIgnoreCase)
                || MessengerIdentity.FromTrayName(current.Name) != client) continue;
            if (client == MessengerClient.QQ && current.AutomationId == "NotifyItemIcon"
                && current.ClassName == "SystemTray.NormalButton") return TryDoubleClickTrayIcon(current.BoundingRectangle);
            if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)) continue;
            ((InvokePattern)pattern).Invoke(); return true;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or ElementNotEnabledException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        return false;
    }
    private static bool TryDoubleClickTrayIcon(Rect bounds)
    {
        if (bounds.IsEmpty || bounds.Width is < 16 or > 96 || bounds.Height is < 16 or > 96
            || (GetAsyncKeyState(1) & 0x8000) != 0 || (GetAsyncKeyState(2) & 0x8000) != 0
            || !NativeMethods.GetCursorPos(out var original)) return false;
        int x = (int)(bounds.X + bounds.Width / 2), y = (int)(bounds.Y + bounds.Height / 2);
        var under = WindowFromPoint(new NativeMethods.POINT { X = x, Y = y });
        if (ReadClass(GetAncestor(under, 2)) is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland")) return false;
        if (!SetCursorPos(x, y)) return false;
        try
        {
            var inputs = new[] { NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_LEFTUP,
                NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_LEFTUP }.Select(flag => new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                u = new NativeMethods.InputUnion { mi = new NativeMethods.MOUSEINPUT
                    { dwFlags = flag, dwExtraInfo = NativeMethods.ExternalShortcutInputTag } }
            }).ToArray();
            bool sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>()) == inputs.Length;
            Thread.Sleep(90); return sent;
        }
        finally
        {
            if (NativeMethods.GetCursorPos(out var current) && Math.Abs(current.X - x) < 3 && Math.Abs(current.Y - y) < 3)
                SetCursorPos(original.X, original.Y);
        }
    }
    private static bool TryStartClient(MessengerClient client)
    {
        var processes = new MessengerWindowReader().ReadProcesses().Where(p => p.Client == client).ToArray();
        if (processes.Length > 0) return false; // Running messaging clients restore themselves through their tray entries.
        try
        {
            string? executable = processes.FirstOrDefault()?.Path ?? KnownPaths.GetValueOrDefault(client) ?? InstalledPath(client);
            if (executable == null || !File.Exists(executable) || !IsClientExecutable(executable)) return false;
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true }); return true;
        }
        catch { return false; }
    }
    internal static IntPtr FindPrimaryWindow(MessengerClient client, IReadOnlySet<int> processIds)
    {
        IntPtr best = IntPtr.Zero; long largest = 0;
        NativeMethods.EnumWindows((window, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(window, out uint id);
            if (!processIds.Contains((int)id) || NativeMethods.GetWindow(window, 4) != IntPtr.Zero || !GetWindowRect(window, out var rect)) return true;
            string title = ReadTitle(window), type = ReadClass(window);
            if (!IsPrimaryWindow(client, type, title) || IsNotificationWindow(window, title)) return true;
            if (NativeMethods.IsIconic(window))
            {
                var placement = new NativeMethods.WINDOWPLACEMENT { Length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
                if (NativeMethods.GetWindowPlacement(window, ref placement)) rect = placement.Normal;
            }
            long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            if (area > largest) { largest = area; best = window; } return true;
        }, IntPtr.Zero);
        return best;
    }
    private static bool IsInternalWindow(string type, string title) => System.Text.RegularExpressions.Regex.IsMatch(type + " " + title,
        @"TrayIcon|MessageWindow|SystemMessage|PowerMessage|Tooltip|IME|ImagePreview|VideoPlayer",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    internal static bool IsPrimaryWindow(MessengerClient client, string type, string title)
    {
        if (IsInternalWindow(type, title) || System.Text.RegularExpressions.Regex.IsMatch(type + " " + title, @"Notify|Notification",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return false;
        bool caption = System.Text.RegularExpressions.Regex.IsMatch(title.Trim(), client == MessengerClient.WeChat
            ? @"^(微信|WeChat|Weixin)(\s*[\[(（][^\r\n]*[\])）])?$"
            : @"^(QQ|腾讯QQ)(\s*[\[(（][^\r\n]*[\])）])?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!caption) return false;
        bool qt = System.Text.RegularExpressions.Regex.IsMatch(type, @"^Qt\d+QWindowIcon$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return client == MessengerClient.WeChat ? qt || type is "WeChatMainWndForPC" or "mmui::MainWindow"
            : qt || type is "Chrome_WidgetWin_1" or "TXGuiFoundation";
    }
    private static string? InstalledPath(MessengerClient client)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        foreach (string uninstall in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var root = hive.OpenSubKey(uninstall);
            if (root == null) continue;
            foreach (string name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                string display = key?.GetValue("DisplayName") as string ?? "";
                if (MessengerIdentity.FromTrayName(display) != client) continue;
                string icon = (key?.GetValue("DisplayIcon") as string ?? "").Split(',')[0].Trim().Trim('"');
                if (File.Exists(icon) && IsClientExecutable(icon)) return icon;
            }
        }
        return null;
    }
    internal static string ReadTitle(IntPtr window) { var text = new StringBuilder(512); GetWindowText(window, text, text.Capacity); return text.ToString(); }
    internal static string ReadClass(IntPtr window) { var text = new StringBuilder(256); GetClassName(window, text, text.Capacity); return text.ToString(); }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeMethods.RECT rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flag);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
