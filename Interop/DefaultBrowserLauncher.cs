using System.Diagnostics;
using alpha_island.Core;

namespace alpha_island.Interop;

internal static class DefaultBrowserLauncher
{
    internal static ProcessStartInfo CreateStartInfo(Uri uri)
    {
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || WebLinkDetector.TryWebUri(uri.AbsoluteUri) == null)
            throw new ArgumentException("只能打开 HTTP 或 HTTPS 网页链接", nameof(uri));
        return new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true, Verb = "open" };
    }
    internal static void Open(Uri uri) => Process.Start(CreateStartInfo(uri))?.Dispose();
}
