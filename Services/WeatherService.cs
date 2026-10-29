using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using alpha_island.Core;

namespace alpha_island.Services;

public sealed record WeatherCity(string Name, double Latitude, double Longitude, string Region = "")
{
    public string Display => Region.Length == 0 ? Name : Name + " · " + Region;
    public static WeatherCity Beijing { get; } = new(L.T("北京"), 39.9042, 116.4074, L.T("中国"));
}

public sealed record WeatherSnapshot(WeatherCity City, double Temperature, double FeelsLike,
    int Code, double Low, double High, DateTime UpdatedUtc)
{
    public string Description => WeatherService.Describe(Code);
    public int UtcOffsetSeconds { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public double? WindSpeed { get; init; }
    public double? WindGusts { get; init; }
    public WeatherDay[] Days { get; init; } = [];
    public WeatherHour[] Hours { get; init; } = [];
}

public sealed record WeatherDay(DateOnly Date, int Code, double Low, double High,
    double? RainProbability, double? Precipitation, double? WindSpeed, double? WindGusts);
public sealed record WeatherHour(DateTimeOffset Time, double Temperature, int Code,
    double? RainProbability, double? Precipitation, double? WindSpeed, double? WindGusts);

public sealed class WeatherService : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly string _cachePath;
    public WeatherService(HttpClient? client = null, string? cachePath = null)
    {
        _http = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _ownsClient = client == null;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AlphaIsland/0.1.17");
        _cachePath = cachePath ?? AppPaths.WeatherCacheFile;
    }

    public async Task<IReadOnlyList<WeatherCity>> SearchAsync(string query, CancellationToken ct)
    {
        string url = $"https://geocoding-api.open-meteo.com/v1/search?count=5&language={(L.IsEnglish ? "en" : "zh")}&format=json&name=" + Uri.EscapeDataString(query.Trim());
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
        return new WeatherCity(name.Length == 0 ? L.T("系统定位地区") : name, latitude, longitude);
    }

    public async Task<WeatherSnapshot> GetAsync(WeatherCity city, CancellationToken ct)
    {
        if (!ValidCity(city)) throw new ArgumentException(L.T("城市坐标无效"));
        string url = "https://api.open-meteo.com/v1/forecast?latitude=" + Number(city.Latitude) + "&longitude=" + Number(city.Longitude) +
            "&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m,wind_gusts_10m" +
            "&hourly=temperature_2m,weather_code,precipitation_probability,precipitation,wind_speed_10m,wind_gusts_10m" +
            "&daily=weather_code,temperature_2m_min,temperature_2m_max,precipitation_probability_max,precipitation_sum,wind_speed_10m_max,wind_gusts_10m_max" +
            "&forecast_days=3&timezone=auto&timeformat=unixtime&temperature_unit=celsius&wind_speed_unit=kmh&precipitation_unit=mm";
        var snapshot = Parse(await _http.GetStringAsync(url, ct).ConfigureAwait(false), city, DateTime.UtcNow);
        try { AtomicJsonFile.Write(_cachePath, JsonSerializer.Serialize(snapshot)); }
        catch (Exception ex) { IslandLog.Warn("Weather", "天气缓存保存失败: " + ex.Message); }
        return snapshot;
    }

    public WeatherSnapshot? Cached(WeatherCity? city = null)
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            var data = AtomicJsonFile.Read<WeatherSnapshot>(_cachePath);
            return data != null && ValidCity(data.City) && double.IsFinite(data.Temperature) &&
                (city == null || SameCity(data.City, city)) &&
                data.UpdatedUtc <= DateTime.UtcNow.AddMinutes(1) && DateTime.UtcNow - data.UpdatedUtc < TimeSpan.FromHours(24) ? data : null;
        }
        catch { return null; }
    }

    // 城市可以保留，天气本身仍按 Cached 的 24 小时期限失效。
    internal WeatherCity? LastCity()
    {
        try
        {
            var city = File.Exists(_cachePath) ? AtomicJsonFile.Read<WeatherSnapshot>(_cachePath)?.City : null;
            return city != null && ValidCity(city) ? city : null;
        }
        catch { return null; }
    }

    internal static WeatherSnapshot Parse(string json, WeatherCity city, DateTime now)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var current = root.GetProperty("current");
        var daily = root.GetProperty("daily");
        int offset = root.TryGetProperty("utc_offset_seconds", out var zone) ? zone.GetInt32() : 0;
        if (offset is < -43200 or > 50400) throw new JsonException(L.T("天气时区无效"));
        double temperature = current.GetProperty("temperature_2m").GetDouble();
        double feels = current.GetProperty("apparent_temperature").GetDouble();
        double low = daily.GetProperty("temperature_2m_min")[0].GetDouble();
        double high = daily.GetProperty("temperature_2m_max")[0].GetDouble();
        if (!double.IsFinite(temperature) || !double.IsFinite(feels) || !double.IsFinite(low) || !double.IsFinite(high))
            throw new JsonException(L.T("天气数据无效"));
        var days = new List<WeatherDay>();
        if (daily.TryGetProperty("time", out var dates))
            for (int i = 0; i < Math.Min(3, dates.GetArrayLength()); i++)
            {
                var date = DateOnly.FromDateTime(ReadTime(dates[i], offset).ToOffset(TimeSpan.FromSeconds(offset)).DateTime);
                if (At(daily, "temperature_2m_min", i) is not double min || At(daily, "temperature_2m_max", i) is not double max) continue;
                days.Add(new(date, (int)(At(daily, "weather_code", i) ?? -1), min, max,
                    At(daily, "precipitation_probability_max", i), At(daily, "precipitation_sum", i),
                    At(daily, "wind_speed_10m_max", i), At(daily, "wind_gusts_10m_max", i)));
            }
        var hours = new List<WeatherHour>();
        if (root.TryGetProperty("hourly", out var hourly) && hourly.TryGetProperty("time", out var times))
            for (int i = 0; i < Math.Min(72, times.GetArrayLength()); i++)
            {
                if (At(hourly, "temperature_2m", i) is not double temp) continue;
                hours.Add(new(ReadTime(times[i], offset), temp, (int)(At(hourly, "weather_code", i) ?? -1),
                    At(hourly, "precipitation_probability", i), At(hourly, "precipitation", i),
                    At(hourly, "wind_speed_10m", i), At(hourly, "wind_gusts_10m", i)));
            }
        return new(city, temperature, feels, current.GetProperty("weather_code").GetInt32(), low, high, now)
        {
            UtcOffsetSeconds = offset,
            ObservedAt = current.TryGetProperty("time", out var observed) ? ReadTime(observed, offset) : null,
            WindSpeed = Value(current, "wind_speed_10m"), WindGusts = Value(current, "wind_gusts_10m"),
            Days = days.ToArray(), Hours = hours.ToArray()
        };
    }

    private static DateTimeOffset ReadTime(JsonElement value, int offset) => value.ValueKind == JsonValueKind.Number
        ? DateTimeOffset.FromUnixTimeSeconds(value.GetInt64())
        : new DateTimeOffset(DateTime.SpecifyKind(DateTime.Parse(value.GetString()!, CultureInfo.InvariantCulture), DateTimeKind.Unspecified), TimeSpan.FromSeconds(offset));
    private static double? At(JsonElement x, string key, int index) => x.TryGetProperty(key, out var array) && array.ValueKind == JsonValueKind.Array && index < array.GetArrayLength()
        && array[index].ValueKind == JsonValueKind.Number && array[index].TryGetDouble(out var value) && double.IsFinite(value) ? value : null;
    private static double? Value(JsonElement x, string key) => x.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var value) && double.IsFinite(value) ? value : null;
    internal static bool SameCity(WeatherCity a, WeatherCity b) => Math.Abs(a.Latitude - b.Latitude) < 0.01 && Math.Abs(a.Longitude - b.Longitude) < 0.01;

    internal static bool ValidCity(WeatherCity city) => double.IsFinite(city.Latitude) && double.IsFinite(city.Longitude) &&
        city.Latitude is >= -90 and <= 90 && city.Longitude is >= -180 and <= 180;
    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    private static string Text(JsonElement x, string key) => x.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
    internal static string Describe(int code) => code switch
    {
        0 => L.T("晴"), 1 => L.T("大部晴朗"), 2 => L.T("多云"), 3 => L.T("阴"), 45 or 48 => L.T("雾"),
        51 or 53 or 55 => L.T("毛毛雨"), 56 or 57 or 66 or 67 => L.T("冻雨"),
        61 or 63 or 65 => L.T("雨"), 71 or 73 or 75 or 77 => L.T("雪"),
        80 or 81 or 82 => L.T("阵雨"), 85 or 86 => L.T("阵雪"), 95 or 96 or 97 or 99 => L.T("雷雨"), _ => L.T("天气状态未知")
    };
    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
