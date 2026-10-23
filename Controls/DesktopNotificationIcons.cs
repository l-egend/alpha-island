using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using alpha_island.Core;

namespace alpha_island.Controls;

/// <summary>Share validated application logos across every notification presentation.</summary>
internal static partial class DesktopNotificationIcons
{
    private sealed record DecodedLogo(BitmapSource? Source);
    private static readonly ConditionalWeakTable<byte[], DecodedLogo> Cache = new();
    private static readonly ConditionalWeakTable<byte[], DecodedLogo> ContentCache = new();
    private static readonly ConcurrentDictionary<string, BitmapSource> AppLogos = new(StringComparer.Ordinal);
    private static readonly Lazy<BitmapSource?> QQ = new(() => Bundled("qq"));
    private static readonly Lazy<BitmapSource?> WeChat = new(() => Bundled("wechat"));

    internal static BitmapSource? GetContentImage(DesktopNotification item) =>
        item.ContentImage is { Length: > 0 and <= 1048576 } bytes
            ? ContentCache.GetValue(bytes, b => Decode(b, 160)).Source : null;
    internal static BitmapSource? GetPresentationIcon(DesktopNotification item)
    {
        var source = MessengerIdentity.FromNotification(item) == MessengerClient.QQ ? GetLogo(item)
            : (!item.ContentImageIsHero ? GetContentImage(item) : null) ?? GetLogo(item);
        return source == null ? null : PresentationCache.GetValue(source, FitVisibleIcon).Source;
    }

    internal static void PrimeLogos(IEnumerable<DesktopNotification> items)
    {
        // Old history can contain a valid logo even if the newest toast omitted it.
        foreach (var item in items.OrderBy(item => item.Created))
            if (item.Logo is { Length: > 0 and <= 262144 }) GetLogo(item);
    }
    internal static BitmapSource? GetLogo(DesktopNotification item)
    {
        if (item.Kind != DesktopEventKind.Message) return null;
        var client = MessengerIdentity.FromNotification(item);
        string key = client is { } messenger ? "messenger:" + messenger
            : "app:" + (string.IsNullOrWhiteSpace(item.AppId) ? item.AppName : item.AppId).Trim().ToUpperInvariant();
        if (item.Logo is { Length: > 0 and <= 262144 } bytes && Cache.GetValue(bytes, b => Decode(b, 64)).Source is { } source)
        {
            if (AppLogos.Count >= 64 && !AppLogos.ContainsKey(key)) AppLogos.Clear();
            AppLogos[key] = source; return source;
        }
        if (AppLogos.TryGetValue(key, out var remembered)) return remembered;
        return client switch { MessengerClient.QQ => QQ.Value, MessengerClient.WeChat => WeChat.Value, _ => null };
    }
    private static BitmapSource? Bundled(string name)
    {
        try
        {
            var bitmap = new BitmapImage(); bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri($"pack://application:,,,/alpha-island;component/Assets/Messengers/{name}.png");
            bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
        catch { return null; }
    }
    private static DecodedLogo Decode(byte[] bytes, int size)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage(); bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = size; bitmap.StreamSource = stream;
            bitmap.EndInit(); bitmap.Freeze(); return new(bitmap);
        }
        catch { return new(null); }
    }
}