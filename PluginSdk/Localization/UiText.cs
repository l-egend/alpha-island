using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AlphaIsland.PluginSdk.Localization;

/// <summary>Local interface text shared by the host and isolated plugins. Never translates user data.</summary>
public static class UiText
{
    public const string SystemLanguage = "system";
    public const string Chinese = "zh-CN";
    public const string English = "en-US";
    private static readonly CultureInfo WindowsDisplayCulture = CultureInfo.GetCultureInfo(CultureInfo.CurrentUICulture.Name);
    private static readonly Lazy<Catalog> Strings = new(LoadCatalog);
    private static string _language = Chinese;
    public static string Language => Volatile.Read(ref _language);
    public static bool IsEnglish => Language == English;
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language);

    public static string NormalizeSelection(string? selection) => selection switch
    {
        Chinese or "zh" => Chinese,
        English or "en" => English,
        _ => SystemLanguage
    };

    public static string ResolveLanguage(string? selection, CultureInfo? systemCulture = null)
    {
        string normalized = NormalizeSelection(selection);
        if (normalized != SystemLanguage) return normalized;
        return (systemCulture ?? WindowsDisplayCulture).TwoLetterISOLanguageName == "zh" ? Chinese : English;
    }

    public static void Initialize(string? selection)
    {
        string language = ResolveLanguage(selection);
        Volatile.Write(ref _language, language);
        // UI language must not change numeric input, file formats or network serialization.
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(language);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
    }

    public static string T(string? source)
    {
        if (string.IsNullOrEmpty(source)) return source ?? "";
        return IsEnglish && Strings.Value.English.TryGetValue(source, out string? translated) ? translated : source;
    }

    public static string F(FormattableString source) => string.Format(Culture, T(source.Format), source.GetArguments());
    public static string ByKey(string key) => Strings.Value.Sources.TryGetValue(key, out string? source) ? T(source) : key;
    public static string KeyFor(string source) => "S" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..12];
    public static IReadOnlyDictionary<string, string> EnglishStrings => Strings.Value.English;

    private static Catalog LoadCatalog()
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream("AlphaIsland.Localization.en.json")
            ?? throw new InvalidOperationException("Missing interface language catalog.");
        var english = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidDataException("Invalid interface language catalog.");
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string source in english.Keys)
            if (!sources.TryAdd(KeyFor(source), source)) throw new InvalidDataException("Duplicate localization key.");
        return new(english, sources);
    }

    private sealed record Catalog(Dictionary<string, string> English, Dictionary<string, string> Sources);
}
