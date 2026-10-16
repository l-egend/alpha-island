using System.IO;
using System.Text;
using System.Text.Json;

namespace alpha_island.Conversations;

/// <summary>同目录原子替换；旧版本作为备份保留。失败不会截断原文件。</summary>
internal static class AtomicJsonFile
{
    public static void Write(string path, string json, bool keepBackup = true)
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

            if (File.Exists(fullPath))
                File.Replace(temporary, fullPath, keepBackup ? fullPath + ".bak" : null);
            else
                File.Move(temporary, fullPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static T? Read<T>(string path, JsonSerializerOptions? options = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), options); }
            catch (IOException) when (attempt < 2) { Thread.Sleep(20); }
        }
    }
}
