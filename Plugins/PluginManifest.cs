using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AlphaIsland.PluginSdk;

namespace alpha_island.Plugins;

internal sealed record PluginManifest
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "1.0.0";
    public int ApiVersion { get; init; }
    public string Assembly { get; init; } = "";
    public string EntryType { get; init; } = "";
    public string Description { get; init; } = "";

    internal static bool ValidKey(string key) => Regex.IsMatch(key, "^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    internal static PluginManifest Read(string file)
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(file),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("插件清单为空");
        if (!ValidKey(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name) || !System.Version.TryParse(manifest.Version, out _))
            throw new InvalidDataException("插件 Id、名称或版本无效");
        if (manifest.ApiVersion != PluginApi.Version) throw new InvalidDataException($"插件需要 API {manifest.ApiVersion}，宿主提供 API {PluginApi.Version}");
        if (string.IsNullOrWhiteSpace(manifest.Assembly) || Path.GetFileName(manifest.Assembly) != manifest.Assembly
            || !manifest.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(manifest.EntryType))
            throw new InvalidDataException("程序集必须为插件目录内的 DLL 文件名，并指定入口类型");
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(file)!, manifest.Assembly))) throw new FileNotFoundException("插件 DLL 不存在");
        return manifest;
    }
}
