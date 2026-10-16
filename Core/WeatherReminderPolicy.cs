using System.Globalization;
using System.IO;
using System.Text.Json;
using alpha_island.Services;

namespace alpha_island.Core;

internal sealed record WeatherReminderOptions(bool AutoRefresh, int RefreshMinutes, bool Conditions,
    bool Temperature, int TemperatureThreshold, bool Evening, int EveningHour)
{
    public static WeatherReminderOptions From(IslandOptions options) => new(
        SettingsRegistry.GetBool(options, "WeatherAutoRefresh", true),
        Math.Clamp(SettingsRegistry.GetInt(options, "WeatherRefreshMinutes", 15), 5, 120),
        SettingsRegistry.GetBool(options, "WeatherConditionNotify", true),
        SettingsRegistry.GetBool(options, "WeatherTemperatureNotify", true),
        Math.Clamp(SettingsRegistry.GetInt(options, "WeatherTemperatureThreshold", 5), 2, 15),
        SettingsRegistry.GetBool(options, "WeatherEveningNotify", true),
        Math.Clamp(SettingsRegistry.GetInt(options, "WeatherEveningHour", 20), 18, 23));
}

internal sealed record WeatherReminder(string Key, string Title, string Body, int? WeatherCode = null)
{
    internal DesktopNotification ToNotification(DateTimeOffset created) => new("weather:" + Guid.NewGuid().ToString("N"),
        "", "天气", Title, Body, created, DesktopEventKind.Weather, WeatherCode: WeatherCode);
}
internal sealed record WeatherTemperatureReading(DateTimeOffset Time, double Temperature);
internal sealed class WeatherReminderState
{
    public string City { get; set; } = "";
    public DateTimeOffset? LastObservation { get; set; }
    public List<WeatherTemperatureReading> Temperatures { get; set; } = [];
    public string[] ActiveHazards { get; set; } = [];
    public Dictionary<string, DateTimeOffset> Sent { get; set; } = new();
}

/// <summary>只分析刚获取的预报。调度与通知展示由主窗口负责；去重状态独立持久化。</summary>
internal sealed class WeatherReminderPolicy
{
    private readonly WeatherReminderState _state;
    internal WeatherReminderPolicy(WeatherReminderState? state = null) => _state = state ?? new();
    internal static string CityKey(WeatherCity city) => city.Latitude.ToString("0.00", CultureInfo.InvariantCulture) + "/" + city.Longitude.ToString("0.00", CultureInfo.InvariantCulture);
    internal bool EveningDue(WeatherCity city, WeatherReminderOptions options, DateTimeOffset localNow) =>
        options.Evening && localNow.Hour >= options.EveningHour &&
        !_state.Sent.ContainsKey(EveningKey(city, localNow));
    private static string EveningKey(WeatherCity city, DateTimeOffset now) => "evening/" + now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "/" + CityKey(city);

    internal IReadOnlyList<WeatherReminder> Evaluate(WeatherSnapshot weather, WeatherReminderOptions options,
        DateTimeOffset localNow, bool allowEvening = true)
    {
        var now = localNow.ToUniversalTime();
        // 缓存、过期的模型时刻或回拨的系统时间不参与变化判断。
        if (!WeatherService.ValidCity(weather.City) || !double.IsFinite(weather.Temperature) ||
            now.UtcDateTime - weather.UpdatedUtc > TimeSpan.FromMinutes(30) || weather.UpdatedUtc > now.UtcDateTime.AddMinutes(2) ||
            weather.ObservedAt is { } observed && (now - observed > TimeSpan.FromHours(1) || observed > now.AddMinutes(5))) return [];
        string city = CityKey(weather.City);
        bool changedCity = _state.City != city;
        if (changedCity)
        {
            _state.City = city; _state.Temperatures.Clear(); _state.ActiveHazards = []; _state.LastObservation = null;
        }
        if (_state.LastObservation > now.AddMinutes(2)) { _state.Temperatures.Clear(); _state.ActiveHazards = []; }
        if (_state.LastObservation < now.AddHours(-3)) _state.ActiveHazards = [];
        _state.LastObservation = now;
        foreach (string key in _state.Sent.Where(x => x.Value < now.AddDays(-7) || x.Value > now.AddMinutes(2)).Select(x => x.Key).ToArray()) _state.Sent.Remove(key);
        while (_state.Sent.Count > 64) _state.Sent.Remove(_state.Sent.OrderBy(x => x.Value).First().Key);
        var future = (weather.Hours ?? []).Where(x => x.Time > now && x.Time <= now.AddHours(3) && double.IsFinite(x.Temperature)).OrderBy(x => x.Time).ToArray();
        var reminders = new List<WeatherReminder>();
        var hazards = new Dictionary<string, string>();
        var hazardCodes = new Dictionary<string, int>();
        AddHazards(hazards, weather.Code, null, null, weather.WindSpeed, weather.WindGusts, "当前", hazardCodes);
        foreach (var hour in future) AddHazards(hazards, hour.Code, hour.RainProbability, hour.Precipitation, hour.WindSpeed, hour.WindGusts, "未来 3 小时", hazardCodes);
        // 雷雨或冻雨已经包含降雨，避免同一场天气同时弹多个提醒。
        if (hazards.ContainsKey("thunder") || hazards.ContainsKey("ice")) hazards.Remove("rain");
        var fresh = hazards.Where(x => !_state.ActiveHazards.Contains(x.Key) && Ready(city + "/" + x.Key, now, TimeSpan.FromHours(1))).ToArray();
        if (options.Conditions && fresh.Length > 0)
        {
            foreach (var item in fresh) _state.Sent[city + "/" + item.Key] = now;
            string iconHazard = new[] { "thunder", "ice", "snow", "rain", "fog", "wind" }.First(x => fresh.Any(y => y.Key == x));
            reminders.Add(new("conditions", weather.City.Name + " · 天气提醒", string.Join("；", fresh.Select(x => x.Value)), hazardCodes[iconHazard]));
        }
        _state.ActiveHazards = options.Conditions ? hazards.Keys.ToArray() : [];

        _state.Temperatures.RemoveAll(x => x.Time < now.AddHours(-3) || x.Time > now || !double.IsFinite(x.Temperature));
        if (options.Temperature)
        {
            var past = _state.Temperatures.Select(x => x.Temperature).ToArray();
            double fall = past.Length == 0 ? 0 : past.Max() - weather.Temperature;
            double rise = past.Length == 0 ? 0 : weather.Temperature - past.Min();
            double upcomingFall = future.Length == 0 ? 0 : weather.Temperature - future.Min(x => x.Temperature);
            double upcomingRise = future.Length == 0 ? 0 : future.Max(x => x.Temperature) - weather.Temperature;
            bool falling = Math.Max(fall, upcomingFall) >= Math.Max(rise, upcomingRise);
            double difference = falling ? Math.Max(fall, upcomingFall) : Math.Max(rise, upcomingRise);
            string key = city + (falling ? "/cooling" : "/warming");
            if (difference >= options.TemperatureThreshold && Ready(key, now, TimeSpan.FromHours(3)))
            {
                bool upcoming = falling ? upcomingFall > fall : upcomingRise > rise;
                string text = upcoming ? "未来 3 小时预计" : "近 3 小时已";
                reminders.Add(new("temperature", weather.City.Name + (falling ? " · 降温提醒" : " · 升温提醒"),
                    $"{text}{(falling ? "降温" : "升温")} {difference:0.#}°，当前 {weather.Temperature:0.#}°。" + (falling ? "外出记得添衣。" : "注意补水和防晒。"), weather.Code));
                _state.Sent[key] = now; _state.Temperatures.Clear();
            }
        }
        _state.Temperatures.Add(new(now, weather.Temperature));
        while (_state.Temperatures.Count > 16) _state.Temperatures.RemoveAt(0);

        if (allowEvening && EveningDue(weather.City, options, localNow))
        {
            var cityToday = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromSeconds(weather.UtcOffsetSeconds)).DateTime);
            var tomorrow = (weather.Days ?? []).FirstOrDefault(x => x.Date == cityToday.AddDays(1));
            if (tomorrow != null)
            {
                var advice = new Dictionary<string, string>();
                AddHazards(advice, tomorrow.Code, tomorrow.RainProbability, tomorrow.Precipitation, tomorrow.WindSpeed, tomorrow.WindGusts, "明天");
                if (advice.ContainsKey("thunder") || advice.ContainsKey("ice")) advice.Remove("rain");
                var today = (weather.Days ?? []).FirstOrDefault(x => x.Date == cityToday);
                if (today != null && today.High - tomorrow.High >= options.TemperatureThreshold) advice["cooling"] = "明天明显降温，记得添衣";
                string description = WeatherService.Describe(tomorrow.Code);
                // 建议放在前面，让小岛单行摘要也能看见“带伞 / 添衣”；完整温度仍留在通知记录里。
                string body = advice.Count == 0
                    ? $"明天 {description} · {tomorrow.Low:0}°～{tomorrow.High:0}°。"
                    : string.Join("；", advice.Values) + $"。预计 {tomorrow.Low:0}°～{tomorrow.High:0}°。";
                reminders.Add(new("evening", weather.City.Name + " · 明日天气", body, tomorrow.Code));
                _state.Sent[EveningKey(weather.City, localNow)] = now;
            }
        }
        return reminders;
    }

    private bool Ready(string key, DateTimeOffset now, TimeSpan cooldown) => !_state.Sent.TryGetValue(key, out var sent) || now - sent >= cooldown;
    private static void AddHazards(Dictionary<string, string> target, int code, double? probability, double? precipitation,
        double? wind, double? gusts, string when, Dictionary<string, int>? iconCodes = null)
    {
        void Add(string key, string body, int? iconCode = null)
        {
            if (target.TryAdd(key, body)) iconCodes?.TryAdd(key, iconCode ?? code);
        }
        if (code is 95 or 96 or 97 or 99) Add("thunder", when + "有雷雨，带伞并留意雷电，避免空旷处");
        else if (code is 56 or 57 or 66 or 67) Add("ice", when + "有冻雨，带伞并注意道路结冰");
        else if (code is 71 or 73 or 75 or 77 or 85 or 86) Add("snow", when + "有雪，注意保暖和路面湿滑");
        else if (code is 51 or 53 or 55 or 61 or 63 or 65 or 80 or 81 or 82 || probability >= 60 && precipitation >= 0.2)
            Add("rain", when + "可能降雨，出门记得带伞", WeatherIconPolicy.Resolve(code) is WeatherIconKind.Drizzle or WeatherIconKind.Rain ? code : 61);
        if (code is 45 or 48) Add("fog", when + "有雾，出行注意能见度");
        if (wind >= 39 || gusts >= 50) Add("wind", when + "风较大，留意高空坠物");
    }

    internal void Save(string path)
    {
        try { AtomicJsonFile.Write(path, JsonSerializer.Serialize(_state)); }
        catch (Exception ex) { IslandLog.Warn("Weather", "提醒状态保存失败: " + ex.Message); }
    }
    internal static WeatherReminderPolicy Load(string path)
    {
        try
        {
            var state = File.Exists(path) ? AtomicJsonFile.Read<WeatherReminderState>(path) : null;
            if (state != null && state.Temperatures != null && state.ActiveHazards != null && state.Sent != null && state.Sent.Count <= 256)
                return new(state);
        }
        catch (Exception ex) { IslandLog.Warn("Weather", "提醒状态不可用，将重新记录: " + ex.Message); }
        return new();
    }
}
