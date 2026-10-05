using System.Windows;
using System.Windows.Threading;

namespace AlphaIsland.PluginSdk;

public static class PluginApi { public const int Version = 1; }

/// <summary>Initialize 只注册贡献。宿主在 UI 线程调用所有生命周期方法。</summary>
public interface IIslandPlugin : IDisposable
{
    void Initialize(IPluginContext context);
    void OnSettingsChanged() { }
}

public interface IPluginContext
{
    string PluginId { get; }
    string PluginDirectory { get; }
    Dispatcher Dispatcher { get; }
    CancellationToken Lifetime { get; }
    void RegisterPage(PluginPage page);
    void RegisterSetting(PluginSetting setting);
    void RegisterService(PluginService service);
    void RegisterNetworkSource(PluginNetworkSource source) => throw new NotSupportedException("宿主不支持网络数据源插件");
    T GetSetting<T>(string key, T fallback);
    void OpenPage(string key);
    /// <summary>为本插件已注册的页面提交临时小岛提醒。旧宿主可回退到日志。</summary>
    void NotifyPage(string key, string title, string detail, double seconds = 6)
        => Log("宿主不支持页面提醒：" + title);
    void Log(string message);
}

public sealed record PluginPage(string Key, string Title, Func<FrameworkElement> CreateView,
    string Glyph = "\uE8A5", double Height = 214, int Order = 100,
    Action? Activated = null, Action? Deactivated = null)
{
    /// <summary>可选的实时小岛摘要；保留原构造函数，兼容已有 API 1 插件。</summary>
    public Func<string>? Summary { get; init; }
}

public sealed record PluginService(string Key, Action<CancellationToken> Start, Action Stop);
public sealed record PluginNetworkSource(string Key, string Title, Func<PluginNetworkSample?> Read, int Priority = 100);
public sealed record PluginNetworkSample(DateTimeOffset Timestamp, double DownBytesPerSecond, double UpBytesPerSecond,
    long? DownloadCounterBytes = null, long? UploadCounterBytes = null, string CounterEpoch = "",
    long? TodayDownBytes = null, long? TodayUpBytes = null, string Adapter = "");

public enum PluginSettingKind { Boolean, Number, Text, Choice }

public sealed record PluginSetting(string Key, string Label, PluginSettingKind Kind, object DefaultValue,
    string Description = "", double Minimum = 0, double Maximum = 100, double Step = 1,
    IReadOnlyList<string>? Choices = null);
