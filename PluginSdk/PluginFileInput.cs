using System.IO;

namespace AlphaIsland.PluginSdk;

public interface IPluginFileInput
{
    PluginFileInputResult AttachFiles(IReadOnlyList<string> paths);
}

public sealed record PluginFileInputResult(bool Accepted, string Message, int Count = 0);

public static class PluginFileInput
{
    public const int MaximumFiles = 16;
    public static string[] Validate(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count is 0 or > MaximumFiles) throw new ArgumentException("一次可附加 1～16 个文件或文件夹。");
        var valid = new List<string>();
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 32767 || path.IndexOfAny(['\0', '\r', '\n']) >= 0)
                throw new ArgumentException("附件路径无效，请重新添加。");
            string full = Path.GetFullPath(path);
            if (!File.Exists(full) && !Directory.Exists(full)) throw new IOException("附件已移动或删除，请重新添加。");
            if (!valid.Contains(full, StringComparer.OrdinalIgnoreCase)) valid.Add(full);
        }
        return valid.ToArray();
    }
}
