using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Threading.Tasks;
using System.Windows.Threading;
using alpha_island.Core;
using alpha_island.Services;

namespace alpha_island;

public partial class MainWindow
{
    private sealed record NetworkCounterSnapshot(NetworkSpeedSample Current, long SessionDown, long SessionUp,
        long ObservedDown, long ObservedUp, double PeakDown, double PeakUp, string[] Adapters, string DegradedReason)
    {
        internal static NetworkCounterSnapshot Capture(NetworkSpeedService service) => new(service.Current,
            service.SessionDownloadedBytes, service.SessionUploadedBytes, service.ObservedDownloadedBytes,
            service.ObservedUploadedBytes, service.SessionPeakDownload, service.SessionPeakUpload,
            service.AdapterNames.ToArray(), service.DegradedReason);
    }

    private Task<NetworkCounterSnapshot>? _networkReadTask;
    private NetworkSpeedService? _networkReadOwner;
    private NetworkSpeedService? _networkSnapshotSource;
    private NetworkCounterSnapshot? _networkSnapshot;
    private long _networkReadStartedAt, _networkLastReadMs, _networkSlowReadCount, _networkReadFailures, _networkNextSlowLog;
    private NetworkCounterSnapshot? NativeNetworkSnapshot => ReferenceEquals(_networkSnapshotSource, _netSpeed) ? _networkSnapshot : null;
    private long NativeObservedDown => NativeNetworkSnapshot?.ObservedDown ?? _netSpeed?.ObservedDownloadedBytes ?? 0;
    private long NativeObservedUp => NativeNetworkSnapshot?.ObservedUp ?? _netSpeed?.ObservedUploadedBytes ?? 0;

    private void SampleNetworkSpeed()
    {
        var service = _netSpeed;
        if (_exiting || service == null || ReferenceEquals(_networkReadOwner, service) && _networkReadTask != null) return;
        _networkReadOwner = service;
        _networkReadStartedAt = Environment.TickCount64;
        var read = _readNetworkCounters;
        var pending = Task.Run(() => { read(service); return NetworkCounterSnapshot.Capture(service); });
        _networkReadTask = pending;
        _ = CompleteNetworkReadAsync(service, pending);
    }

    private async Task CompleteNetworkReadAsync(NetworkSpeedService service, Task<NetworkCounterSnapshot> pending)
    {
        long startedAt = _networkReadStartedAt;
        NetworkCounterSnapshot? sample = null;
        try { sample = await pending.ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { IslandLog.Warn("NetworkSpeed", "后台网卡采样未完成：" + error.GetType().Name); }
        long elapsed = Math.Max(0, Environment.TickCount64 - startedAt);
        if (Dispatcher.HasShutdownStarted) return;
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (_exiting || !ReferenceEquals(_netSpeed, service)) return;
                    _networkLastReadMs = elapsed;
                    if (elapsed >= 500)
                    {
                        _networkSlowReadCount++;
                        if (Environment.TickCount64 >= _networkNextSlowLog)
                        {
                            _networkNextSlowLog = Environment.TickCount64 + 60_000;
                            IslandLog.Warn("NetworkSpeed", $"网卡读取耗时 {elapsed}ms，已在后台完成；界面不等待驱动");
                        }
                    }
                    if (sample == null) { _networkReadFailures++; return; }
                    _networkSnapshotSource = service; _networkSnapshot = sample;
                    ApplyNetworkSpeedSample(ReadNetworkSample(service));
                }
                finally
                {
                    if (ReferenceEquals(_networkReadTask, pending)) { _networkReadTask = null; _networkReadOwner = null; }
                }
            }, DispatcherPriority.Background).Task.ConfigureAwait(false);
        }
        catch (Exception error) when (error is TaskCanceledException or InvalidOperationException)
        { if (!_exiting && !Dispatcher.HasShutdownStarted) IslandLog.Warn("NetworkSpeed", "网速显示更新未完成：" + error.GetType().Name); }
    }

    private string NetworkSamplingDiagnostics() => L.F($"后台读取；正在采样={_networkReadTask != null}；最近用时={_networkLastReadMs} ms；")
        + L.F($"慢读取={_networkSlowReadCount}；失败={_networkReadFailures}；网卡数={NativeNetworkSnapshot?.Adapters.Length ?? 0}");
}
