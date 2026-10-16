using System.Text.RegularExpressions;

namespace alpha_island.Core;

/// <summary>Detects one unambiguous web link without fetching it or executing custom URI schemes.</summary>
internal static class WebLinkDetector
{
    private static readonly Regex Links = new(
        @"https?://[^\s<>""\u0027，。；！？、]+|" +
        @"[\p{L}\p{N}][\p{L}\p{N}-]*(?:[.\u3002\uFF0E\uFF61][\p{L}\p{N}][\p{L}\p{N}-]*)+" +
        @"(?::[0-9]{1,5})?(?:[/?#][^\s<>""\u0027，。；！？、]*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    // A bare name such as report.pdf is ambiguous. Explicit HTTP(S) links still accept any valid host.
    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "csv", "rtf", "md",
        "jpg", "jpeg", "png", "gif", "bmp", "ico", "svg", "webp", "mp3", "wav", "mp4", "avi", "mkv",
        "zip", "rar", "7z", "tar", "gz", "exe", "dll", "msi", "bat", "cmd", "ps1", "lnk",
        "json", "xml", "yaml", "yml", "ini", "config", "cs", "cpp", "h", "py", "js", "ts", "css", "html", "log"
    };

    internal static Uri? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 32_768) return null;
        string candidate = text.Trim();
        if (TryWebUri(candidate) is Uri exact) return exact;
        if (!candidate.Any(char.IsWhiteSpace) && Uri.TryCreate(candidate, UriKind.Absolute, out var other)
            && other.Scheme != Uri.UriSchemeHttp && other.Scheme != Uri.UriSchemeHttps) return null;
        Uri? found = null;
        foreach (Match match in Links.Matches(candidate))
        {
            if (!IsLinkBoundary(candidate, match.Index) || !IsLinkEnd(candidate, match.Index + match.Length)) continue;
            string value = match.Value.TrimEnd('.', ',', ';', '!', '?', '，', '。', '；', '！', '？', '、');
            foreach (var (open, close) in new[] { ('(', ')'), ('[', ']'), ('{', '}'), ('（', '）') })
                while (value.EndsWith(close) && value.Count(c => c == close) > value.Count(c => c == open)) value = value[..^1];
            if (TryWebUri(value) is not Uri uri) continue;
            if (found != null) return null;
            found = uri;
        }
        return found;
    }

    internal static Uri? TryWebUri(string text)
    {
        if (text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || text.Contains('\\')) return null;
        bool explicitScheme = text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        if (!explicitScheme) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(uri.Host)
            || uri.UserInfo.Length > 0) return null;
        if (!explicitScheme && !IsBareDomain(uri)) return null;
        return uri;
    }

    private static bool IsBareDomain(Uri uri)
    {
        if (uri.HostNameType != UriHostNameType.Dns) return false;
        string host;
        try { host = uri.IdnHost.TrimEnd('.'); }
        catch (UriFormatException) { return false; }
        if (host.Length > 253) return false;
        var labels = host.Split('.');
        if (labels.Length < 2 || labels.Any(label => label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-'
            || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))) return false;
        string suffix = labels[^1];
        return suffix.Length >= 2 && suffix.Any(char.IsAsciiLetter) && !FileExtensions.Contains(suffix);
    }

    private static bool IsLinkBoundary(string text, int start)
    {
        if (start == 0) return true;
        char previous = text[start - 1];
        if (char.IsLetterOrDigit(previous) || previous is '_' or '@' or '/' or '\\' or '.' or '-') return false;
        if (previous != ':') return true;
        // Do not extract a web URL out of a custom protocol, e.g. javascript:https://example.com.
        int index = start - 2;
        while (index >= 0 && (char.IsAsciiLetterOrDigit(text[index]) || text[index] is '+' or '-' or '.')) index--;
        return index == start - 2;
    }

    private static bool IsLinkEnd(string text, int end)
        => end == text.Length || (!char.IsLetterOrDigit(text[end]) && text[end] is not (':' or '@' or '\\' or '_' or '-'));
}
