using System.IO;
using System.Windows.Media.Imaging;
using NAudio.Wave;

namespace alpha_island.Services;

/// <summary>Imports small, independent welcome assets. Original user files are never changed.</summary>
internal sealed class GreetingMediaStore(string directory)
{
    internal string DirectoryPath { get; } = Path.GetFullPath(directory);
    internal const int ImagePixels = 128;
    internal const double MaxSoundSeconds = 10;

    internal string ImportImage(string source)
    {
        ValidateFile(source, 20 * 1024 * 1024);
        var bitmap = LoadImage(source);
        return WriteAsset("image", ".png", path =>
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(path); encoder.Save(output);
        });
    }

    internal string ImportSound(string source)
    {
        ValidateFile(source, 20 * 1024 * 1024);
        using var reader = new AudioFileReader(source);
        if (reader.TotalTime <= TimeSpan.Zero || reader.TotalTime.TotalSeconds > MaxSoundSeconds)
            throw new InvalidDataException("请选择长度在 10 秒以内的短音效。");
        return WriteAsset("sound", ".wav", path => WaveFileWriter.CreateWaveFile16(path, reader));
    }

    internal static BitmapImage LoadImage(string path)
    {
        ValidateFile(path, 20 * 1024 * 1024);
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0]; bool wide = frame.PixelWidth >= frame.PixelHeight;
        stream.Position = 0;
        var image = new BitmapImage(); image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream;
        if (wide) image.DecodePixelWidth = ImagePixels; else image.DecodePixelHeight = ImagePixels;
        image.EndInit(); image.Freeze(); return image;
    }

    internal void RemoveOwnedAsset(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            string full = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(full), DirectoryPath, StringComparison.OrdinalIgnoreCase)) return;
            string name = Path.GetFileNameWithoutExtension(full);
            if (!(name.StartsWith("image-", StringComparison.Ordinal) || name.StartsWith("sound-", StringComparison.Ordinal))
                || !Guid.TryParseExact(name[(name.IndexOf('-') + 1)..], "N", out _)) return;
            if (!File.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return;
            if ((File.GetAttributes(DirectoryPath) & FileAttributes.ReparsePoint) != 0) return;
            File.Delete(full);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string WriteAsset(string kind, string extension, Action<string> write)
    {
        Directory.CreateDirectory(DirectoryPath);
        if ((File.GetAttributes(DirectoryPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("欢迎素材目录不能是目录链接。");
        string destination = Path.Combine(DirectoryPath, kind + "-" + Guid.NewGuid().ToString("N") + extension);
        try { write(destination); return destination; }
        catch { if (File.Exists(destination)) File.Delete(destination); throw; }
    }

    private static void ValidateFile(string path, long maximumBytes)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0 || file.Length > maximumBytes)
            throw new InvalidDataException("请选择有效的本地文件，大小不能超过 20 MB。");
    }
}
