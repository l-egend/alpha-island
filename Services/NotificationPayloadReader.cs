using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using alpha_island.Core;
using alpha_island.Interop;
using Windows.Management.Deployment;

namespace alpha_island.Services;

/// <summary>Optional read-only enrichment. The listener permission remains the gate;
/// Windows schema changes or inaccessible images fall back to the official listener data.</summary>
internal static class NotificationPayloadReader
{
    private const int MaxImageBytes = 1024 * 1024;
    internal sealed record Details(string[] Text, string? Image, bool Hero);

    internal static async Task<IReadOnlyList<DesktopNotification>> EnrichAsync(IReadOnlyList<DesktopNotification> items)
    {
        if (items.Count == 0) return items;
        return await Task.Run(() =>
        {
            var result = items.ToArray();
            try
            {
                var ids = items.Where(x => x.WindowsId.HasValue).Select(x => x.WindowsId!.Value).Distinct().Take(100).ToArray();
                if (ids.Length == 0) return result;
                string db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "Windows", "Notifications", "wpndatabase.db");
                string sql = "SELECT n.Id,h.PrimaryId,hex(n.Payload) FROM Notification n "
                    + "JOIN NotificationHandler h ON h.RecordId=n.HandlerId WHERE n.Id IN("
                    + string.Join(",", ids.Select(x => x.ToString(CultureInfo.InvariantCulture))) + ")";
                if (!WinSqlite.TryQuery(db, sql, 100, out var rows, out _)) return result;
                for (int i = 0; i < result.Length; i++)
                {
                    var item = result[i];
                    var row = rows.FirstOrDefault(r => uint.TryParse(r[0], out uint id) && id == item.WindowsId
                        && string.Equals(r[1], item.AppId, StringComparison.OrdinalIgnoreCase));
                    if (row?[2] is not { Length: > 0 and <= 2097152 } hex) continue;
                    var details = Parse(DecodePayload(Convert.FromHexString(hex)));
                    // IDs can be recycled; never attach a different notification's text/image.
                    if (details == null || !SameContent(item, details.Text)) continue;
                    byte[]? image = ReadImage(details.Image, item.AppId);
                    result[i] = item with { Title = details.Text.FirstOrDefault() ?? item.Title,
                        Body = string.Join("\n", details.Text.Skip(1)), ContentImage = image, ContentImageIsHero = details.Hero };
                }
            }
            catch { /* Optional enrichment must not disrupt notification delivery. */ }
            return result;
        });
    }

    private static bool SameContent(DesktopNotification item, IReadOnlyList<string> text)
    {
        string title = text.FirstOrDefault() ?? "";
        string body = string.Join("\n", text.Skip(1));
        return title.Trim() == item.Title.Trim() && (body.Trim() == item.Body.Trim()
            || body.Trim().StartsWith(item.Body.Trim(), StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(item.Body));
    }

    internal static string DecodePayload(byte[] payload)
    {
        if (payload.Length >= 2 && (payload[0] == 0xFF && payload[1] == 0xFE || payload[1] == 0))
            return Encoding.Unicode.GetString(payload).TrimStart('\uFEFF');
        if (payload.Length >= 2 && (payload[0] == 0xFE && payload[1] == 0xFF || payload[0] == 0))
            return Encoding.BigEndianUnicode.GetString(payload).TrimStart('\uFEFF');
        return Encoding.UTF8.GetString(payload).TrimStart('\uFEFF');
    }

    internal static Details? Parse(string xml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1048576 });
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            var bindings = document.Descendants().Where(x => x.Name.LocalName == "binding").ToArray();
            var binding = bindings.FirstOrDefault(x => (string?)x.Attribute("template") == "ToastGeneric") ?? bindings.FirstOrDefault();
            if (binding == null) return null;
            string[] text = binding.Descendants().Where(x => x.Name.LocalName == "text").Select(x => x.Value).ToArray();
            if (text.Length == 0) return null;
            var images = binding.Descendants().Where(x => x.Name.LocalName == "image").ToArray();
            var image = images.FirstOrDefault(x => (string?)x.Attribute("placement") == "appLogoOverride")
                ?? images.FirstOrDefault(x => (string?)x.Attribute("placement") != "hero") ?? images.FirstOrDefault();
            return new(text, (string?)image?.Attribute("src"), (string?)image?.Attribute("placement") == "hero");
        }
        catch { return null; }
    }

    internal static string? ResolvePackagedImage(string root, string relative)
    {
        string containedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(containedRoot, StringComparison.OrdinalIgnoreCase)) return null;
        if (File.Exists(path)) return path;
        // ms-appx paths are logical resource names. Store apps commonly ship only
        // qualified files such as BatteryCharging100.scale-200.png.
        string? directory = Path.GetDirectoryName(path);
        if (directory == null || !Directory.Exists(directory)) return null;
        string name = Path.GetFileNameWithoutExtension(path), extension = Path.GetExtension(path);
        return Directory.EnumerateFiles(directory).Take(256)
            .Where(candidate => Path.GetExtension(candidate).Equals(extension, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => Path.GetFileNameWithoutExtension(candidate).StartsWith(name + ".", StringComparison.OrdinalIgnoreCase))
            .Where(candidate => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(candidate)[(name.Length + 1)..],
                @"^(scale-\d+|targetsize-\d+)(_(altform-unplated|contrast-standard))?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .OrderBy(candidate => Path.GetFileName(candidate).Contains("scale-200", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(candidate => Path.GetFileName(candidate), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    internal static byte[]? ReadImage(string? source, string appId)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        try
        {
            if (source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                int comma = source.IndexOf(',');
                if (comma < 0 || !source[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                    || source.Length > MaxImageBytes * 4 / 3 + 100) return null;
                byte[] data = Convert.FromBase64String(source[(comma + 1)..]);
                return data.Length <= MaxImageBytes ? data : null;
            }
            string? path = null;
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
            {
                if (uri.IsFile && !uri.IsUnc) path = uri.LocalPath;
                else if (uri.Scheme is "ms-appx" or "ms-appdata")
                {
                    string family = appId.Split('!')[0];
                    if (family.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
                    string relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                    string? root = null;
                    if (uri.Scheme == "ms-appx")
                        root = new PackageManager().FindPackagesForUser("", family).FirstOrDefault()?.InstalledLocation.Path;
                    else
                    {
                        string[] parts = relative.Split(Path.DirectorySeparatorChar, 2);
                        string? folder = parts[0] switch { "local" => "LocalState", "roaming" => "RoamingState", "temp" => "TempState", _ => null };
                        if (folder != null && parts.Length == 2)
                        {
                            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", family, folder);
                            relative = parts[1];
                        }
                    }
                    if (root != null)
                    {
                        path = ResolvePackagedImage(root, relative);
                        if (path == null) return null;
                        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return null;
                    }
                }
            }
            else if (Path.IsPathFullyQualified(source)) path = source;
            if (path == null || path.StartsWith("\\\\", StringComparison.Ordinal) || !File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is <= 0 or > MaxImageBytes) return null;
            using var memory = new MemoryStream(); stream.CopyTo(memory);
            return memory.Length <= MaxImageBytes ? memory.ToArray() : null;
        }
        catch { return null; }
    }
}
