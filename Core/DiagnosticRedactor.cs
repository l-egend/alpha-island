using System.Text.RegularExpressions;

namespace alpha_island.Core;

internal static class DiagnosticRedactor
{
    private const string Hidden = "[已隐藏]";
    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));
    private static readonly Regex PrivateKey = Pattern(@"-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?-----END [^-]*PRIVATE KEY-----");
    private static readonly Regex Credential = Pattern(@"((?:[""']?)(?:api[_-]?key|access[_-]?key|access[_-]?token|refresh[_-]?token|token|secret|password|passwd|authorization|cookie)(?:[""']?)\s*[:=]\s*)(?:\[已隐藏\]|""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;}&\]]+)");
    private static readonly Regex Bearer = Pattern(@"\b(?:Bearer|Basic)\s+[^\s""',;]+|\bsk-[A-Za-z0-9_-]{8,}|\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+");
    private static readonly Regex Argument = Pattern(@"(--(?:api[_-]?key|token|secret|password|authorization)\s+)(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s]+)");
    private static readonly Regex Payload = Pattern(@"((?:[""']?)(?:prompt|persona|transcript|clipboard[_-]?(?:text|content)|消息正文|剪贴板正文|文本)(?:[""']?)\s*[:=]\s*)[^\r\n]*");
    private static readonly Regex Url = Pattern(@"https?://[^\s""'<>]+");
    private static readonly Regex Path = Pattern(@"(?<![A-Za-z0-9_])(?:[A-Z]:[\\/]|\\\\[^\\/\s]+[\\/])[^""'<>\r\n|,;]*");
    private static readonly Regex UnixHome = Pattern(@"/(?:home|Users)/[^\s""'<>]+|file:///[^\s""'<>]+");
    private static readonly Regex Address = Pattern(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b|\b(?:[0-9A-F]{2}[:-]){5}[0-9A-F]{2}\b|\b(?:\d{1,3}\.){3}\d{1,3}\b");

    internal static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        try
        {
            string text = value.Length > 2_097_152 ? value[..2_097_152] + "\n（诊断内容已截断）" : value;
            text = PrivateKey.Replace(text, Hidden);

            text = Bearer.Replace(text, Hidden);
            text = Argument.Replace(text, "$1" + Hidden);
            text = Payload.Replace(text, "$1" + Hidden);
            text = Credential.Replace(text, "$1" + Hidden);
            text = Url.Replace(text, match =>
            {
                if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)) return "[网址已隐藏]";
                var builder = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" };
                return builder.Uri.AbsoluteUri;
            });
            text = Path.Replace(text, match =>
            {
                var line = Regex.Match(match.Value, @":\s*(?:line|行号)\s*\d+\s*$", RegexOptions.IgnoreCase);
                return "[路径已隐藏]" + (line.Success ? line.Value : "");
            });
            text = UnixHome.Replace(text, "[路径已隐藏]");
            text = Address.Replace(text, match => (match.Value is "127.0.0.1" or "0.0.0.0" || match.Value.StartsWith("0.", StringComparison.Ordinal)) ? match.Value : "[地址已隐藏]");
            string user = Environment.UserName;
            if (user.Length >= 3) text = Regex.Replace(text, @"(?<![\w])" + Regex.Escape(user) + @"(?![\w])", "[用户]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            return text;
        }
        catch (RegexMatchTimeoutException) { return "（该段内容超出安全脱敏处理范围，已省略）"; }
        catch (UriFormatException) { return "（该段包含无法安全处理的网址，已省略）"; }
    }
}
