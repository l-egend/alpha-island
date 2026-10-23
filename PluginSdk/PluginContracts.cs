using System.Windows;
using System.Windows.Threading;

namespace AlphaIsland.PluginSdk;

public static class PluginApi { public const int Version = 5; public const int MinimumVersion = 1; }

/// <summary>Initialize 只注册贡献。宿主在 UI 线程调用所有生命周期方法。</summary>
public interface IIslandPlugin : IDisposable
{
    void Initialize(IPluginContext context);
    void OnSettingsChanged() { }
}

public interface IPluginContext
{
    void RegisterTool(IToolHandler tool) => throw new NotSupportedException("宿主不支持工具注册");
    IToolDispatcher Tools => UnavailableTools.Instance;
    ValueTask<DesktopContextSnapshot> GetDesktopContextAsync(string prompt, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(DesktopContextSnapshot.Empty);
    string PluginId { get; }
    string PluginDirectory { get; }
    Dispatcher Dispatcher { get; }
    CancellationToken Lifetime { get; }
    void RegisterPage(PluginPage page);
    void RegisterSetting(PluginSetting setting);
    void RegisterService(PluginService service);
    IPluginScheduledTask Schedule(string key, TimeSpan interval, Action callback, bool initiallyPaused = false)
        => new LegacyPluginScheduledTask(this, interval, callback, initiallyPaused);
    void RegisterNetworkSource(PluginNetworkSource source) => throw new NotSupportedException("宿主不支持网络数据源插件");
    T GetSetting<T>(string key, T fallback);
    void OpenPage(string key);
    /// <summary>为本插件已注册的页面提交临时小岛提醒。旧宿主可回退到日志。</summary>
    void NotifyPage(string key, string title, string detail, double seconds = 6)
        => Log("宿主不支持页面提醒：" + title);
    void Log(string message);
    void RegisterAction(PluginAction action) => throw new NotSupportedException("宿主不支持插件操作");
    void RegisterSettingsView(Func<FrameworkElement> createView) => throw new NotSupportedException("宿主不支持插件设置界面");
    string DataDirectory => throw new NotSupportedException();
    string ConfigDirectory => throw new NotSupportedException();
    T ReadConfiguration<T>(string key, T fallback) => fallback;
    void WriteConfiguration(string key, object value) => throw new NotSupportedException();
    void Refresh() { }
    void OpenSettings() { }
    void ReportActivity(string state, string detail) { }
    void ReportActivity(string state, string detail, string model) => ReportActivity(state, detail);
    IModelAvatar? CreateModelAvatar() => null;
    /// <summary>Report fresh private-protocol battery data; the host owns reminder policy.</summary>
    void ReportBluetoothBattery(PluginBluetoothBattery battery) { }
    void ReportDeviceBatteries(IReadOnlyList<PluginDeviceBattery> devices) { }
}

public sealed record PluginPage(string Key, string Title, Func<FrameworkElement> CreateView,
    string Glyph = "\uE8A5", double Height = 214, int Order = 100,
    Action? Activated = null, Action? Deactivated = null)
{
    /// <summary>可选的实时小岛摘要；保留原构造函数，兼容已有 API 1 插件。</summary>
    public Func<string>? Summary { get; init; }
    public Func<bool>? Available { get; init; }
}

public sealed record PluginAction(string Key, string Title, Action Invoke, Func<bool>? Available = null);

public sealed record PluginService(string Key, Action<CancellationToken> Start, Action Stop);
public sealed record PluginNetworkSource(string Key, string Title, Func<PluginNetworkSample?> Read, int Priority = 100);
public sealed record PluginNetworkSample(DateTimeOffset Timestamp, double DownBytesPerSecond, double UpBytesPerSecond,
    long? DownloadCounterBytes = null, long? UploadCounterBytes = null, string CounterEpoch = "",
    long? TodayDownBytes = null, long? TodayUpBytes = null, string Adapter = "");

public enum PluginSettingKind { Boolean, Number, Text, Choice }

public sealed record PluginSetting(string Key, string Label, PluginSettingKind Kind, object DefaultValue,
    string Description = "", double Minimum = 0, double Maximum = 100, double Step = 1,
    IReadOnlyList<string>? Choices = null);

public sealed record PluginBluetoothBattery(string Id, string Name, int? Percent, bool Connected, bool Charging = false);
