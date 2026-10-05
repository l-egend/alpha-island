using System.IO;
using System.Text.Json.Nodes;
using alpha_island.Services;

namespace alpha_island.Core;

/// <summary>全程序配置的目录布局与旧版本迁移；不搬动运行日志、历史记录、缓存或插件程序。</summary>
internal static class ConfigurationLayout
{
    internal static string ConfigDirectory(string root) => Path.Combine(root, "config");
    internal static string OptionsFile(string root) => Path.Combine(ConfigDirectory(root), "options.json");
    internal static string SecretFile(string root) => Path.Combine(ConfigDirectory(root), "ai", "secrets.dat");

    internal static string CodexFile(string root) => Path.Combine(ConfigDirectory(root), "codex-hooks.json");

    internal static void Migrate(string dataRoot, string? legacyCodexConfig = null)
    {
        string root = Path.GetFullPath(dataRoot);
        string config = ConfigDirectory(root);
        Directory.CreateDirectory(config);
        if ((File.GetAttributes(config) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("配置目录不能是符号链接或目录联接");

        string backups = Path.Combine(config, "backups");
        MoveFile(Path.Combine(root, "ai", "secrets.dat"), SecretFile(root), backups);

        // 老版本若曾把密钥写进 JSON，先加密成功，再移除明文。当前配置及密钥优先于旧文件。
        string primary = File.Exists(OptionsFile(root)) ? OptionsFile(root) : Path.Combine(root, "options.json");
        JsonObject? options = ReadOptions(primary) ?? ReadOptions(primary + ".bak");
        string? legacyKey = null;
        if (options?["Extras"] is JsonObject extras && extras["AiApiKey"] is JsonValue key) key.TryGetValue(out legacyKey);
        if (!string.IsNullOrWhiteSpace(legacyKey) && !File.Exists(SecretFile(root))
            && !AiSecretStore.TrySave(SecretFile(root), legacyKey))
            throw new IOException("旧密钥加密失败，保留原配置文件");

        MoveFile(Path.Combine(root, "options.json"), OptionsFile(root), backups);
        MoveFile(Path.Combine(root, "options.json.bak"), OptionsFile(root) + ".bak", backups);
        foreach (string file in Directory.EnumerateFiles(root, "options*.json*"))
            MoveFile(file, Path.Combine(backups, Path.GetFileName(file)), backups);
        foreach (string file in Directory.EnumerateFiles(config, "options*.json*")) SanitizeOptions(file);
        if (Directory.Exists(backups))
            foreach (string file in Directory.EnumerateFiles(backups, "*options*.json*")) SanitizeOptions(file);

        MoveFile(Path.Combine(root, "icon.ico"), Path.Combine(config, "icon.ico"), backups);
        MergeIcons(Path.Combine(root, "icons"), Path.Combine(config, "icons"), root, backups);
        MigrateCodexConfiguration(root, legacyCodexConfig);
    }

    private static void MigrateCodexConfiguration(string root, string? legacyPath)
    {
        if (string.IsNullOrWhiteSpace(legacyPath) || !File.Exists(legacyPath)) return;
        if ((File.GetAttributes(legacyPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("旧接入配置不能是符号链接");
        var oldConfig = ReadOptions(legacyPath);
        if (oldConfig == null) throw new IOException("旧接入配置不是有效的 JSON 对象");
        string target = CodexFile(root);
        if (oldConfig["configPath"] is JsonValue pointer)
        {
            if (!File.Exists(target)) throw new IOException("接入配置指针的目标缺失，请重新安装接入脚本");
            if (pointer.TryGetValue<string>(out var value) && string.Equals(value, target, StringComparison.OrdinalIgnoreCase)) return;
        }
        else
        {
            if (!File.Exists(target)) AtomicJsonFile.Write(target, oldConfig.ToJsonString(IslandOptions.JsonOptions), keepBackup: false);
            else AtomicJsonFile.Write(Path.Combine(ConfigDirectory(root), "backups", "codex-hooks.legacy.json"), oldConfig.ToJsonString(IslandOptions.JsonOptions), keepBackup: false);
        }
        // 已被 Codex 信任的命令仍指向旧路径；那里只留下目录指针，实际配置统一存入 config。
        AtomicJsonFile.Write(legacyPath, new JsonObject { ["configPath"] = target }.ToJsonString(), keepBackup: false);
    }

    private static JsonObject? ReadOptions(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; }
        catch (System.Text.Json.JsonException) { return null; } // 损坏文件仍交给原有备份恢复逻辑处理。
    }

    private static void SanitizeOptions(string path)
    {
        var options = ReadOptions(path);
        if (options?["Extras"] is not JsonObject extras || extras["AiApiKey"] is not JsonValue key
            || !key.TryGetValue<string>(out string? value) || string.IsNullOrEmpty(value)) return;
        extras["AiApiKey"] = "";
        AtomicJsonFile.Write(path, options.ToJsonString(IslandOptions.JsonOptions), keepBackup: false);
    }

    private static void MoveFile(string source, string destination, string backups)
    {
        if (!File.Exists(source)) return;
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("旧配置不能是符号链接");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            Directory.CreateDirectory(backups);
            destination = Path.Combine(backups, "legacy-" + Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(source));
        }
        File.Move(source, destination);
    }

    private static void MergeIcons(string source, string destination, string root, string backups)
    {
        if (!Directory.Exists(source)) return;
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(source).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFullPath(destination).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("图标迁移路径必须位于应用数据目录内");
        if (!Directory.Exists(destination)) { Directory.Move(source, destination); return; }
        if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0) throw new IOException("图标目录不能是符号链接");
        foreach (string file in Directory.EnumerateFiles(source)) MoveFile(file, Path.Combine(destination, Path.GetFileName(file)), backups);
        foreach (string directory in Directory.EnumerateDirectories(source))
            MergeIcons(directory, Path.Combine(destination, Path.GetFileName(directory)), root, backups);
        Directory.Delete(source); // 仅删除已迁完的空目录。
    }
}
