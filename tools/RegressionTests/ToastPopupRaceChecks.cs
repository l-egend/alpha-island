using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using alpha_island.Interop;

internal static partial class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flag);

    // Explicit opt-in: observes only our unique test notification; neither reads unrelated
    // notification bodies nor dismisses windows or changes Windows notification settings.
    private static async Task ToastPopupRaceChecks(bool nativeOnly = false)
    {
        string calibration = Path.GetFullPath("bin/toast-popup-probe-control.json");
        if (!nativeOnly)
        {
            Check(File.Exists(calibration) && DateTime.UtcNow - File.GetLastWriteTimeUtc(calibration) < TimeSpan.FromMinutes(5),
                "发送源在最近五分钟内已通过真实系统横幅对照；没有横幅的发送源不能证明接管效果");
        }
        string title = "Alpha Island duplicate probe " + Guid.NewGuid().ToString("N")[..8];
        string expectedTitle = title + " [1/1]";
        using var app = nativeOnly ? null : Process.GetProcessesByName("alpha-island").Single();
        IntPtr island = IntPtr.Zero;
        NativeMethods.EnumWindows((window, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(window, out uint process);
            var text = new StringBuilder(256); GetWindowText(window, text, text.Capacity);
            if (app != null && process == app.Id && text.ToString() == "Alpha Island") island = window;
            return true;
        }, IntPtr.Zero);
        if (!nativeOnly) Check(island != IntPtr.Zero, "找到当前安装版的岛窗口");
        var samples = new ConcurrentQueue<(long Time, bool Native)>();
        var shellWindows = new ConcurrentDictionary<IntPtr, string>();
        var toastWindows = new ConcurrentDictionary<IntPtr, bool>();
        var watch = Stopwatch.StartNew();
        void ObserveShellWindow(IntPtr window)
        {
            if (window == IntPtr.Zero) return;
            var cls = new StringBuilder(256); GetClassName(window, cls, cls.Capacity);
            if (cls.ToString() is not ("Windows.UI.Core.CoreWindow" or "Xaml_WindowedPopupClass" or "Windows.UI.Composition.DesktopWindowContentBridge")) return;
            NativeMethods.GetWindowThreadProcessId(window, out uint process);
            string owner;
            try { using var peer = Process.GetProcessById((int)process); owner = peer.ProcessName; }
            catch { return; }
            if (!owner.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) && !owner.Equals("ShellHost", StringComparison.OrdinalIgnoreCase)) return;
            var text = new StringBuilder(256); GetWindowText(window, text, text.Capacity);
            string name = text.ToString();
            shellWindows[window] = owner + "/" + cls + "/" + name;
            if ((name.Contains("notification", StringComparison.OrdinalIgnoreCase) || name.Contains("通知", StringComparison.Ordinal))
                && name != "通知中心" && !name.Equals("Notification Center", StringComparison.OrdinalIgnoreCase))
            {
                toastWindows[window] = true;
                if (NativeMethods.IsWindowVisible(window)) samples.Enqueue((watch.ElapsedMilliseconds, true));
            }
        }
        // EnumWindows excludes non-desktop app windows on Windows 8 and later. Observe
        // shell events as well, otherwise a UWP-hosted banner can make the control blind.
        using var hookThread = new HookThread("Native toast test observer");
        IntPtr eventHook = IntPtr.Zero;
        NativeMethods.WinEventProc callback = (_, kind, window, objectId, childId, _, _) =>
        {
            if (kind is not (0x8002 or 0x800B or 0x800C) || childId != 0 || objectId is not (0 or -4)) return;
            var rootWindow = GetAncestor(window, 2);
            ObserveShellWindow(rootWindow == IntPtr.Zero ? window : rootWindow);
        };
        Check(hookThread.Start(() =>
        {
            eventHook = NativeMethods.SetWinEventHook(0x8002, 0x800C, IntPtr.Zero, callback, 0, 0, 2);
            NativeMethods.EnumWindows((window, _) => { ObserveShellWindow(window); return true; }, IntPtr.Zero);
            return eventHook;
        }, () => { if (eventHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(eventHook); }), "实机横幅观察器已启动");
        using var cancel = new CancellationTokenSource();
        Task observer = Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                bool native = toastWindows.Keys.Any(NativeMethods.IsWindowVisible);
                samples.Enqueue((watch.ElapsedMilliseconds, native));
                await Task.Delay(10);
            }
        });
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.GetFullPath("tools/Test-WindowsNotification.ps1"), "-Title", title, "-Count", "1" }) info.ArgumentList.Add(argument);
        using var sender = Process.Start(info)!;
        var root = nativeOnly ? null : AutomationElement.FromHandle(island);
        var condition = new AndCondition(new PropertyCondition(AutomationElement.NameProperty, expectedTitle), new PropertyCondition(AutomationElement.IsOffscreenProperty, false));
        bool sawIsland = false;
        bool sawSystemTest = false;
        var islandSamples = new List<long>();
        while (watch.ElapsedMilliseconds < 6000)
        {
            if (root?.FindFirst(TreeScope.Descendants, condition) != null) { sawIsland = true; islandSamples.Add(watch.ElapsedMilliseconds); }
            if (nativeOnly && !sawSystemTest)
                foreach (var window in toastWindows.Keys.Where(NativeMethods.IsWindowVisible))
                    try { if (AutomationElement.FromHandle(window).FindFirst(TreeScope.Descendants, condition) != null) sawSystemTest = true; }
                    catch (ElementNotAvailableException) { }
            await Task.Delay(20);
        }
        cancel.Cancel(); await observer; await sender.WaitForExitAsync();
        Check(sender.ExitCode == 0, "测试通知成功发送");
        bool overlap = islandSamples.Any(time => samples.Any(s => s.Native && Math.Abs(s.Time - time) <= 20));
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { SawIsland = sawIsland, SawSystemTest = sawSystemTest, NativeVisibleSamples = samples.Count(s => s.Native), Overlap = overlap, ShellSurfaces = shellWindows.Values }));
        if (nativeOnly)
        {
            Check(samples.Any(s => s.Native), "对照测试能捕获实际 Windows 通知横幅");
            Check(sawSystemTest, "对照横幅包含本次唯一测试标题，排除通知中心等其他窗口");
            Directory.CreateDirectory(Path.GetDirectoryName(calibration)!);
            File.WriteAllText(calibration, System.Text.Json.JsonSerializer.Serialize(new { NativeControlVerified = true, Time = DateTime.UtcNow }));
            return;
        }
        Check(sawIsland, "岛确实显示了本次唯一测试通知");
        Check(!overlap, "岛显示同一通知期间，原 Windows 横幅不可见");
    }
}
