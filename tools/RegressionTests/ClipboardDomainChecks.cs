using alpha_island.Core;
using alpha_island.Interop;

internal static partial class Program
{
    private static void ClipboardDomainChecks()
    {
        Check(WebLinkDetector.Detect("example.com")?.AbsoluteUri == "https://example.com/",
            "剪切板裸域名补齐 HTTPS");
        Check(WebLinkDetector.Detect("www.example.com/path")?.AbsoluteUri == "https://www.example.com/path",
            "www 地址继续保持默认 HTTPS");
        Check(WebLinkDetector.Detect("Example.COM:8443/path?q=中文#part")?.AbsoluteUri
            == new Uri("https://Example.COM:8443/path?q=中文#part").AbsoluteUri,
            "无协议域名保留端口、路径、查询与片段");
        Check(WebLinkDetector.Detect("例子.中国/中文路径")?.IdnHost == "xn--fsqu00a.xn--fiqs8s"
            && WebLinkDetector.Detect("例子.中国/中文路径")?.AbsolutePath == new Uri("https://例子.中国/中文路径").AbsolutePath,
            "中文域名与中文路径可识别");
        Check(WebLinkDetector.Detect("例子。中国")?.IdnHost == "xn--fsqu00a.xn--fiqs8s"
            && WebLinkDetector.Detect("example.ai/path")?.AbsoluteUri == "https://example.ai/path"
            && WebLinkDetector.Detect("example.co.uk/path")?.AbsoluteUri == "https://example.co.uk/path",
            "国际化域名点号与常见多级后缀可识别");
        Check(WebLinkDetector.Detect("查看官网：example.com/path。")?.AbsoluteUri == "https://example.com/path",
            "正文内裸域名去除中文句末标点");
        Check(WebLinkDetector.Detect("(example.com/a(b))")?.AbsoluteUri == "https://example.com/a(b)",
            "包围括号去除但保留路径自身括号");
        Check(WebLinkDetector.Detect("example.com www.other.com") == null,
            "混合多个域名不误选一个");
        Check(WebLinkDetector.Detect("https://one.example other.example") == null,
            "带协议与裸域名混合时也检查歧义");
        Check(WebLinkDetector.Detect("user@example.com") == null
            && WebLinkDetector.Detect("联系 user@example.com 获取帮助") == null,
            "邮箱与正文里的邮箱不被误认成网站");
        Check(WebLinkDetector.Detect("C:\\work\\example.com") == null
            && WebLinkDetector.Detect("\\\\server\\example.com") == null
            && WebLinkDetector.Detect("/home/user/example.com") == null
            && WebLinkDetector.Detect("打开 C:\\work\\example.com 或等待") == null,
            "Windows、UNC 与 Unix 文件路径不被误认成域名");
        Check(WebLinkDetector.Detect("file:///C:/example.com") == null
            && WebLinkDetector.Detect("mailto:user@example.com") == null
            && WebLinkDetector.Detect("javascript:https://example.com") == null
            && WebLinkDetector.Detect("跳转 javascript:https://example.com") == null,
            "非网页协议即使包裹在正文中也不打开");
        Check(WebLinkDetector.Detect("readme.txt") == null && WebLinkDetector.Detect("photo.jpg") == null
            && WebLinkDetector.Detect("archive.zip") == null && WebLinkDetector.Detect("report.pdf") == null,
            "常见文件名不被裸域名识别接管");
        Check(WebLinkDetector.Detect("1.2.3") == null && WebLinkDetector.Detect("v1.2.3") == null
            && WebLinkDetector.Detect("a..com") == null && WebLinkDetector.Detect("-bad.com") == null
            && WebLinkDetector.Detect("bad-.com") == null,
            "版本号与无效域名标签不识别");
        Check(WebLinkDetector.Detect("https://example.com/path?q=one%20two#section")?.AbsoluteUri
            == "https://example.com/path?q=one%20two#section",
            "已有完整链接行为保持兼容");
        Check(WebLinkDetector.Detect("普通文本和 192.168.1.2") == null,
            "普通文本与无协议 IP 不被扩大识别误接管");
        Check(WebLinkDetector.Detect("访问 example.com:333333/path") == null
            && WebLinkDetector.Detect("访问 example.com:not-a-port/path") == null
            && WebLinkDetector.Detect("账号 first.example.com@second.example.com") == null,
            "无效端口与邮箱不被局部截断为有效域名");
        var uri = WebLinkDetector.Detect("example.com/path?q=hello#section")!;
        var launch = DefaultBrowserLauncher.CreateStartInfo(uri);
        Check(launch.UseShellExecute && launch.Verb == "open" && launch.FileName == uri.AbsoluteUri
            && string.IsNullOrEmpty(launch.Arguments), "补齐后的域名仍使用系统默认浏览器关联打开");
        bool rejected = false;
        try { DefaultBrowserLauncher.CreateStartInfo(new Uri("example.com:8443")); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "浏览器入口仅接受已经补齐的 HTTP(S) Uri，拒绝域名被解析为自定义协议");
    }
}
