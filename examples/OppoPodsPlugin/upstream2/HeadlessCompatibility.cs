using System.Globalization;
using System.Reflection;
using System.Text.Json;
namespace AlphaIsland.EarbudsV2.Control.Subsystems.Logging
{
    // Host diagnostics replace the standalone application's files and settings.
    internal sealed class ApplicationLog
    {
        public static ApplicationLog? Current => null;
        public void Debug(string area, string message) { }
        public void Info(string area, string message) { }
        public void Error(string area, string message, Exception? error = null) { }
        public void Warning(string area, string message) { }
    }
}
namespace AlphaIsland.EarbudsV2.Assets.Localization
{
    internal static class TranslationCatalog
    {
        private static readonly Lazy<Dictionary<string, string>> Strings = new(() =>
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AlphaIsland.EarbudsV2.Assets.Localization.Strings.json")!;
            using var doc = JsonDocument.Parse(stream);
            var map = new Dictionary<string, string>();
            foreach (var item in doc.RootElement.EnumerateObject())
                if (item.Value.ValueKind == JsonValueKind.String) map[item.Name] = item.Value.GetString()!;
            return map;
        });
        public static string CurrentLanguage => "zh-Hans";
        public static string Get(string key, params object[] args)
        {
            var text = Strings.Value.GetValueOrDefault(key, key);
            return args.Length == 0 ? text : string.Format(CultureInfo.GetCultureInfo("zh-CN"), text, args);
        }
    }
}
