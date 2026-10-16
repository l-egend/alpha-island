using alpha_island.Core;
using alpha_island.Services;
using System.IO;
using System.Windows;
using Grid = System.Windows.Controls.Grid;

internal static partial class Program
{
    private static void ClipboardDomainUiChecks(alpha_island.MainWindow window, Grid root, string testRoot)
    {
        using var hotkeys = new HotkeyService(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        using var clipboard = new ClipboardService(System.Windows.Threading.Dispatcher.CurrentDispatcher, hotkeys,
            () => 12u, () => (string?)null, Path.Combine(testRoot, "clipboard-domain-history.json"));
        clipboard.Start();
        SetAgentField(window, "_clipboard", clipboard);
        List<Uri> opened = [];
        SetAgentField(window, "_openClipboardWebLink", new Action<Uri>(opened.Add));
        var options = AgentField<IslandOptions>(window, "_options");
        SettingsRegistry.Write(options, "ClipboardLinkDetectionEnabled", true);
        clipboard.ContentCopied += (sender, e) => PrivateCall(window, "OnClipboardContentCopied", sender, e);
        clipboard.CopiedDismissed += (sender, e) => PrivateCall(window, "OnClipboardCopiedDismissed", sender, e);
        void Copy(string value)
        {
            clipboard.ProcessClipboardData(new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText, value), false);
            PumpAnimations(350);
            root.UpdateLayout();
        }
        try
        {
            Copy("example.com/path?q=hello#section");
            Check(window.ClipboardStatusTitle.Text == "点击打开链接" && window.ClipboardModeBadgeText.Text == "链接",
                "裸域名经过真实剪切板通知入口显示链接提示");
            Check((bool)PrivateCall(window, "TryOpenClipboardWebLink", new Uri("https://example.com/path?q=hello#section"))!
                && opened.Single().AbsoluteUri == "https://example.com/path?q=hello#section",
                "裸域名点击打开完整地址并释放剪切板驻留");
            Copy("例子.中国/中文路径");
            Check(window.ClipboardStatusTitle.Text == "点击打开链接" && clipboard.CurrentWebLink?.IdnHost == "xn--fsqu00a.xn--fiqs8s",
                "中文域名通过真实岛入口显示链接提示");
            Copy("user@example.com");
            Check(window.ClipboardStatusTitle.Text == "已复制" && clipboard.CurrentWebLink == null,
                "邮箱在真实岛入口保持普通复制提示");
            Copy("C:\\work\\example.com");
            Check(window.ClipboardStatusTitle.Text == "已复制" && clipboard.CurrentWebLink == null,
                "文件路径在真实岛入口保持普通复制提示");
        }
        finally
        {
            clipboard.DismissCopiedPending();
            SetAgentField(window, "_clipboard", null);
            SetAgentField(window, "_clipboardLocked", false);
        }
    }
}
