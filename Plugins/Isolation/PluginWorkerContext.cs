using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AlphaIsland.PluginSdk;

namespace alpha_island.Plugins.Isolation;

internal sealed class PluginWorkerContext(PluginStart start, Dispatcher dispatcher, Action<RemoteEvent> publish,
    CancellationToken token) : IPluginContext
{
    private Dictionary<string, JsonElement> _values = start.Configuration;
    private (string State, string Detail)? _lastActivity;
    internal List<PluginPage> Pages { get; } = new();
    internal List<PluginSetting> Settings { get; } = new();
    internal List<PluginAction> Actions { get; } = new();
    internal List<PluginService> Services { get; } = new();
    internal List<PluginNetworkSource> Networks { get; } = new();
    internal Func<FrameworkElement>? SettingsView { get; private set; }
    internal bool Sealed;
    public string PluginId => start.PluginId;
    public string PluginDirectory => start.Directory;
    public string DataDirectory => start.DataRoot;
    public string ConfigDirectory => Path.Combine(DataDirectory, "config");
    public Dispatcher Dispatcher => dispatcher;
    public CancellationToken Lifetime => token;
    public IPluginScheduledTask Schedule(string key, TimeSpan interval, Action callback, bool initiallyPaused = false)
    {
        if (!PluginManifest.ValidKey(key) || interval <= TimeSpan.Zero) throw new ArgumentException("插件采样订阅无效");
        return new WorkerScheduledTask(this, key, interval, callback, initiallyPaused);
    }
    private void Validate(string key)
    {
        Dispatcher.VerifyAccess();
        if (Sealed || !PluginManifest.ValidKey(key)) throw new InvalidOperationException("插件注册阶段或组件标识无效");
    }
    public void RegisterPage(PluginPage page)
    {
        Validate(page.Key);
        if (Pages.Any(p => p.Key == page.Key) || string.IsNullOrWhiteSpace(page.Title) || page.CreateView == null
            || !double.IsFinite(page.Height) || page.Height is < 214 or > 320 || page.Order < 100) throw new InvalidDataException("插件页面声明无效");
        Pages.Add(page);
    }
    public void RegisterSetting(PluginSetting setting)
    { Validate(setting.Key); if (Settings.Any(p => p.Key == setting.Key)) throw new InvalidDataException("插件设置重复"); PluginHost.NormalizeValue(setting, PluginWire.Element(setting.DefaultValue), true); Settings.Add(setting); }
    public void RegisterAction(PluginAction action)
    { Validate(action.Key); if (Actions.Any(p => p.Key == action.Key)) throw new InvalidDataException("插件操作重复"); Actions.Add(action); }
    public void RegisterService(PluginService service)
    { Validate(service.Key); if (Services.Any(p => p.Key == service.Key)) throw new InvalidDataException("插件服务重复"); Services.Add(service); }
    public void RegisterNetworkSource(PluginNetworkSource source)
    { Validate(source.Key); if (Networks.Any(p => p.Key == source.Key)) throw new InvalidDataException("插件数据来源重复"); Networks.Add(source); }
    public void RegisterSettingsView(Func<FrameworkElement> factory)
    { Validate("settings"); if (SettingsView != null) throw new InvalidDataException("插件设置界面重复"); SettingsView = factory; }
    public T ReadConfiguration<T>(string key, T fallback)
    { try { return _values.TryGetValue(key, out var value) ? value.Deserialize<T>(PluginWire.Json) ?? fallback : fallback; } catch (JsonException) { return fallback; } }
    public T GetSetting<T>(string key, T fallback)
    {
        var setting = Settings.FirstOrDefault(p => p.Key == key);
        if (setting == null) return fallback;
        try
        {
            var value = _values.TryGetValue(PluginHost.SettingKey(PluginId, key), out var configured) ? configured : PluginWire.Element(setting.DefaultValue);
            return PluginHost.NormalizeValue(setting, value).Deserialize<T>(PluginWire.Json) ?? fallback;
        }
        catch (JsonException) { return fallback; }
    }
    public void WriteConfiguration(string key, object value)
    {
        Dispatcher.VerifyAccess(); if (Lifetime.IsCancellationRequested) return;
        if (key == "AiApiKey" && value is not "") throw new InvalidOperationException("密钥必须通过加密仓保存");
        var element = PluginWire.Element(value); _values[key] = element;
        publish(new("configuration", key, Value: element));
    }
    internal void Update(Dictionary<string, JsonElement> values)
    {
        _values = values;
        bool Flag(string key) => values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.True;
        alpha_island.Animation.MotionPolicy.ProbeRenderTier();
        alpha_island.Animation.MotionPolicy.Apply(Flag("ReduceMotionEnabled"), Flag("PowerSaveEnabled"));
    }
    public void OpenPage(string key) => publish(new("open", key));
    public void OpenSettings() => publish(new("settings"));
    public void Refresh() => publish(new("refresh"));
    public void Log(string message) => publish(new("log", Text: message.Length > 2048 ? message[..2048] : message));
    public void ReportActivity(string state, string detail)
    {
        var next = (State: Clip(state, 256), Detail: Clip(detail, 4096)); if (_lastActivity == next) return;
        _lastActivity = next; publish(new("activity", next.State, next.Detail));
    }
    private static string Clip(string value, int limit)
    {
        if (value.Length <= limit) return value;
        return value[..(char.IsHighSurrogate(value[limit - 1]) ? limit - 1 : limit)];
    }
    public void ReportBluetoothBattery(PluginBluetoothBattery battery) => publish(new("battery", Battery: battery));
    public void NotifyPage(string key, string title, string detail, double seconds = 6)
    {
        if (!Pages.Any(page => page.Key == key) || !double.IsFinite(seconds) || seconds is < 1 or > 30) return;
        publish(new("notice", key, Clip(title, 4096), Clip(detail, 8192), seconds));
    }
}
