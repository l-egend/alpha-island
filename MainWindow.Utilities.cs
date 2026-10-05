using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using alpha_island.Controls;
using alpha_island.Core;
using alpha_island.Services;
using Windows.Devices.Geolocation;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace alpha_island;

public partial class MainWindow
{
    private readonly WeatherService _weatherService = new();
    private readonly CancellationTokenSource _utilitiesLifetime = new();
    private WeatherCity _weatherCity = WeatherCity.Beijing;
    private bool _weatherBusy;
    private bool _weatherLocated;
    private bool _weatherAuto = true;
    private DateTime _weatherLastUpdate;
    private TextBlock _weatherTitle = null!;
    private TextBlock _weatherValue = null!;
    private TextBlock _weatherDetail = null!;
    private TextBox _weatherQuery = null!;
    private ComboBox _weatherCities = null!;
    private readonly PomodoroTimer _pomodoro = new();
    private DispatcherTimer? _pomodoroTicker;
    private TextBlock _focusValue = null!;
    private TextBlock _focusDetail = null!;
    private Button _focusButton = null!;
    private ScreenToolsWindow? _screenTools;

    private static TextBlock UtilityText(string text, double size = 12) => new()
    { Text = text, FontSize = size, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 3) };
    private static Button UtilityButton(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 2, 6, 2), FontSize = 11 };
        button.Click += (_, _) => action(); return button;
    }
    private static StackPanel UtilityButtons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in buttons) panel.Children.Add(button);
        return panel;
    }
    private void RegisterUtility(string id, string title, int order, StackPanel content, Action? activated = null)
    {
        var view = new Grid { Height = 194, VerticalAlignment = VerticalAlignment.Center, RenderTransform = new TranslateTransform() };
        view.Children.Add(content); CardViewport.Children.Add(view);
        _pages.Register(new DelegateIslandPage(id, title, view, order, IslandMetrics.CardHeightLyrics, activated: activated,
            viewportChanged: height => view.Height = height));
    }
    private void RegisterUtilityPages()
    {
        _weatherAuto = SettingsRegistry.GetBool(_options, "WeatherAutoLocation", true);
        if (!_weatherAuto)
        {
            var manual = new WeatherCity(SettingsRegistry.GetText(_options, "WeatherCity", "北京"),
                SettingsRegistry.GetDouble(_options, "WeatherLatitude", 39.9042), SettingsRegistry.GetDouble(_options, "WeatherLongitude", 116.4074));
            if (WeatherService.ValidCity(manual)) _weatherCity = manual;
        }
        var weather = new StackPanel();
        _weatherTitle = UtilityText("天气 · 北京", 14);
        _weatherTitle.ToolTip = "天气来源：Open-Meteo · 城市定位：BigDataCloud"; weather.Children.Add(_weatherTitle);
        _weatherValue = UtilityText("—", 26); weather.Children.Add(_weatherValue);
        _weatherDetail = UtilityText("正在获取天气…"); weather.Children.Add(_weatherDetail);
        _weatherQuery = new TextBox { Width = 100, Text = _weatherCity.Name, Margin = new Thickness(0, 2, 6, 2), FontSize = 11 };
        var search = UtilityButtons(UtilityButton("搜索城市", async () => await SearchWeatherCitiesAsync()),
            UtilityButton("使用定位", async () => { if (_weatherBusy) return; _weatherAuto = true; _weatherLocated = false; await RefreshWeatherAsync(true); }),
            UtilityButton("刷新", async () => await RefreshWeatherAsync(true)));
        search.Children.Insert(0, _weatherQuery); weather.Children.Add(search);
        _weatherCities = new ComboBox { Width = 240, FontSize = 11, DisplayMemberPath = "Display", HorizontalAlignment = HorizontalAlignment.Left };
        var cityRow = UtilityButtons(UtilityButton("选择", async () => await UseWeatherCityAsync())); cityRow.Children.Insert(0, _weatherCities); weather.Children.Add(cityRow);
        RegisterUtility("weather", "天气", 8, weather, () => _ = RefreshWeatherAsync(false));

        var focus = new StackPanel(); focus.Children.Add(UtilityText("番茄钟", 15));
        _pomodoro.Configure(PomodoroOptions.From(_options));
        if (_runtimeEnabled && _pomodoro.Options.AutoStart) _pomodoro.Start(DateTime.UtcNow);
        _focusValue = UtilityText("25:00", 36); focus.Children.Add(_focusValue);
        _focusDetail = UtilityText("专注 · 已完成 0 轮"); focus.Children.Add(_focusDetail);
        _focusButton = UtilityButton("开始专注", ToggleFocus);
        focus.Children.Add(UtilityButtons(_focusButton, UtilityButton("重置", () => { _pomodoro.Reset(); SyncFocus(); }),
            UtilityButton("设置", OpenFocusSettings)));
        focus.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2 || IsFocusControl(e.OriginalSource)) return;
            e.Handled = true;
            if (_pomodoro.Options.DoubleClickOpensSettings) OpenFocusSettings(); else ToggleFocus();
        };
        RegisterUtility("focus", "番茄钟", 9, focus, SyncFocus);
        _pomodoroTicker = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _pomodoroTicker.Tick += (_, _) =>
        {
            if (_pomodoro.Tick(DateTime.UtcNow))
            {
                ShowSystemNotify("\uE916", _pomodoro.CycleFinished ? "番茄钟循环完成" : _pomodoro.IsBreak ? "专注完成" : "休息结束",
                    _pomodoro.CycleFinished ? $"已完成 {_pomodoro.CompletedFocus} 轮专注"
                        : $"{(_pomodoro.IsLongBreak ? "长休息" : _pomodoro.IsBreak ? "短休息" : "专注")} {Math.Ceiling(_pomodoro.Remaining.TotalMinutes)} 分钟"
                            + (_pomodoro.Running ? " · 已自动开始" : " · 点击开始"),
                    _pomodoro.IsBreak ? "休息" : "专注");
                if (_pomodoro.Options.PlaySound) PomodoroSound.Play(_pomodoro.Options.Sound);
            }
            SyncFocus();
        };
        _pomodoroTicker.Start();
        SyncFocus();

        var tools = new StackPanel(); tools.Children.Add(UtilityText("屏幕工具", 15));
        tools.Children.Add(UtilityButtons(UtilityButton("翻译 / 字幕 / 扫码", OpenScreenTools)));
        tools.Children.Add(UtilityText("翻译需配置 AI 接口", 11));
        RegisterUtility("screen-tools", "屏幕翻译与扫码", 10, tools);
    }

    private void OpenScreenTools()
    {
        if (_screenTools == null)
        {
            _screenTools = new ScreenToolsWindow(() => ChatConfig.From(_options));
            _screenTools.Closed += (_, _) => _screenTools = null;
        }
        _screenTools.Show(); _screenTools.Activate();
    }
    private void ToggleFocus()
    {
        if (_pomodoro.Running) _pomodoro.Pause(DateTime.UtcNow); else _pomodoro.Start(DateTime.UtcNow);
        SyncFocus();
    }
    private void SyncFocus()
    {
        if (_focusValue == null) return;
        _focusValue.Text = _pomodoro.DisplayRemaining();
        _focusButton.Content = _pomodoro.Running ? "暂停" : _pomodoro.CycleFinished ? "重新开始" : (_pomodoro.IsBreak ? "开始休息" : "开始专注");
        _focusDetail.Text = $"{(_pomodoro.IsLongBreak ? "长休息" : _pomodoro.IsBreak ? "短休息" : "专注阶段")} · 已完成 {_pomodoro.CompletedFocus} 轮"
            + (_pomodoro.Options.AutoCycle ? $" / {_pomodoro.Options.CycleCount} 轮计划" : "");
        UpdateCardPinSummary("focus");
    }
    private static bool IsFocusControl(object source)
    {
        var node = source as DependencyObject;
        while (node != null)
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase) return true;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : null;
        }
        return false;
    }
    private void OpenFocusSettings()
    {
        ShowSettings();
        _settingsWindow?.OpenPomodoroSettings();
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
        catch (Exception ex) { _weatherDetail.Text = "城市搜索失败：" + ex.Message; }
        finally { _weatherBusy = false; }
    }
    private async Task UseWeatherCityAsync()
    {
        if (_weatherBusy || _weatherCities.SelectedItem is not WeatherCity city) return;
        _weatherCity = city; _weatherAuto = false;
        SaveWeatherChoice(); await RefreshWeatherAsync(true);
    }
    private void SaveWeatherChoice()
    {
        SettingsRegistry.SetExtra(_options, "WeatherAutoLocation", _weatherAuto);
        if (!_weatherAuto)
        {
            SettingsRegistry.SetExtra(_options, "WeatherCity", _weatherCity.Name);
            SettingsRegistry.SetExtra(_options, "WeatherLatitude", _weatherCity.Latitude);
            SettingsRegistry.SetExtra(_options, "WeatherLongitude", _weatherCity.Longitude);
        }
        _options.Save(allowRecovery: true);
    }
    private async Task RefreshWeatherAsync(bool force)
    {
        if (_weatherBusy || _exiting || (!force && DateTime.UtcNow - _weatherLastUpdate < TimeSpan.FromMinutes(15))) return;
        _weatherBusy = true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_utilitiesLifetime.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(35));
        string source = _weatherAuto ? "系统定位" : "手选城市";
        try
        {
            _weatherDetail.Text = "正在更新天气…";
            if (_weatherAuto && !_weatherLocated)
            {
                _weatherCity = WeatherCity.Beijing;
                try
                {
                    Activate();
                    if (await Geolocator.RequestAccessAsync().AsTask(cts.Token) == GeolocationAccessStatus.Allowed)
                    {
                        var position = await new Geolocator { DesiredAccuracyInMeters = 1000 }.GetGeopositionAsync(
                            TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10)).AsTask(cts.Token);
                        var point = position.Coordinate.Point.Position;
                        _weatherCity = new WeatherCity("系统定位地区", point.Latitude, point.Longitude);
                        try { _weatherCity = await _weatherService.NameCurrentLocationAsync(point.Latitude, point.Longitude, cts.Token); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { IslandLog.Warn("Weather", "定位城市名称读取失败: " + ex.Message); }
                        _weatherLocated = true;
                    }
                    else source = "定位不可用，默认北京";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { source = "定位不可用，默认北京"; IslandLog.Warn("Weather", "定位失败: " + ex.Message); }
                // 仅显式选城市/切换定位时写设置，首次打开天气页不覆盖损坏设置。
                if (force) SaveWeatherChoice();
            }
            var snapshot = await _weatherService.GetAsync(_weatherCity, cts.Token);
            if (_exiting) return;
            _weatherLastUpdate = snapshot.UpdatedUtc;
            ShowWeather(snapshot, source);
        }
        catch (Exception ex)
        {
            if (_exiting) return;
            var cached = _weatherService.Cached(_weatherCity);
            if (cached != null) ShowWeather(cached, "离线缓存 · " + cached.UpdatedUtc.ToLocalTime().ToString("MM-dd HH:mm"));
            else { _weatherTitle.Text = "天气 · " + _weatherCity.Name; _weatherValue.Text = "—"; _weatherDetail.Text = "天气暂不可用，请稍后刷新。"; }
            IslandLog.Warn("Weather", "更新失败: " + ex.Message);
        }
        finally { _weatherBusy = false; }
    }
    private void ShowWeather(WeatherSnapshot weather, string source)
    {
        _weatherTitle.Text = "天气 · " + weather.City.Name;
        _weatherValue.Text = $"{weather.Temperature:0.#}° · {weather.Description}";
        _weatherDetail.Text = $"{weather.Low:0}°～{weather.High:0}° · 体感 {weather.FeelsLike:0}°\n{source}";
        UpdateCardPinSummary("weather");
    }
    private void StopUtilities()
    {
        _pomodoroTicker?.Stop(); _screenTools?.Close(); _utilitiesLifetime.Cancel(); _weatherService.Dispose();
    }
}
