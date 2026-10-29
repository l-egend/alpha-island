namespace alpha_island.Core;

internal sealed record ExternalTranslationOptions(bool Enabled, string Name, string Shortcut)
{
    internal const string DefaultName = "";
    internal const string DefaultShortcut = "";

    internal static ExternalTranslationOptions From(IslandOptions options) => new(
        SettingsRegistry.GetBool(options, "ExternalTranslationEnabled", false),
        SettingsRegistry.GetText(options, "ExternalTranslationName", DefaultName).Trim(),
        SettingsRegistry.GetText(options, "ExternalTranslationShortcut", DefaultShortcut).Trim());

    internal string DisplayName => string.IsNullOrWhiteSpace(Name) ? "外部翻译软件" : Name;
    internal bool IsConfigured => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Shortcut);
}
