using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;
using alpha_island.Core;

namespace alpha_island.Services;

internal static class GreetingPresetCatalog
{
    internal const string Rabbit = "rabbit";
    internal const string Nailong = "nailong";
    internal const string Amiya = "amiya";
    internal const string HoshinoSwimsuit = "hoshino-swimsuit";
    internal const string Random = "random";
    internal const string WelcomeText = "欢迎回来";
    internal const string Custom = "custom";
    internal const string NailongMedia = "builtin:nailong";
    internal const string AmiyaMedia = "builtin:amiya";
    internal const string HoshinoSwimsuitMedia = "builtin:hoshino-swimsuit";
    internal static readonly string[] Ids = [Rabbit, Nailong, Amiya, HoshinoSwimsuit, Random];
    internal static readonly string[] Labels = ["兔子 · 轻柔启动音", "奶龙 · 欢笑音效", "阿米娅 · 问候语音", "星野 · 欢迎语音", "随机 · 图片与语音配套"];
    internal static string ChooseRandomPreset() => Ids[System.Random.Shared.Next(Ids.Length - 1)];
    internal static bool IsBuiltInAnimation(string? key) => key is NailongMedia or AmiyaMedia or HoshinoSwimsuitMedia;
    internal static bool IsBuiltInSound(string? key) => IsBuiltInAnimation(key);
    private const string OriginalImageHash = "1FBB14B63B1986296857CC6ECD277C5D2EC2BC52C5AAE01F3600DE1A6AC06030";
    private const string OriginalSoundHash = "C5C8F4ABF5E35111F49993DA7949FF4FAFE4BD5EDCD1CE9E93C8512A5D225C1E";
    private static readonly object LegacyLock = new();
    private static string? _legacySignature;
    private static bool _legacyMatch;

    internal static string GetId(IslandOptions options)
    {
        string id = SettingsRegistry.GetText(options, "GreetingPreset", "");
        if (id is Rabbit or Nailong or Amiya or HoshinoSwimsuit or Random or Custom) return id;
        string image = SettingsRegistry.GetText(options, "GreetingImagePath", "");
        string sound = SettingsRegistry.GetText(options, "GreetingSoundPath", "");
        if (image.Length == 0 && sound.Length == 0) return Rabbit;
        return MatchesLegacyPair(image, sound) ? Nailong : Custom;
    }

    internal static string ImageKey(IslandOptions options, string? resolvedPreset = null) => (resolvedPreset ?? GetId(options)) switch
    {
        Nailong => NailongMedia,
        Amiya => AmiyaMedia,
        HoshinoSwimsuit => HoshinoSwimsuitMedia,
        Custom => SettingsRegistry.GetText(options, "GreetingImagePath", ""),
        _ => ""
    };

    internal static string SoundKey(IslandOptions options, string? resolvedPreset = null) => (resolvedPreset ?? GetId(options)) switch
    {
        Nailong => NailongMedia,
        Amiya => AmiyaMedia,
        HoshinoSwimsuit => HoshinoSwimsuitMedia,
        Custom => SettingsRegistry.GetText(options, "GreetingSoundPath", ""),
        _ => ""
    };

    internal static void Select(IslandOptions options, string id)
    {
        if (id is not (Rabbit or Nailong or Amiya or HoshinoSwimsuit or Random)) throw new ArgumentException("未知的欢迎组合。", nameof(id));
        SettingsRegistry.Write(options, "GreetingPreset", id);
        SettingsRegistry.Write(options, "GreetingImagePath", "");
        SettingsRegistry.Write(options, "GreetingSoundPath", "");
    }

    internal static GreetingGifFrame[] LoadFrames(string key, bool firstFrameOnly = false)
    {
        if (!IsBuiltInAnimation(key)) return key.Length == 0 ? [] : GreetingMediaStore.LoadFrames(key, firstFrameOnly);
        using var stream = OpenResource("Frames.zip", ResourceFolder(key));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var manifest = archive.GetEntry("delays.json") ?? throw new InvalidDataException("欢迎动画缺少播放时间。");
        if (manifest.Length > 4096) throw new InvalidDataException("欢迎动画时间表过大。");
        using var metadata = manifest.Open();
        int[] delays = JsonSerializer.Deserialize<int[]>(metadata) ?? [];
        if (delays.Length is < 2 or > 120) throw new InvalidDataException("欢迎动画帧数无效。");
        var frames = new GreetingGifFrame[firstFrameOnly ? 1 : delays.Length];
        for (int i = 0; i < frames.Length; i++)
        {
            var entry = archive.GetEntry($"{i:D3}.png") ?? throw new InvalidDataException("欢迎动画帧不完整。");
            if (entry.Length > 256 * 1024) throw new InvalidDataException("欢迎动画帧过大。");
            using var input = entry.Open();
            using var buffer = new MemoryStream((int)entry.Length); input.CopyTo(buffer); buffer.Position = 0;
            var image = new BitmapImage(); image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = buffer;
            image.DecodePixelWidth = GreetingMediaStore.ImagePixels; image.EndInit(); image.Freeze();
            if (image.PixelHeight > GreetingMediaStore.ImagePixels) throw new InvalidDataException("欢迎动画尺寸无效。");
            frames[i] = new GreetingGifFrame(image, TimeSpan.FromMilliseconds(Math.Clamp(delays[i], 20, 1000)));
        }
        return frames;
    }

    internal static Stream OpenNailongSound() => OpenResource("Sound.wav");
    internal static Stream OpenAmiyaSound() => OpenResource("Sound.wav", "Amiya");
    internal static Stream OpenBuiltInSound(string key) => OpenResource("Sound.wav", ResourceFolder(key));

    private static string ResourceFolder(string key) => key switch
    {
        NailongMedia => "Nailong",
        AmiyaMedia => "Amiya",
        HoshinoSwimsuitMedia => "HoshinoSwimsuit",
        _ => throw new ArgumentException("未知的欢迎素材。", nameof(key))
    };

    private static Stream OpenResource(string file, string preset = "Nailong") => typeof(GreetingPresetCatalog).Assembly
        .GetManifestResourceStream($"AlphaIsland.Greeting.{preset}.{file}")
        ?? throw new FileNotFoundException("内置欢迎素材不可用。");

    private static bool MatchesLegacyPair(string image, string sound)
    {
        try
        {
            var picture = new FileInfo(image); var audio = new FileInfo(sound);
            if (!picture.Exists || !audio.Exists || picture.Length > 20 * 1024 * 1024 || audio.Length > 20 * 1024 * 1024) return false;
            string signature = $"{picture.FullName}|{picture.Length}|{picture.LastWriteTimeUtc.Ticks}|{audio.FullName}|{audio.Length}|{audio.LastWriteTimeUtc.Ticks}";
            lock (LegacyLock)
            {
                if (_legacySignature == signature) return _legacyMatch;
                using var pictureStream = picture.OpenRead(); using var audioStream = audio.OpenRead();
                _legacyMatch = Convert.ToHexString(SHA256.HashData(pictureStream)) == OriginalImageHash
                    && Convert.ToHexString(SHA256.HashData(audioStream)) == OriginalSoundHash;
                _legacySignature = signature;
                return _legacyMatch;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
