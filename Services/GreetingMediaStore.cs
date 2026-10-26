using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.Wave;

namespace alpha_island.Services;

internal readonly record struct GreetingGifFrame(BitmapSource Image, TimeSpan Delay);

/// <summary>Imports small, independent welcome assets. Original user files are never changed.</summary>
internal sealed class GreetingMediaStore(string directory)
{
    internal string DirectoryPath { get; } = Path.GetFullPath(directory);
    internal const int ImagePixels = 128;
    internal const double MaxSoundSeconds = 10;
    private const int MaxGifFrames = 120;

    internal string ImportImage(string source)
    {
        ValidateFile(source, 20 * 1024 * 1024);
        byte[] raw = File.ReadAllBytes(source);
        if (TryLoadAnimatedGif(raw, out _))
            return WriteAsset("image", ".gif", path => File.WriteAllBytes(path, raw));

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
            throw new InvalidDataException("请选择长度 10 秒内的短音效。");
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

    internal static GreetingGifFrame[] LoadFrames(string path, bool firstFrameOnly = false)
    {
        ValidateFile(path, 20 * 1024 * 1024);
        byte[] raw = File.ReadAllBytes(path);
        if (TryLoadAnimatedGif(raw, out var frames, firstFrameOnly)) return frames;
        return [new GreetingGifFrame(LoadImage(path), TimeSpan.Zero)];
    }

    private static bool TryLoadAnimatedGif(byte[] raw, out GreetingGifFrame[] frames, bool firstFrameOnly = false)
    {
        frames = [];
        try
        {
            using var stream = new MemoryStream(raw, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder is not GifBitmapDecoder || decoder.Frames.Count <= 1) return false;

            int count = firstFrameOnly ? 1 : Math.Min(decoder.Frames.Count, MaxGifFrames);
            int canvasWidth = Math.Max(1, decoder.Frames[0].PixelWidth);
            int canvasHeight = Math.Max(1, decoder.Frames[0].PixelHeight);
            if (decoder.Metadata is BitmapMetadata gifMeta)
            {
                canvasWidth = Math.Max(canvasWidth, GetMetaUShort(gifMeta, "/logscrdesc/Width", (ushort)canvasWidth));
                canvasHeight = Math.Max(canvasHeight, GetMetaUShort(gifMeta, "/logscrdesc/Height", (ushort)canvasHeight));
            }
            for (int i = 0; i < count; i++)
            {
                var meta = decoder.Frames[i].Metadata as BitmapMetadata;
                canvasWidth = Math.Max(canvasWidth, GetMetaUShort(meta, "/imgdesc/Left", 0) + decoder.Frames[i].PixelWidth);
                canvasHeight = Math.Max(canvasHeight, GetMetaUShort(meta, "/imgdesc/Top", 0) + decoder.Frames[i].PixelHeight);
            }

            byte[] canvas = new byte[canvasWidth * canvasHeight * 4];
            var result = new GreetingGifFrame[count];
            for (int i = 0; i < count; i++)
            {
                var srcFrame = decoder.Frames[i];
                var meta = srcFrame.Metadata as BitmapMetadata;
                int left = GetMetaUShort(meta, "/imgdesc/Left", 0);
                int top = GetMetaUShort(meta, "/imgdesc/Top", 0);
                int delayCs = GetMetaUShort(meta, "/grctlext/Delay", 10);
                byte disposal = GetMetaByte(meta, "/grctlext/Disposal", 0);
                byte[]? backup = disposal == 3 ? (byte[])canvas.Clone() : null;

                var bgra = new FormatConvertedBitmap(srcFrame, PixelFormats.Bgra32, null, 0);
                int fw = bgra.PixelWidth, fh = bgra.PixelHeight;
                byte[] patch = new byte[fw * fh * 4];
                bgra.CopyPixels(patch, fw * 4, 0);

                for (int y = 0; y < fh && top + y < canvasHeight; y++)
                {
                    for (int x = 0; x < fw && left + x < canvasWidth; x++)
                    {
                        int sIdx = (y * fw + x) * 4;
                        byte a = patch[sIdx + 3];
                        if (a == 0) continue;
                        int dIdx = ((top + y) * canvasWidth + (left + x)) * 4;
                        canvas[dIdx] = patch[sIdx];
                        canvas[dIdx + 1] = patch[sIdx + 1];
                        canvas[dIdx + 2] = patch[sIdx + 2];
                        canvas[dIdx + 3] = a;
                    }
                }

                BitmapSource composed = BitmapSource.Create(canvasWidth, canvasHeight, 96, 96, PixelFormats.Bgra32, null, canvas, canvasWidth * 4);
                if (canvasWidth > ImagePixels || canvasHeight > ImagePixels)
                {
                    double scale = canvasWidth >= canvasHeight
                        ? (double)ImagePixels / canvasWidth
                        : (double)ImagePixels / canvasHeight;
                    var scaled = new TransformedBitmap(composed, new ScaleTransform(scale, scale));
                    int sw = Math.Max(1, scaled.PixelWidth), sh = Math.Max(1, scaled.PixelHeight);
                    byte[] scaledBytes = new byte[sw * sh * 4];
                    scaled.CopyPixels(scaledBytes, sw * 4, 0);
                    composed = BitmapSource.Create(sw, sh, 96, 96, PixelFormats.Bgra32, null, scaledBytes, sw * 4);
                }
                composed.Freeze();
                TimeSpan delay = TimeSpan.FromMilliseconds(Math.Clamp((delayCs <= 1 ? 10 : delayCs) * 10, 20, 1000));
                result[i] = new GreetingGifFrame(composed, delay);

                if (disposal == 2)
                {
                    for (int y = 0; y < fh && top + y < canvasHeight; y++)
                        Array.Clear(canvas, ((top + y) * canvasWidth + left) * 4, Math.Min(fw, canvasWidth - left) * 4);
                }
                else if (disposal == 3 && backup != null)
                {
                    canvas = backup;
                }
            }

            frames = result;
            return frames.Length > 1;
        }
        catch
        {
            frames = [];
            return false;
        }
    }

    private static ushort GetMetaUShort(BitmapMetadata? meta, string query, ushort fallback)
    {
        try { if (meta != null && meta.ContainsQuery(query) && meta.GetQuery(query) is ushort v) return v; }
        catch { }
        return fallback;
    }

    private static byte GetMetaByte(BitmapMetadata? meta, string query, byte fallback)
    {
        try { if (meta != null && meta.ContainsQuery(query) && meta.GetQuery(query) is byte v) return v; }
        catch { }
        return fallback;
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
            throw new IOException("欢迎素材目录不接受目录链接。");
        string destination = Path.Combine(DirectoryPath, kind + "-" + Guid.NewGuid().ToString("N") + extension);
        try { write(destination); return destination; }
        catch { if (File.Exists(destination)) File.Delete(destination); throw; }
    }

    private static void ValidateFile(string path, long maximumBytes)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0 || file.Length > maximumBytes)
            throw new InvalidDataException("请选择有效的本地文件，且大小不能超过 20 MB。");
    }
}
