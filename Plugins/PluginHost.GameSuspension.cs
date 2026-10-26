using alpha_island.Core;
using alpha_island.Plugins.Isolation;

namespace alpha_island.Plugins;

internal sealed partial class PluginHost
{
    private bool _gameSuspended, _checkingGameSuspension;
    private long _gameSuspensionRevision;
    private IslandTicker.Subscription? _gameBudgetPoll;

    internal void SetGameSuspended(bool suspended)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _gameSuspended == suspended) return;
        _gameSuspended = suspended; ++_gameSuspensionRevision;
        _gameBudgetPoll?.Dispose(); _gameBudgetPoll = null;
        if (suspended)
        {
            _gameBudgetPoll = IslandTicker.Shared(_dispatcher).Subscribe("game-plugin-budget", TimeSpan.FromSeconds(5),
                () => _ = ReleaseIdleGameWorkersAsync(), runImmediately: true);
        }
        else
        {
            foreach (var entry in _entries.Where(entry => entry.GameSuspended))
                entry.Attempted = false;
            Sync(_options);
        }
    }

    internal bool IsPageGameSuspended(string pageId) => _entries.Any(entry => entry.GameSuspended
        && entry.Manifest != null && pageId.StartsWith("plugin:" + entry.Manifest.Id + "/", StringComparison.Ordinal));

    internal async Task ReleaseIdleGameWorkersAsync()
    {
        if (_disposed || !_gameSuspended || _checkingGameSuspension) return;
        _checkingGameSuspension = true;
        long revision = _gameSuspensionRevision;
        try
        {
            foreach (var entry in _entries.ToArray())
            {
                if (_disposed || !_gameSuspended || revision != _gameSuspensionRevision) return;
                if (!entry.Running || entry.Instance is not IsolatedPlugin { SupportsRuntimeSuspension: true } proxy) continue;
                try
                {
                    bool prepared = await proxy.PrepareGameSuspensionAsync();
                    if (!ReferenceEquals(entry.Instance, proxy)) continue;
                    if (_disposed || !_gameSuspended || revision != _gameSuspensionRevision)
                    {
                        if (prepared && proxy.IsAlive) await proxy.ResumeAfterGamePreparationAsync();
                        continue;
                    }
                    if (!prepared) continue;
                    entry.GameSuspended = true;
                    Stop(entry);
                    Changed?.Invoke();
                }
                catch (Exception error)
                {
                    if (!_disposed && ReferenceEquals(entry.Instance, proxy))
                        IslandLog.Debug("Plugins:GameBudget", "保留尚未完成休眠准备的插件：" + error.GetType().Name);
                }
            }
        }
        finally { _checkingGameSuspension = false; }
    }
}
