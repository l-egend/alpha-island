using System.IO;

namespace alpha_island.Conversations;

/// <summary>同目录写入并原子替换，写入失败不会截断原文件；密钥不保留旧密文备份。</summary>
internal static class AtomicBinaryFile
{
    public static void Write(string path, byte[] bytes, bool keepBackup = false)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            AtomicJsonFile.CommitTemporary(temporary, fullPath, keepBackup ? fullPath + ".bak" : null);

        }
        finally { AtomicJsonFile.TryDeleteTemporary(temporary); }
    }
}
