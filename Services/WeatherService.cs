using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using alpha_island.Core;

namespace alpha_island.Services;

public sealed record WeatherCity(string Name, double Latitude, double Longitude, string Region = "")
{
    public string Display => Region.Length == 0 ? Name : Name + " · " + Region;
    public static WeatherCity Beijing { get; } = new("北京", 39.9042, 116.4074, "中国");
}

public sealed record WeatherSnapshot(WeatherCity City, double Temperature, double FeelsLike,
    int Code, double Low, double High, DateTime UpdatedUtc)
{
    public string Description => WeatherService.Describe(Code);
}

public sealed class WeatherService : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly string _cachePath;
    public WeatherService(HttpClient? client = null, string? cachePath = null)
    {
        _http = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _ownsClient = client == null;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AlphaIsland/0.1.11");
        _cachePath = cachePath ?? AppPaths.WeatherCacheFile;
    }

    public async Task<IReadOnlyList<WeatherCity>> SearchAsync(string query, CancellationToken ct)
    {
        string url = "https://geocoding-api.open-meteo.com/v1/search?count=5&language=zh&format=json&name=" + Uri.EscapeDataString(query.Trim());
        using var json = JsonDocument.Parse(await _http.GetStringAsync(url, ct).ConfigureAwait(false));
        if (!json.RootElement.TryGetProperty("results", out var results)) return Array.Empty<WeatherCity>();
        return results.EnumerateArray().Select(x => new WeatherCity(x.GetProperty("name").GetString() ?? query,
            x.GetProperty("latitude").GetDouble(), x.GetProperty("longitude").GetDouble(),
            string.Join(" / ", new[] { Text(x, "admin1"), Text(x, "country") }.Where(s => s.Length > 0)))).ToArray();
    }

    // 只用于用户当前从 Windows 定位取得的坐标；不使用 IP 猜测定位。
    public async Task<WeatherCity> NameCurrentLocationAsync(double latitude, double longitude, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await _http.GetStringAsync(
            "https://api.bigdatacloud.net/data/reverse-geocode-client?localityLanguage=zh&latitude=" + Number(latitude) +
            "&longitude=" + Number(longitude), ct).ConfigureAwait(false));
        string name = Text(json.RootElement, "city");
        if (name.Length == 0) name = Text(json.RootElement, "locality");
        return new WeatherCity(name.Length == 0 ? "系统定位地区" : name, latitude, longitude);
    }

    public async Task<WeatherSnapshot> GetAsync(WeatherCity city, CancellationToken ct)
    {
        if (!ValidCity(city)) throw new ArgumentException("城市坐标无效");
        string url = "https://api.open-meteo.com/v1/forecast?latitude=" + Number(city.Latitude) + "&longitude=" + Number(city.Longitude) +
            "&current=temperature_2m,apparent_temperature,weather_code&daily=temperature_2m_min,temperature_2m_max&forecast_days=1&timezone=auto";
        var snapshot = Parse(await _http.GetStringAsync(url, ct).ConfigureAwait(false), city, DateTime.UtcNow);
        try { AtomicJsonFile.Write(_cachePath, JsonSerializer.Serialize(snapshot)); }
        catch (Exception ex) { IslandLog.Warn("Weather", "天气缓存保存失败: " + ex.Message); }
        return snapshot;
    }

    public WeatherSnapshot? Cached(WeatherCity city)
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            var data = AtomicJsonFile.Read<WeatherSnapshot>(_cachePath);
            return data != null && ValidCity(data.City) && Math.Abs(data.City.Latitude - city.Latitude) < 0.01 &&
                Math.Abs(data.City.Longitude - city.Longitude) < 0.01 &&
                data.UpdatedUtc <= DateTime.UtcNow.AddMinutes(1) && DateTime.UtcNow - data.UpdatedUtc < TimeSpan.FromHours(24) ? data : null;
        }
        catch { return null; }
    }

    internal static WeatherSnapshot Parse(string json, WeatherCity city, DateTime now)
    {
        using var document = JsonDocument.Parse(json);
        var current = document.RootElement.GetProperty("current");
        var daily = document.RootElement.GetProperty("daily");
        double temperature = current.GetProperty("temperature_2m").GetDouble();
        double feels = current.GetProperty("apparent_temperature").GetDouble();
        double low = daily.GetProperty("temperature_2m_min")[0].GetDouble();
        double high = daily.GetProperty("temperature_2m_max")[0].GetDouble();
        if (!double.IsFinite(temperature) || !double.IsFinite(feels) || !double.IsFinite(low) || !double.IsFinite(high))
            throw new JsonException("天气数据无效");
        return new(city, temperature, feels, current.GetProperty("weather_code").GetInt32(), low, high, now);
    }

    internal static bool ValidCity(WeatherCity city) => double.IsFinite(city.Latitude) && double.IsFinite(city.Longitude) &&
        city.Latitude is >= -90 and <= 90 && city.Longitude is >= -180 and <= 180;
    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    private static string Text(JsonElement x, string key) => x.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
    internal static string Describe(int code) => code switch
    {
        0 => "晴", 1 => "大部晴朗", 2 => "多云", 3 => "阴", 45 or 48 => "雾",
        51 or 53 or 55 => "毛毛雨", 56 or 57 or 66 or 67 => "冻雨",
        61 or 63 or 65 => "雨", 71 or 73 or 75 or 77 => "雪",
        80 or 81 or 82 => "阵雨", 85 or 86 => "阵雪", 95 or 96 or 99 => "雷雨", _ => "天气状态未知"
    };
    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
