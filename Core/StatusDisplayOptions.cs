namespace alpha_island.Core;

internal static class StatusDisplayOptions
{
    internal static readonly string[] All = ["upload", "download", "cpu", "memory", "power"];
    internal const string DefaultOrder = "upload,download,cpu,memory,power";
    internal static string Label(string key) => key switch
    {
        "upload" => "上传速度", "download" => "下载速度", "cpu" => "CPU 占用",
        "memory" => "内存占用", "power" => "CPU＋GPU 功耗", _ => key
    };
    internal static string SettingKey(string key) => "StatusShow" + char.ToUpperInvariant(key[0]) + key[1..];
    internal static string[] Order(string? value) => (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Where(All.Contains).Concat(All).Distinct(StringComparer.Ordinal).ToArray();
    internal static string[] Selected(IslandOptions options) => Order(SettingsRegistry.GetText(options, "StatusDisplayOrder", DefaultOrder))
        .Where(key => SettingsRegistry.GetBool(options, SettingKey(key), true)).ToArray();
    internal static double Width(int count, bool compact = false) => Math.Max(1, (count + 1) / 2) switch
    {
        1 => compact ? 108 : 126, 2 => compact ? 176 : 204, _ => compact ? 256 : 292
    };
}
