using System.IO;
using alpha_island.Core;

namespace alpha_island.Services;

internal sealed class LauncherApplicationIndex
{
    internal async Task<LauncherEntry[]> ScanAsync(CancellationToken token) => await Task.Run(() =>
    {
        var entries = new Dictionary<string, LauncherEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) })
        {
            if (root.Length == 0 || !Directory.Exists(root)) continue;
            var directories = new Queue<(string Path, int Depth)>(); directories.Enqueue((root, 0));
            while (directories.TryDequeue(out var directory) && entries.Count < 3000)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    foreach (string file in Directory.EnumerateFiles(directory.Path))
                    {
                        token.ThrowIfCancellationRequested(); if (entries.Count >= 3000) break;
                        if (!Path.GetExtension(file).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) continue;
                        string title = Path.GetFileNameWithoutExtension(file);
                        entries.TryAdd(title, new("app:" + file, title, "应用程序", LauncherKind.Application, file));
                    }
                    if (directory.Depth < 8)
                        foreach (string child in Directory.EnumerateDirectories(directory.Path))
                            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) directories.Enqueue((child, directory.Depth + 1));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return entries.Values.ToArray();
    }, token);
}
