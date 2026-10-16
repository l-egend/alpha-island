namespace alpha_island.Core;

internal sealed record ExternalTranslationOptions(bool Enabled, string Name, string Shortcut)
{
    internal const string DefaultName = "网易有道翻译";
    internal const string DefaultShortcut = "Ctrl+Alt+D";

    internal static ExternalTranslationOptions From(IslandOptions options) => new(
        SettingsRegistry.GetBool(options, "ExternalTranslationEnabled", true),
        SettingsRegistry.GetText(options, "ExternalTranslationName", DefaultName).Trim(),
        SettingsRegistry.GetText(options, "ExternalTranslationShortcut", DefaultShortcut).Trim());

    internal string DisplayName => string.IsNullOrWhiteSpace(Name) ? "外部翻译软件" : Name;
}
