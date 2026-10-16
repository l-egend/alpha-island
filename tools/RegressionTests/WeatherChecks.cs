using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Services;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;

internal static partial class Program
{
    private sealed class ForecastHandler(string json) : HttpMessageHandler
    {
        public Uri? Requested { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Requested = request.RequestUri; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }); }
    }
    private static async Task WeatherReminderChecks(string root)
    {
        var now = new DateTimeOffset(2026, 10, 7, 19, 0, 0, TimeSpan.FromHours(8));
        var options = WeatherReminderOptions.From(new IslandOptions());
        var snapshot = new WeatherSnapshot(new("广州", 23.4, 113.46), 26, 27, 0, 20, 28, now.UtcDateTime)
        {
            UtcOffsetSeconds = 28800, ObservedAt = now,
            Days = [new(new(2026, 10, 7), 0, 20, 28, 0, 0, 10, 15), new(new(2026, 10, 8), 63, 18, 22, 85, 12, 12, 20)]
        };
        WeatherSnapshot At(DateTimeOffset time, double temp = 26, int code = 0) => snapshot with { UpdatedUtc = time.UtcDateTime, ObservedAt = time, Temperature = temp, Code = code };
        var policy = new WeatherReminderPolicy();
        Check(options.RefreshMinutes == 15 && options.TemperatureThreshold == 5 && options.EveningHour == 20, "默认刷新 15 分钟、温差 5°C、晚间 20 点");
        Check(policy.Evaluate(snapshot, options, now).Count == 0, "晴天与稳定温度不弹提醒，20 点前不发明日预报");
        var rain = policy.Evaluate(At(now.AddMinutes(15), code: 63), options, now.AddMinutes(15));
        Check(rain.Count == 1 && rain[0].Body.Contains("带伞"), "新出现降雨及时给出带伞建议");
        Check(rain[0].WeatherCode == 63 && rain[0].ToNotification(now).WeatherCode == 63, "降雨提醒将预报码传到通知，不丢失类型");
        Check(policy.Evaluate(At(now.AddMinutes(30), code: 63), options, now.AddMinutes(30)).Count == 0, "持续下雨不会每次刷新重复提醒");
        var thunder = policy.Evaluate(At(now.AddMinutes(35), code: 97), options, now.AddMinutes(35));
        Check(thunder.Count == 1 && thunder[0].Body.Contains("雷电"), "降雨升级为雷雨再次提醒，并支持 WMO 97");
        var cold = policy.Evaluate(At(now.AddMinutes(40), temp: 20, code: 97), options, now.AddMinutes(40));
        Check(cold.Count == 1 && cold[0].Key == "temperature" && cold[0].Body.Contains("添衣"), "短时间降温 6°C 提醒添衣，持续雷雨不重复");
        Check(policy.Evaluate(At(now.AddMinutes(45), temp: 18, code: 97), options, now.AddMinutes(45)).Count == 0, "同方向温差提醒有冷却时间");
        var twenty = now.AddHours(1);
        var evening = policy.Evaluate(At(twenty, temp: 18, code: 97), options, twenty);
        Check(evening.Count == 1 && evening[0].Key == "evening" && evening[0].Body.Contains("18°～22°") && evening[0].Body.Contains("带伞") && evening[0].Body.Contains("添衣"), "20 点提示明日温度、带伞和明显降温建议");
        Check(evening[0].WeatherCode == 63 && evening[0].ToNotification(twenty).WeatherCode == 63, "当前雷雨、明日下雨时通知保留明日雨码，不使用当前雷雨图标");
        var sunnyTomorrow = At(twenty, code: 63) with { Days = [new(new(2026, 10, 8), 0, 20, 31, 0, 0, 10, 15)] };
        var sunnyReminder = new WeatherReminderPolicy().Evaluate(sunnyTomorrow, options, twenty).Single(x => x.Key == "evening");
        Check(sunnyReminder.WeatherCode == 0 && sunnyReminder.Body.Contains("晴") && sunnyReminder.ToNotification(twenty).WeatherCode == 0,
            "当前下雨但明日晴时，明日提醒为太阳并保留 20°～31°");
        Check(!policy.EveningDue(snapshot.City, options, twenty.AddMinutes(1)), "当晚明日预报只发送一次");
        string state = Path.Combine(root, "weather-reminders.json"); policy.Save(state);
        var restored = WeatherReminderPolicy.Load(state);
        Check(!restored.EveningDue(snapshot.City, options, twenty.AddHours(1)), "重启后从 config 去重状态恢复晚间提醒记录");
        Check(restored.EveningDue(snapshot.City, options, twenty.AddDays(1)), "次日晚间正常重新提醒");
        Check(!restored.EveningDue(snapshot.City, options, twenty.AddHours(5)), "午夜后不补弹昨晚的明日预报");
        var late = new WeatherReminderPolicy();
        Check(late.Evaluate(At(twenty.AddHours(2)), options, twenty.AddHours(2)).Any(x => x.Key == "evening"), "当晚较晚启动或唤醒仍补发当天明日预报");
        var locked = new WeatherReminderPolicy();
        Check(!locked.Evaluate(At(twenty), options, twenty, allowEvening: false).Any(x => x.Key == "evening") && locked.EveningDue(snapshot.City, options, twenty), "锁屏不消费晚间提醒，解锁后仍待发送");
        var stale = new WeatherReminderPolicy();
        Check(stale.Evaluate(snapshot with { Code = 63 }, options, twenty).Count == 0 && stale.EveningDue(snapshot.City, options, twenty), "旧缓存不触发天气或晚间提醒，也不消耗发送资格");
        Check(stale.Evaluate(At(twenty, code: 63) with { ObservedAt = twenty.AddHours(-2) }, options, twenty).Count == 0, "刚下载但模型时间过期的数据不触发提醒");
        var switching = new WeatherReminderPolicy(); switching.Evaluate(snapshot, options, now);
        Check(switching.Evaluate(At(now.AddMinutes(15), 10) with { City = new("北京", 39.9, 116.4) }, options, now.AddMinutes(15)).Count == 0, "切换城市不把两地气温差当作突然降温");
        var disabled = options with { Conditions = false, Temperature = false, Evening = false };
        Check(new WeatherReminderPolicy().Evaluate(At(twenty, 5, 95), disabled, twenty).Count == 0, "关闭提醒后雷雨、温差和明日预报均不发送");
        var next = At(now) with { Hours = [new(now.AddHours(1), 19, 61, 75, 2, 10, 15)] };
        var ahead = new WeatherReminderPolicy().Evaluate(next, options, now);
        Check(ahead.Count == 2 && ahead.Any(x => x.Body.Contains("未来 3 小时预计降温")) && ahead.Any(x => x.Body.Contains("带伞")), "未来几小时的降温、降雨可提前提醒");
        Check(ahead.Single(x => x.Key == "conditions").WeatherCode == 61, "当前晴但未来降雨的提醒选用未来雨码");
        var probableRain = At(now) with { Hours = [new(now.AddHours(1), 26, 3, 80, 2, 10, 15)] };
        Check(new WeatherReminderPolicy().Evaluate(probableRain, options, now).Single().WeatherCode == 61,
            "由高降雨概率和降水量生成的提醒显示雨形，避免正文提醒带伞却显示阴天");
        var distant = At(now) with { Hours = [new(now.AddHours(5), 19, 61, 75, 2, 10, 15)] };
        Check(new WeatherReminderPolicy().Evaluate(distant, options, now).Count == 0, "白天不误用 3 小时以外的天气作为即将变化");
        Check(new WeatherReminderPolicy().Evaluate(At(now, code: 75), options, now).Single().Body.Contains("路面湿滑"), "降雪提供保暖和路面湿滑建议");
        Check(new WeatherReminderPolicy().Evaluate(At(now) with { WindGusts = 55 }, options, now).Single().Body.Contains("风较大"), "较强阵风触发大风提醒");
        Check(new WeatherReminderPolicy().Evaluate(At(now) with { Hours = [new(now.AddHours(1), 26, 3, 20, 0, 10, 15)] }, options, now).Count == 0, "低概率且无降水量的阴天不误报带伞");
        var noTomorrow = new WeatherReminderPolicy();
        noTomorrow.Evaluate(At(twenty) with { Days = [] }, options, twenty);
        Check(noTomorrow.EveningDue(snapshot.City, options, twenty), "明日数据缺失时不标记已提醒，之后可重试");
        File.WriteAllText(state, "broken");
        Check(WeatherReminderPolicy.Load(state).EveningDue(snapshot.City, options, twenty), "损坏的去重文件安全恢复，天气服务继续工作");

        long midnight = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds();
        string json = JsonSerializer.Serialize(new {
            utc_offset_seconds = 28800,
            current = new { time = now.ToUnixTimeSeconds(), temperature_2m = 26, apparent_temperature = 27, weather_code = 0, wind_speed_10m = 10, wind_gusts_10m = 15 },
            hourly = new { time = new[] { now.AddHours(1).ToUnixTimeSeconds() }, temperature_2m = new[] { 19.0 }, weather_code = new[] { 61 }, precipitation_probability = new[] { 75 }, precipitation = new[] { 2.0 }, wind_speed_10m = new[] { 10.0 }, wind_gusts_10m = new double?[] { null } },
            daily = new { time = new[] { midnight, midnight + 86400 }, temperature_2m_min = new[] { 20.0, 18.0 }, temperature_2m_max = new[] { 28.0, 22.0 }, weather_code = new[] { 0, 63 }, precipitation_probability_max = new[] { 0, 85 }, precipitation_sum = new[] { 0.0, 12.0 }, wind_speed_10m_max = new[] { 10.0, 12.0 }, wind_gusts_10m_max = new[] { 15.0, 20.0 } } });
        var parsed = WeatherService.Parse(json, snapshot.City, now.UtcDateTime);
        Check(parsed.Days[1].Date == new DateOnly(2026, 10, 8), "Unix 日期按城市时区解析，避免中国明日预报偏移一天");
        Check(parsed.Hours[0].Time == now.AddHours(1) && parsed.Hours[0].RainProbability == 75 && parsed.Hours[0].WindGusts == null, "逐小时预报、降雨概率和缺失阵风值正确解析");
        Check(parsed.ObservedAt == now && parsed.WindSpeed == 10 && parsed.Days[1].Precipitation == 12, "当前模型时间、风速和每日降水量正确解析");
        using var handler = new ForecastHandler(json); using var client = new HttpClient(handler);
        using var service = new WeatherService(client, Path.Combine(root, "forecast.json"));
        await service.GetAsync(snapshot.City, CancellationToken.None);
        Check(handler.Requested!.Query.Contains("forecast_days=3") && handler.Requested.Query.Contains("hourly=") && handler.Requested.Query.Contains("timeformat=unixtime"), "正式请求包含未来预报并显式指定单位和时间格式");
        Check(service.Cached(snapshot.City)?.Days.Length == 2 && service.LastCity()?.Name == "广州", "预报缓存保留明日数据和上次定位城市");
    }

    private static void WeatherUiChecks(alpha_island.MainWindow window, Grid root)
    {
        SetAgentField(window, "_weatherBusy", true); // UI 验证不访问网络或真实用户定位。
        var options = AgentField<IslandOptions>(window, "_options");
        var settings = new alpha_island.Settings.SettingsWindow(persistSettings: false);
        try
        {
            settings.LoadFrom(options);
            foreach (var descriptor in SettingsRegistry.All.Where(x => x.Key.StartsWith("Weather") && x.ControlName.Length > 0))
                Check(settings.FindName(descriptor.ControlName) != null && descriptor.PageId == SettingsPageIds.Behavior, "天气设置控件已绑定: " + descriptor.Key);
            Check(((CheckBox)settings.FindName("WeatherAutoRefreshToggle")).IsChecked == true && ((TextBox)settings.FindName("WeatherEveningHourBox")).Text == "20", "设置界面加载默认开关与晚间时间");
        }
        finally { settings.ForceClose = true; settings.Close(); }
        var notification = new WeatherReminder("evening", "广州 · 明日天气", "明天有雨，出门记得带伞。", 63).ToNotification(DateTimeOffset.Now);
        var inbox = AgentField<DesktopNotificationInbox>(window, "_desktopInbox");
        PrivateCall(window, "ClearDesktopInbox"); PrivateCall(window, "SetState", IslandState.Bar, true);
        inbox.Add(notification, true); PrivateCall(window, "StartNotificationUiTimer"); PrivateCall(window, "TickDesktopNotifications");
        var pill = AgentField<alpha_island.Controls.DesktopNotificationPill>(window, "_notificationSummary");
        Check(AgentField<IslandState>(window, "_state") == IslandState.Pill && pill.Title.Text.Contains("明日天气") && pill.Detail.Text.Contains("带伞"), "天气自动提醒采用小岛图标与文字形态");
        Check(pill.Icon.Text == "\uE9BD" && !notification.CanOpenApp, "天气提醒使用天气图标，不复用应用通知图标");
        Check(pill.Icon.ResolvedShape == alpha_island.Controls.IconShape.Weather && pill.Icon.WeatherCode == 63,
            "明日天气通知使用独立天气轮廓，不通过通用字体绘制缺字方框");
        var card = new alpha_island.Controls.DesktopNotificationCard(notification);
        Check(card.OpenButton?.Content?.ToString() == "查看天气", "通知中心天气记录提供查看天气按钮");
        PrivateCall(window, "OpenDesktopNotificationApp", notification);
        Check(AgentField<IslandPageHost>(window, "_pages").ActiveId == "weather", "点击天气通知记录可直接打开天气卡片");
        var snapshot = new WeatherSnapshot(new("广州", 23.4, 113.46), 26, 27, 0, 20, 28, DateTime.UtcNow) {
            UtcOffsetSeconds = 28800, Days = [new(DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8)).AddDays(1), 63, 18, 22, 85, 12, 10, 15)] };
        PrivateCall(window, "ShowWeather", snapshot, "更新 20:00");
        var artwork = AgentField<alpha_island.Controls.SemanticIcon>(window, "_weatherArtwork");
        root.UpdateLayout();
        Check(artwork.Shape == alpha_island.Controls.IconShape.Weather && artwork.WeatherCode == 0
            && artwork.ActualWidth == 60 && artwork.TranslatePoint(new System.Windows.Point(), window.IslandBody).X
                > AgentField<TextBlock>(window, "_weatherValue").TranslatePoint(new System.Windows.Point(), window.IslandBody).X,
            "展开天气卡片右侧增加当前天气图形，与明日通知各自对应当前和明日的天气");
        Check(AgentField<TextBlock>(window, "_weatherDetail").Text.Contains("明天 雨 · 18°～22°"), "天气卡片同时显示明日天气和温度范围");
        Directory.CreateDirectory("bin/launch-previews");
        PumpAnimations(600);
        SaveThemePreview(window.IslandBody, "bin/launch-previews/weather-card.png");
        PrivateCall(window, "StopWeatherPolling");
    }

    private static async Task WeatherLiveChecks(string root)
    {
        using var service = new WeatherService(cachePath: Path.Combine(root, "weather-live.json"));
        var result = await service.GetAsync(new("广州市", 23.40377, 113.462463), CancellationToken.None);
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromSeconds(result.UtcOffsetSeconds)).DateTime);
        Check(result.Days.Length == 3 && result.Days.Any(x => x.Date == today.AddDays(1)), "真实天气接口可获取广州当前与明日预报");
        Check(result.Hours.Length == 72 && result.Hours.Any(x => x.Time > DateTimeOffset.UtcNow), "真实接口返回完整未来逐小时预报");
        Check(result.ObservedAt != null && DateTimeOffset.UtcNow - result.ObservedAt < TimeSpan.FromHours(1), "真实当前天气的模型时间新鲜，提醒可参与判断");
        var options = WeatherReminderOptions.From(new IslandOptions());
        var reminders = new WeatherReminderPolicy().Evaluate(result, options, DateTimeOffset.Now);
        Check(DateTimeOffset.Now.Hour < 20 || reminders.Any(x => x.Key == "evening"), "真实明日预报可生成当晚提醒建议");
        Console.WriteLine("Forecast verified: " + result.Description + ", tomorrow " + WeatherService.Describe(result.Days.First(x => x.Date == today.AddDays(1)).Code));
    }
}
