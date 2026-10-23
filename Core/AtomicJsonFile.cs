using System.IO;
using System.Text;
using System.Text.Json;

namespace alpha_island.Core;

/// <summary>同目录原子替换；旧版本作为备份保留。失败不会截断原文件。</summary>
internal static class AtomicJsonFile
{
    public static void Write(string path, string json, bool keepBackup = true) => WriteForFileSystem(path, json, keepBackup, File.Replace);

    internal static void WriteForFileSystem(string path, string json, bool keepBackup, Action<string, string, string?> replace)
    {
        // 先验证内容，避免错误调用覆盖有效文件。
        using var document = JsonDocument.Parse(json);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(json);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            CommitTemporary(temporary, fullPath, keepBackup ? fullPath + ".bak" : null, replace);
        }
        finally
        {
            TryDeleteTemporary(temporary);
        }
    }

    internal static void CommitTemporary(string temporary, string destination, string? backup, Action<string, string, string?>? replace = null)
    {
        if (!File.Exists(destination)) { File.Move(temporary, destination); return; }
        try { (replace ?? File.Replace)(temporary, destination, backup); }
        catch (Exception ex) when (ex is PlatformNotSupportedException || ex is IOException io && (io.HResult & 0xffff) is 1 or 50 or 120)
        {
            // Some removable/network file systems cannot implement ReplaceFile.
            // Back up first; a denied backup or move must leave the current file intact.
            if (backup != null) File.Copy(destination, backup, overwrite: true);
            File.Move(temporary, destination, overwrite: true);
        }
    }

    internal static void TryDeleteTemporary(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    public static T? Read<T>(string path, JsonSerializerOptions? options = null, int maximumBytes = 8 * 1024 * 1024)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (stream.Length > maximumBytes) throw new InvalidDataException("本机配置文件超过容量限制");
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return JsonSerializer.Deserialize<T>(reader.ReadToEnd(), options);
            }
            catch (IOException) when (attempt < 2) { Thread.Sleep(20); }
        }
    }
}
