using AlphaIsland.PluginSdk;
using alpha_island.Core;

namespace alpha_island.Services;

internal sealed class DailyTrafficService : IDisposable
{
    private readonly LocalStateFile<DailyTrafficState>? _file;
    private readonly DailyTrafficLedger _ledger;
    private DateTimeOffset _savedAt;
    private DailyTrafficDay? _saved;
    internal DailyTrafficDay Current => _ledger.Current;
    internal string Source => _ledger.Source;
    internal DailyTrafficService(string? path = null)
    {
        if (path != null) _file = new(path, maximumBytes: 8192);
        _ledger = new(_file?.Read());
    }
    internal void Observe(DateTimeOffset now, long localDown, long localUp, string externalId = "", PluginNetworkSample? external = null)
    {
        _ledger.Observe(now, localDown, localUp, externalId, external);
        if (_file != null && Current != _saved && now - _savedAt >= TimeSpan.FromSeconds(30))
        { _file.Write(_ledger.Export()); _saved = Current; _savedAt = now; }
    }
    public void Dispose()
    {
        if (_file == null) return;
        if (Current != _saved) _file.Write(_ledger.Export());
        _file.Dispose();
    }
}
