using System.IO;
using System.Text;

namespace AlphaIsland.Plugins.ApiChat;

internal sealed record ImportedPersona(string FileName, string Text);

/// <summary>A local TXT library; only an explicitly selected file is read, never watched or uploaded automatically.</summary>
internal sealed class ApiPersonaLibrary(string root)
{
    internal const string GuideFileName = "人格提示词编写规范.txt";
    internal const string ExampleFileName = "示例-简洁助理.txt";
    internal const int MaximumFileBytes = 64 * 1024;
    internal string Root { get; } = Path.GetFullPath(root);
    internal void EnsureDirectory()
    {
        Directory.CreateDirectory(Root);
        foreach (string name in new[] { GuideFileName, ExampleFileName })
        {
            string target = Path.Combine(Root, name);
            if (File.Exists(target))
            {
                if (name == GuideFileName)
                {
                    try { UpdateBundledGuide(target); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Documentation updates cannot block importing a personality. */ }
                }
                continue;
            }
            var assembly = typeof(ApiPersonaLibrary).Assembly;
            string resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + name, StringComparison.Ordinal));
            using var source = assembly.GetManifestResourceStream(resource)!;
            FileStream file;
            try { file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
            catch (IOException) when (File.Exists(target)) { continue; }
            using (file) source.CopyTo(file);
        }
    }
    private static void UpdateBundledGuide(string target)
    {
        using var file = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (file.Length > MaximumFileBytes) return;
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file));
        if (hash != "99AC2170595EC4761F6E8FAA91A5BCC3A51FA355B5CC795ACD65D85805AA5AA0") return;
        var assembly = typeof(ApiPersonaLibrary).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + GuideFileName, StringComparison.Ordinal));
        using var source = assembly.GetManifestResourceStream(resource)!;
        file.Position = 0; source.CopyTo(file); file.SetLength(file.Position);
    }
    internal ImportedPersona Import(string sourcePath)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        if (!Path.GetExtension(sourcePath).Equals(".txt", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择 TXT 文本文件。");
        if (Path.GetFileName(sourcePath).Equals(GuideFileName, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("这是编写规范，请选择自己的人格文件或示例文件。");
        string text;
        using (var file = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (file.Length > MaximumFileBytes) throw new ArgumentException("人格文件最多 64 KB；建议使用简短的角色与表达偏好。");
            using var reader = new StreamReader(file, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            try { text = reader.ReadToEnd().Trim(); }
            catch (DecoderFallbackException)
            {
                file.Position = 0;
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                using var legacy = new StreamReader(file, Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback));
                text = legacy.ReadToEnd().Trim();
            }
        }
        if (text.Length == 0 || text.Contains('\0')) throw new ArgumentException("人格文件应为非空的纯文本。");
        if (text.Length > ApiAssistantPrompt.MaximumPersonaCharacters) throw new ArgumentException($"人格提示词最多 {ApiAssistantPrompt.MaximumPersonaCharacters} 字符；请先精简内容。");
        EnsureDirectory();
        if (Path.GetDirectoryName(sourcePath)!.Equals(Root, StringComparison.OrdinalIgnoreCase)) return new(Path.GetFileName(sourcePath), text);
        string stem = Path.GetFileNameWithoutExtension(sourcePath);
        stem = string.Concat(stem.Take(60).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        string name = "人格-" + stem + ".txt";
        string candidate = Path.Combine(Root, name);
        if (File.Exists(candidate))
        {
            if (new FileInfo(candidate).Length <= MaximumFileBytes && File.ReadAllText(candidate) == text) return new(name, text);
            name = "人格-" + stem + "-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        }
        using (var target = new FileStream(Path.Combine(Root, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(target, new UTF8Encoding(false))) writer.Write(text);
        return new(name, text);
    }
}
