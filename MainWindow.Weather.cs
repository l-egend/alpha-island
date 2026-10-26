using System.IO;
using System.Windows.Controls;
using alpha_island.Core;
using alpha_island.Services;
using Windows.Devices.Geolocation;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using alpha_island.Controls;
using alpha_island.Animation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace alpha_island;

public partial class MainWindow
{
    private const string WeatherTickerKey = "weather";
    private readonly WeatherService _weatherService = new();
    private WeatherReminderPolicy _weatherReminders = new();
    private WeatherCity _weatherCity = WeatherCity.Beijing;
    private WeatherSnapshot? _weatherSnapshot;
    private bool _weatherBusy, _weatherLocated, _weatherConfirmed, _weatherAuto = true;
    private DateTime _weatherLastUpdate, _weatherNextAttempt;
    private DateTime _weatherLastLocationCheck;
    private TextBlock _weatherTitle = null!, _weatherValue = null!, _weatherDetail = null!;
    private SemanticIcon _weatherArtwork = null!;
    private TextBox _weatherQuery = null!;
    private ComboBox _weatherCities = null!;
    private static string WeatherReminderFile => Path.Combine(AppPaths.ConfigDir, "weather-reminders.json");

    private void RegisterWeatherPage()
    {
        _weatherAuto = SettingsRegistry.GetBool(_options, "WeatherAutoLocation", true);
        var saved = new WeatherCity(SettingsRegistry.GetText(_options, "WeatherCity", "北京"),
            SettingsRegistry.GetDouble(_options, "WeatherLatitude", 39.9042), SettingsRegistry.GetDouble(_options, "WeatherLongitude", 116.4074));
        if (WeatherService.ValidCity(saved) && (!_weatherAuto || SettingsRegistry.GetBool(_options, "WeatherLocationConfirmed", false)))
        { _weatherCity = saved; _weatherConfirmed = true; }
        if (_runtimeEnabled)
        {
            // 旧版本只在缓存里留下了自动定位城市，升级后先恢复该城市，后台不会申请定位权限。
            if (_weatherAuto && !_weatherConfirmed && _weatherService.LastCity() is { } lastCity)
            { _weatherCity = lastCity; _weatherConfirmed = true; }
            var cached = _weatherService.Cached(_weatherConfirmed ? _weatherCity : null);
            if (cached != null && (_weatherAuto || _weatherConfirmed))
            { _weatherCity = cached.City; _weatherSnapshot = cached; _weatherConfirmed = true; }
            _weatherReminders = WeatherReminderPolicy.Load(WeatherReminderFile);
        }
        var weather = new Grid { Margin = new System.Windows.Thickness(4, 4, 4, 12) };
        weather.RowDefinitions.Add(new RowDefinition { Height = System.Windows.GridLength.Auto });
        weather.RowDefinitions.Add(new RowDefinition());
        weather.RowDefinitions.Add(new RowDefinition { Height = System.Windows.GridLength.Auto });
        var summary = new Grid();
        summary.ColumnDefinitions.Add(new ColumnDefinition());
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = System.Windows.GridLength.Auto });
        var summaryText = new StackPanel();
        _weatherTitle = UtilityText("天气 · " + _weatherCity.Name, 14);
        _weatherTitle.ToolTip = "天气来源：Open-Meteo 预报 · 城市定位：Windows / BigDataCloud"; summaryText.Children.Add(_weatherTitle);
        _weatherValue = UtilityText("—", 26); summaryText.Children.Add(_weatherValue);
        summary.Children.Add(summaryText);
        _weatherArtwork = new SemanticIcon { Width = 60, Height = 60, Shape = IconShape.Weather,
            Foreground = ResolveAccentBrush(), Margin = new System.Windows.Thickness(18, 0, 6, 0),
            VerticalAlignment = System.Windows.VerticalAlignment.Center };
        Grid.SetColumn(_weatherArtwork, 1); summary.Children.Add(_weatherArtwork); weather.Children.Add(summary);
        ActionIconMotion.SetKind(_weatherArtwork, ActionIconAnimation.Pulse);
        _weatherDetail = UtilityText(_weatherConfirmed ? "正在获取天气…" : "搜索城市或使用定位。");
        var details = new ScrollViewer { Content = _weatherDetail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new System.Windows.Thickness(0, 4, 0, 0) };
        Grid.SetRow(details, 1); weather.Children.Add(details);
        var actions = new StackPanel(); Grid.SetRow(actions, 2); weather.Children.Add(actions);
        _weatherQuery = new TextBox { Text = _weatherCity.Name, FontSize = 11 };
        var search = WeatherActionRow(_weatherQuery, UtilityButton("搜索城市", async () => await SearchWeatherCitiesAsync()),
            UtilityButton("使用定位", async () => { if (_weatherBusy) return; _weatherAuto = true; _weatherLocated = false; await RefreshWeatherAsync(true, requestLocation: true); }),
            UtilityButton("刷新", async () => await RefreshWeatherAsync(true)));
        actions.Children.Add(search);
        _weatherCities = new ComboBox { FontSize = 11, DisplayMemberPath = "Display" };
        var cityRow = WeatherActionRow(_weatherCities, UtilityButton("选择", async () => await UseWeatherCityAsync()), UtilityButton("设置", () => { ShowSettings(); _settingsWindow?.OpenWeatherSettings(); })); actions.Children.Add(cityRow);
        RegisterUtility("weather", "天气", 8, weather, () => _ = RefreshWeatherAsync(false, requestLocation: !_weatherConfirmed), preferredHeight: 260);
        if (_weatherSnapshot != null) ShowWeather(_weatherSnapshot, "缓存 · " + _weatherSnapshot.UpdatedUtc.ToLocalTime().ToString("HH:mm"));
    }

    private static Grid WeatherActionRow(System.Windows.FrameworkElement field, params System.Windows.Controls.Button[] buttons)
    {
        var row = new Grid { Margin = new System.Windows.Thickness(0, 12, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        field.Width = double.NaN; field.Height = 32; field.Margin = new System.Windows.Thickness(0);
        field.HorizontalAlignment = HorizontalAlignment.Stretch; row.Children.Add(field);
        foreach (var button in buttons)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = System.Windows.GridLength.Auto });
            button.Margin = new System.Windows.Thickness(12, 0, 0, 0); button.Height = 32;
            Grid.SetColumn(button, row.ColumnDefinitions.Count - 1); row.Children.Add(button);
        }
        return row;
    }

    private void OnWeatherOptionsChanged()
    {
        if (!_runtimeEnabled || !IsLoaded || _ticker == null || _exiting) return;
        var options = WeatherReminderOptions.From(_options);
        if (!options.AutoRefresh) { StopWeatherPolling(); return; }
        if (!_ticker.IsRegistered(WeatherTickerKey))
            _ticker.Register(WeatherTickerKey, TimeSpan.FromMinutes(1), WeatherPollingTick, runImmediately: true);
        else WeatherPollingTick();
    }
    private void StopWeatherPolling() => _ticker?.Unregister(WeatherTickerKey);
    private void WeatherPollingTick()
    {
        var now = DateTime.UtcNow;
        if (_exiting || _weatherBusy || !_weatherConfirmed || now < _weatherNextAttempt) return;
        var options = WeatherReminderOptions.From(_options);
        if (!options.AutoRefresh) return;
        bool evening = !_sessionLocked && _weatherReminders.EveningDue(_weatherCity, options, DateTimeOffset.Now);
        if (now - _weatherLastUpdate >= TimeSpan.FromMinutes(options.RefreshMinutes) || evening)
            _ = RefreshWeatherAsync(true, background: true);
    }

    private async Task SearchWeatherCitiesAsync()
    {
        if (_weatherBusy || string.IsNullOrWhiteSpace(_weatherQuery.Text)) return;
        _weatherBusy = true;
        try
        {
            var cities = await _weatherService.SearchAsync(_weatherQuery.Text, _utilitiesLifetime.Token);
            if (_exiting) return;
            _weatherCities.ItemsSource = cities; _weatherCities.SelectedIndex = cities.Count > 0 ? 0 : -1;
            _weatherDetail.Text = cities.Count > 0 ? "选择城市后点击“选择”。" : "没有找到城市，可尝试城市拼音或英文名称。";
        }
        catch (OperationCanceledException) { if (!_exiting) _weatherDetail.Text = "搜索超时，请重试。"; }
        catch (Exception ex) { if (!_exiting) _weatherDetail.Text = "城市搜索失败：" + ex.Message; }
        finally { _weatherBusy = false; }
    }
    private async Task UseWeatherCityAsync()
    {
        if (_weatherBusy || _weatherCities.SelectedItem is not WeatherCity city || !WeatherService.ValidCity(city)) return;
        _weatherCity = city; _weatherAuto = false; _weatherConfirmed = true;
        _weatherLastUpdate = default; _weatherNextAttempt = default;
        SaveWeatherChoice(); await RefreshWeatherAsync(true);
    }
    private void SaveWeatherChoice()
    {
        SettingsRegistry.SetExtra(_options, "WeatherAutoLocation", _weatherAuto);
        SettingsRegistry.SetExtra(_options, "WeatherLocationConfirmed", _weatherConfirmed);
        if (_weatherConfirmed)
        {
            SettingsRegistry.SetExtra(_options, "WeatherCity", _weatherCity.Name);
            SettingsRegistry.SetExtra(_options, "WeatherLatitude", _weatherCity.Latitude);
            SettingsRegistry.SetExtra(_options, "WeatherLongitude", _weatherCity.Longitude);
        }
        if (_runtimeEnabled) _options.Save(allowRecovery: false);
    }
    private async Task RefreshWeatherAsync(bool force, bool background = false, bool requestLocation = false)
    {
        var options = WeatherReminderOptions.From(_options);
        if (_weatherBusy || _exiting || (!force && DateTime.UtcNow - _weatherLastUpdate < TimeSpan.FromMinutes(options.RefreshMinutes))) return;
        _weatherBusy = true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_utilitiesLifetime.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(35));
        string source = _weatherAuto ? "系统定位" : "手选城市";
        string previousCityName = _weatherCity.Name;
        try
        {
            if (!background) _weatherDetail.Text = "正在更新天气…";
            // 只有用户明确操作才申请权限；后台每 6 小时尝试更新已授权的坐标，失败继续使用已确认的城市。
            if (_weatherAuto && (requestLocation || _weatherConfirmed && (!_weatherLocated || DateTime.UtcNow - _weatherLastLocationCheck > TimeSpan.FromHours(6))))
            {
                _weatherLastLocationCheck = DateTime.UtcNow;
                try
                {
                    bool allowed = true;
                    if (requestLocation)
                    {
                        Activate();
                        allowed = await Geolocator.RequestAccessAsync().AsTask(cts.Token) == GeolocationAccessStatus.Allowed;
                    }
                    if (allowed)
                    {
                        var position = await new Geolocator { DesiredAccuracyInMeters = 1000 }.GetGeopositionAsync(
                            TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(8)).AsTask(cts.Token);
                        var point = position.Coordinate.Point.Position;
                        var located = new WeatherCity("系统定位地区", point.Latitude, point.Longitude);
                        if (!WeatherService.ValidCity(located)) throw new InvalidOperationException("定位坐标无效");
                        if (WeatherService.SameCity(located, _weatherCity)) located = _weatherCity;
                        else try { located = await _weatherService.NameCurrentLocationAsync(point.Latitude, point.Longitude, cts.Token); }
                            catch (Exception ex) when (ex is not OperationCanceledException) { IslandLog.Warn("Weather", "城市名称读取失败: " + ex.Message); }
                        _weatherCity = located; _weatherConfirmed = true;
                        SaveWeatherChoice();
                    }
                    else source = _weatherConfirmed ? "定位不可用，保留上次城市" : "定位不可用，请选择城市";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { source = _weatherConfirmed ? "定位不可用，保留上次城市" : "定位不可用，请选择城市"; IslandLog.Warn("Weather", "定位失败: " + ex.Message); }
                // 本次后台不再重复尝试；下一次 6 小时检查允许重新定位。
                _weatherLocated = true;
            }
            if (!_weatherConfirmed)
            { _weatherDetail.Text = "搜索城市或使用定位。"; return; }
            var snapshot = await _weatherService.GetAsync(_weatherCity, cts.Token);
            if (_exiting) return;
            _weatherSnapshot = snapshot; _weatherLastUpdate = snapshot.UpdatedUtc;
            _weatherNextAttempt = DateTime.UtcNow.AddMinutes(5);
            if (_weatherQuery.Text == previousCityName) _weatherQuery.Text = _weatherCity.Name;
            ShowWeather(snapshot, source + " · 更新 " + snapshot.UpdatedUtc.ToLocalTime().ToString("HH:mm"));
            options = WeatherReminderOptions.From(_options);
            if (background && !options.AutoRefresh) return;
            var reminders = _weatherReminders.Evaluate(snapshot, options, DateTimeOffset.Now, allowEvening: !_sessionLocked);
            foreach (var reminder in reminders)
            {
                var item = reminder.ToNotification(DateTimeOffset.Now);
                _desktopInbox.Add(item, _desktopPolicy.CanPopup(item, _sessionLocked), _desktopPolicy);
            }
            if (reminders.Count > 0) { StartNotificationUiTimer(); RefreshDesktopInbox(); TickDesktopNotifications(); }
            if (_runtimeEnabled) await Task.Run(() => _weatherReminders.Save(WeatherReminderFile));
        }
        catch (Exception ex)
        {
            if (_exiting) return;
            _weatherNextAttempt = DateTime.UtcNow.AddMinutes(5);
            var cached = _weatherService.Cached(_weatherCity);
            if (cached != null) ShowWeather(cached, "离线缓存 · " + cached.UpdatedUtc.ToLocalTime().ToString("MM-dd HH:mm"));
            else { _weatherTitle.Text = "天气 · " + _weatherCity.Name; _weatherValue.Text = "—"; _weatherDetail.Text = "天气暂不可用，将稍后重试。"; }
            IslandLog.Warn("Weather", "更新失败: " + ex.Message);
        }
        finally { _weatherBusy = false; }
    }
    private void ShowWeather(WeatherSnapshot weather, string source)
    {
        _weatherTitle.Text = "天气 · " + weather.City.Name;
        _weatherValue.Text = $"{weather.Temperature:0.#}° · {weather.Description}";
        bool conditionChanged = _weatherArtwork.WeatherCode != weather.Code;
        _weatherArtwork.WeatherCode = weather.Code;
        if (conditionChanged) ActionIconMotion.Play(_weatherArtwork);
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromSeconds(weather.UtcOffsetSeconds)).DateTime);
        var tomorrow = (weather.Days ?? []).FirstOrDefault(x => x.Date == today.AddDays(1));
        _weatherDetail.Text = $"{weather.Low:0}°～{weather.High:0}° · 体感 {weather.FeelsLike:0}°" +
            (tomorrow == null ? "" : $"\n明天 {WeatherService.Describe(tomorrow.Code)} · {tomorrow.Low:0}°～{tomorrow.High:0}°") + $"\n{source}";
        UpdateCardPinSummary("weather");
    }
}
