using System.Text.RegularExpressions;

namespace alpha_island.Services;

public sealed record SensitiveTextReport(bool Sensitive, string Kind, string Redacted);
/// <summary>Bounded local text classification shared by clipboard and API preflight. Never logs input.</summary>
public static class SensitiveTextDetector
{
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(35);
    private static readonly Regex Keys = new(@"\b(?:sk-(?:proj-|ant-)?[A-Za-z0-9_-]{20,}|AIza[A-Za-z0-9_-]{30,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[A-Z0-9]{16})\b|(?i:(?:api[_ -]?key|access[_ -]?token|secret[_ -]?key|bearer)\s*[=:：]?\s*[""']?)[A-Za-z0-9_./+\-=]{20,}", RegexOptions.CultureInvariant, Limit);
    private static readonly Regex Numbers = new(@"(?<!\d)(?:\d[ -]?){12,18}[\dXx](?!\d)", RegexOptions.CultureInvariant, Limit);
    public static SensitiveTextReport Inspect(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new(false, "", text ?? "");
        if (text.Length > 131072) return new(true, "超长文本（未扫描）", "[超长内容已隐藏]");
        string kind = "";
        try
        {
            string redacted = Keys.Replace(text, match => { kind = "API Key / 访问令牌"; return "[访问密钥已隐藏]"; });
            redacted = Numbers.Replace(redacted, match =>
            {
                string digits = match.Value.Replace(" ", "").Replace("-", "");
                bool id = IsIdentity(digits), card = !id && IsCard(digits);
                if (!id && !card) return match.Value;
                kind = kind.Length == 0 ? id ? "身份证号" : "银行卡号" : "多类敏感内容";
                return id ? "[身份证号已隐藏]" : "[银行卡号已隐藏]";
            });
            return new(kind.Length > 0, kind, redacted);
        }
        catch (RegexMatchTimeoutException) { return new(true, "复杂文本（扫描超时）", "[内容已隐藏]"); }
    }
    private static bool IsIdentity(string s)
    {
        if (s.Length != 18 || !s[..17].All(char.IsAsciiDigit) || s[0] == '0'
            || !DateOnly.TryParseExact(s.Substring(6, 8), "yyyyMMdd", out var date)
            || date.Year < 1900 || date > DateOnly.FromDateTime(DateTime.Today)) return false;
        int[] weights = [7, 9, 10, 5, 8, 4, 2, 1, 6, 3, 7, 9, 10, 5, 8, 4, 2];
        int sum = Enumerable.Range(0, 17).Sum(i => (s[i] - '0') * weights[i]);
        return char.ToUpperInvariant(s[17]) == "10X98765432"[sum % 11];
    }
    private static bool IsCard(string s)
    {
        if (s.Length is < 13 or > 19 || !s.All(char.IsAsciiDigit) || s[0] is < '3' or > '6' || s.Distinct().Count() < 2) return false;
        int sum = 0; bool doubled = false;
        for (int i = s.Length - 1; i >= 0; i--) { int n = s[i] - '0'; if (doubled) { n *= 2; if (n > 9) n -= 9; } sum += n; doubled = !doubled; }
        return sum % 10 == 0;
    }
}
