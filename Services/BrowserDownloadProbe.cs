using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using alpha_island.Interop;

namespace alpha_island.Services;

internal sealed record BrowserDownloadReading(string Browser, string Name, long ReceivedBytes, long TotalBytes,
    bool Paused = false, string Path = "");

/// <summary>Read the browser's accessible download UI in a bounded child process, never on the island UI thread.</summary>
internal static class BrowserDownloadProbe
{
    private static readonly Regex Sizes = new(@"(?<r>[\d.,]+)\s*(?<ru>[KMGT]?B|字节)\s*/\s*(?<t>[\d.,]+)\s*(?<tu>[KMGT]?B|字节)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FileName = new(@"^[^<>:/\\|?*\r\n]{1,170}\.[a-zA-Z0-9]{1,12}$", RegexOptions.Compiled);

    internal static string? BrowserForProcess(string name) => name.ToLowerInvariant() switch
    {
        "msedge" => "Edge", "chrome" => "Chrome", "brave" => "Brave", "vivaldi" => "Vivaldi",
        "opera" => "Opera", "chromium" => "Chromium", _ => null
    };

    internal static List<BrowserDownloadReading> Read()
    {
        bool running = false;
        foreach (var process in Process.GetProcesses())
        {
            using (process) { try { running |= BrowserForProcess(process.ProcessName) != null; } catch { } }
        }
        if (!running) return [];
        string executable = System.IO.Path.Combine(AppContext.BaseDirectory, "alpha-island.exe");
        if (!File.Exists(executable) && System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "AlphaIsland.RegressionTests")
            executable = Environment.ProcessPath!;
        if (!File.Exists(executable)) return [];
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--read-browser-downloads");
            using var child = Process.Start(start);
            if (child == null) return [];
            var output = child.StandardOutput.ReadToEndAsync();
            _ = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(2400)) { child.Kill(); child.WaitForExit(500); return []; }
            return child.ExitCode == 0 ? JsonSerializer.Deserialize<List<BrowserDownloadReading>>(output.GetAwaiter().GetResult()) ?? [] : [];
        }
        catch { return []; }
    }

    internal static List<BrowserDownloadReading> ScanWindows()
    {
        List<BrowserDownloadReading> result = [];
        var clock = Stopwatch.StartNew();
        NativeMethods.EnumWindows((handle, _) =>
        {
            if (clock.ElapsedMilliseconds > 1000 || result.Count >= 32) return false;
            if (!NativeMethods.IsWindowVisible(handle)) return true;
            NativeMethods.GetWindowThreadProcessId(handle, out uint pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                string? browser = BrowserForProcess(process.ProcessName);
                if (browser == null) return true;
                var root = AutomationElement.FromHandle(handle);
                // Restrict extraction to short visible labels. Never store page URLs or browser history.
                var elements = root.FindAll(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
                    new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Header),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ProgressBar))));
                List<string> labels = [];
                if (root.Current.Name is "下载" or "Downloads") labels.Add(root.Current.Name);
                foreach (AutomationElement element in elements)
                {
                    if (labels.Count >= 1000) break;
                    string name = element.Current.Name.Trim();
                    if (name.Length is > 0 and <= 250) labels.Add(name);
                }
                result.AddRange(ParseLabels(browser, labels));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return result.DistinctBy(x => (x.Browser, x.Name)).Take(32).ToList();
    }

    internal static List<BrowserDownloadReading> ParseLabels(string browser, IReadOnlyList<string> labels)
    {
        List<BrowserDownloadReading> result = [];
        if (!labels.Any(x => x.Equals("下载", StringComparison.OrdinalIgnoreCase)
            || x.Equals("Downloads", StringComparison.OrdinalIgnoreCase))) return result;
        for (int i = 0; i < labels.Count; i++)
        {
            var match = Sizes.Match(labels[i]);
            if (!match.Success) continue;
            if (!labels[i].Contains("B/s", StringComparison.OrdinalIgnoreCase) && !labels[i].Contains("剩余")
                && !labels[i].Contains("remaining", StringComparison.OrdinalIgnoreCase)
                && !labels[i].Contains("暂停") && !labels[i].Contains("paused", StringComparison.OrdinalIgnoreCase)) continue;
            long received = Bytes(match.Groups["r"].Value, match.Groups["ru"].Value);
            long total = Bytes(match.Groups["t"].Value, match.Groups["tu"].Value);
            if (total <= 0 || received < 0 || received > total) continue;
            string? name = null;
            for (int j = i - 1; j >= Math.Max(0, i - 8); j--)
                if (FileName.IsMatch(labels[j])) { name = labels[j]; break; }
            if (name == null) continue;
            bool paused = labels[i].Contains("暂停", StringComparison.OrdinalIgnoreCase)
                || labels[i].Contains("paused", StringComparison.OrdinalIgnoreCase);
            result.Add(new(browser, name, received, total, paused));
        }
        return result;
    }

    private static long Bytes(string value, string unit)
    {
        value = Regex.IsMatch(value, @"^\d{1,3}(?:,\d{3})+$") ? value.Replace(",", "")
            : value.Contains('.') ? value.Replace(",", "") : value.Replace(",", ".");
        if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double number)
            || !double.IsFinite(number) || number < 0) return -1;
        int exponent = unit.ToUpperInvariant() switch { "KB" => 1, "MB" => 2, "GB" => 3, "TB" => 4, _ => 0 };
        double bytes = number * Math.Pow(1024, exponent);
        return bytes < long.MaxValue ? (long)Math.Round(bytes) : -1;
    }
}

/// <summary>Identify a partial file's actual browser process. Query only; never shut down/restart any application.</summary>
internal static class BrowserPartialOwner
{
    [StructLayout(LayoutKind.Sequential)] private struct UniqueProcess { public uint Id; public System.Runtime.InteropServices.ComTypes.FILETIME Start; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Service;
        public uint Type, Status, Session;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint session, uint count, string[] files, uint apps, IntPtr processes, uint services, IntPtr names);
    [DllImport("rstrtmgr.dll")] private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[]? entries, out uint reasons);
    [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint session);

    internal static string? Read(string path)
    {
        if (RmStartSession(out uint session, 0, new StringBuilder(33)) != 0) return null;
        try
        {
            if (RmRegisterResources(session, 1, [path], 0, IntPtr.Zero, 0, IntPtr.Zero) != 0) return null;
            uint count = 0;
            int status = RmGetList(session, out uint needed, ref count, null, out _);
            if (status != 234 || needed == 0 || needed > 64) return null;
            var entries = new ProcessInfo[needed]; count = needed;
            if (RmGetList(session, out _, ref count, entries, out _) != 0) return null;
            List<string> browsers = [];
            foreach (var entry in entries.Take((int)count))
            {
                try { using var process = Process.GetProcessById((int)entry.Process.Id);
                    if (BrowserDownloadProbe.BrowserForProcess(process.ProcessName) is { } browser) browsers.Add(browser); }
                catch { }
            }
            var distinct = browsers.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return distinct.Length == 1 ? distinct[0] : null;
        }
        finally { RmEndSession(session); }
    }
}
