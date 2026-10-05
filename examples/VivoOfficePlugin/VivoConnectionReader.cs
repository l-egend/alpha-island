using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AlphaIsland.Plugins.VivoOffice;

// Only these three device fields leave the reader. Flags qualify current/cached data.
internal sealed record VivoConnection(string DeviceName = "", string ConnectionMethod = "",
    DateTimeOffset? ConnectedAt = null, bool IsCurrent = false, string Notice = "")
{
    internal string DisplayTime => ConnectedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "—";
    internal string Summary => DeviceName.Length == 0 ? "暂无连接记录"
        : (IsCurrent ? "" : "最近 · ") + DeviceName + " · " + (ConnectionMethod.Length == 0 ? "—" : ConnectionMethod)
            + " · " + (ConnectedAt?.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "时间未知");
}

internal sealed class VivoConnectionReader
{
    private readonly string _historyPath, _configPath;
    private readonly Func<bool> _isRunning;
    private VivoConnection _previous = new();
    internal VivoConnectionReader(string? historyPath = null, string? configPath = null, Func<bool>? isRunning = null)
    {
        _historyPath = historyPath ?? Environment.GetEnvironmentVariable("ALPHA_ISLAND_VIVO_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pcsuite", "historyPhone.json");
        _configPath = configPath ?? Path.Combine(Path.GetDirectoryName(_historyPath)!, "config.json");
        _isRunning = isRunning ?? IsSuiteRunning;
    }
    internal VivoConnection Read()
    {
        try
        {
            if (!File.Exists(_historyPath)) return _previous = new(Notice: "暂无连接记录");
            using var history = ReadDocument(_historyPath);
            var root = history.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            root.TryGetProperty("last_time_phone", out var recent);
            var fallback = Parse(recent);
            bool running = _isRunning();
            if (running && root.TryGetProperty("phone", out var phones) && phones.ValueKind == JsonValueKind.Array)
            {
                var current = phones.EnumerateArray().Where(e => Name(e).Length > 0)
                    .OrderByDescending(e => Timestamp(e)).FirstOrDefault();
                if (current.ValueKind == JsonValueKind.Object)
                {
                    var connection = Parse(current);
                    // Merge missing fields only when both records refer to the same device.
                    if (SameDevice(current, recent)) connection = connection with
                    {
                        ConnectionMethod = connection.ConnectionMethod.Length == 0 ? fallback.ConnectionMethod : connection.ConnectionMethod,
                        ConnectedAt = connection.ConnectedAt ?? fallback.ConnectedAt
                    };
                    return _previous = connection with { IsCurrent = true };
                }
            }
            bool? currentFromConfig = running && fallback.DeviceName.Length > 0 ? ConfigContains(recent) : false;
            return _previous = fallback with { IsCurrent = currentFromConfig == true,
                Notice = currentFromConfig == null ? "连接状态更新中，显示上次记录" : "" };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return _previous with { IsCurrent = false, Notice = "连接记录更新中，显示上次记录" };
        }
    }
    private bool? ConfigContains(JsonElement recent)
    {
        string id = Identity(recent);
        if (id.Length == 0 || !File.Exists(_configPath)) return false;
        try
        {
            using var config = ReadDocument(_configPath);
            var root = config.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("getConnectedDeviceIdList", out var ids) && ids.ValueKind == JsonValueKind.Array
                && ids.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.String && e.GetString() == id)) return true;
            return root.TryGetProperty("getConnectedDeviceInfoList", out var devices) && devices.ValueKind == JsonValueKind.Array
                && devices.EnumerateArray().Any(e => Text(e, "deviceId") == id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    private static bool SameDevice(JsonElement left, JsonElement right)
    {
        string a = Identity(left), b = Identity(right);
        return a.Length > 0 && b.Length > 0 ? a == b : Name(left).Length > 0 && Name(left) == Name(right);
    }
    private static VivoConnection Parse(JsonElement value) => new(Name(value), Method(value), Timestamp(value));
    private static string Identity(JsonElement value) => Text(value, "device_id", "mobileDeviceId", "deviceId");
    private static string Name(JsonElement value) => Text(value, "mobileDeviceName", "deviceName");
    private static string Text(JsonElement value, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object) return "";
        foreach (string key in keys)
            if (value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(field.GetString()))
            {
                string text = field.GetString()!.Trim();
                return text.Length <= 256 ? text : text[..256];
            }
        return "";
    }
    private static string Method(JsonElement value)
    {
        string flag = Text(value, "connectFlag"), type = Text(value, "otherConnectType", "connectType");
        if (type.Equals("FARAWAYWLAN", StringComparison.OrdinalIgnoreCase)) return "远程连接";
        return (flag.Length > 0 ? flag : type).ToUpperInvariant() switch
        {
            "" => "", "USB" => "USB", "WLAN" or "WIFI" => "Wi-Fi",
            "P2P" or "WIFIP2P" or "WLANP2P" => "Wi-Fi 直连",
            "CLOUD" or "INTERNET" or "FARAWAYWLAN" => "远程连接",
            _ => flag.Length > 0 ? flag : type
        };
    }
    private static DateTimeOffset? Timestamp(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("lastSuccessTime", out var stamp)) return null;
        long milliseconds;
        if (stamp.ValueKind == JsonValueKind.Number)
        { if (!stamp.TryGetInt64(out milliseconds)) return null; }
        else if (stamp.ValueKind == JsonValueKind.String)
        { if (!long.TryParse(stamp.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out milliseconds)) return null; }
        else return null;
        if (milliseconds <= 0) return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
    private static JsonDocument ReadDocument(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 2 * 1024 * 1024) throw new JsonException();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return JsonDocument.Parse(reader.ReadToEnd(), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
    }
    private static bool IsSuiteRunning()
    {
        var processes = Process.GetProcessesByName("pcsuite");
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
