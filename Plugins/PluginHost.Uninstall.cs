using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using alpha_island.Core;

namespace alpha_island.Plugins;

internal sealed partial class PluginHost
{
    internal bool Uninstall(Entry entry, out string error)
    {
        _dispatcher.VerifyAccess();
        error = "";
        if (_disposed || !_entries.Contains(entry))
        { error = "插件已不在当前列表中，请刷新后重试。"; return false; }

        // 先停用、取消任务和撤下卡片，再删除文件；失败后也不重新启动。
        entry.UninstallPending = true;
        if (entry.Manifest != null)
            _options.Extras[EnabledKey(entry.Manifest.Id)] = JsonSerializer.SerializeToElement(false);
        try
        {
            Stop(entry);
            entry.Attempted = false;
            string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.Directory));
            string? parent = Path.GetDirectoryName(directory);
            if (!_roots.Any(root => string.Equals(parent, Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("只能卸载插件目录中的独立子文件夹。");

            // 拒绝目录链接，避免递归删除跳转到插件之外的文件。
            for (var ancestor = new DirectoryInfo(parent!); ancestor != null; ancestor = ancestor.Parent)
                if (ancestor.Exists) VerifyOrdinaryPath(ancestor.FullName);
            FileAttributes? attributes;
            try { attributes = File.GetAttributes(directory); }
            catch (DirectoryNotFoundException) { attributes = null; }
            catch (FileNotFoundException) { attributes = null; }
            if (attributes != null)
            {
                VerifyOrdinaryPath(directory);
                if ((attributes & FileAttributes.Directory) == 0) throw new IOException("插件目录已被替换为文件，请刷新后重试。");
                VerifyTree(directory);
                DeleteTree(directory, keepManifestUntilLast: true);
            }
            _entries.Remove(entry);
            if (entry.Manifest != null) ForgetSettings(_options, entry.Manifest.Id);
            IslandLog.Info("Plugins", "已卸载 " + (entry.Manifest?.Id ?? Path.GetFileName(directory)));
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            error = "插件已停用，文件未完全删除。" + ex.GetBaseException().Message;
            entry.UninstallError = error;
            IslandLog.Warn("Plugins", "卸载失败：" + error);
            Changed?.Invoke();
            return false;
        }
    }

    internal static void ForgetSettings(IslandOptions options, string id)
    {
        string prefix = "plugin/" + id + "/";
        foreach (string key in options.Extras.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            options.Extras.Remove(key);
    }

    private static void VerifyOrdinaryPath(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("目录中含有文件或目录链接，请先移除链接后重试。");
    }

    private static void VerifyTree(string directory)
    {
        VerifyOrdinaryPath(directory);
        foreach (string path in Directory.GetFileSystemEntries(directory))
        {
            VerifyOrdinaryPath(path);
            if (Directory.Exists(path)) VerifyTree(path);
        }
    }

    private static void DeleteTree(string directory, bool keepManifestUntilLast = false)
    {
        VerifyOrdinaryPath(directory);
        string manifest = Path.Combine(directory, "plugin.json");
        foreach (string path in Directory.GetFileSystemEntries(directory))
        {
            if (keepManifestUntilLast && string.Equals(path, manifest, StringComparison.OrdinalIgnoreCase)) continue;
            VerifyOrdinaryPath(path);
            if (Directory.Exists(path)) DeleteTree(path);
            else DeleteFile(path);
        }
        if (keepManifestUntilLast && File.Exists(manifest))
        { VerifyOrdinaryPath(manifest); DeleteFile(manifest); }
        Directory.Delete(directory);
    }

    private static void DeleteFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }
}
