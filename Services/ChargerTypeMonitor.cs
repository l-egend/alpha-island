using alpha_island.Interop;

namespace alpha_island.Services;

/// <summary>One slow firmware query at most. Old results cannot cross an AC/DC transition.</summary>
internal sealed class ChargerTypeMonitor
{
    private readonly object _gate = new();
    private readonly Func<BatteryChargeType> _read;
    private readonly Func<DateTime> _utcNow;
    private Task<BatteryChargeType>? _query;
    private BatteryChargeType _current;
    private bool? _onAc;
    private DateTime _sampledUtc = DateTime.MinValue, _nextReadUtc = DateTime.MinValue;
    private int _generation, _queryGeneration;
    internal ChargerTypeMonitor(Func<BatteryChargeType>? read = null, Func<DateTime>? utcNow = null)
    { _read = read ?? AsusChargerTypeReader.Read; _utcNow = utcNow ?? (() => DateTime.UtcNow); }
    internal BatteryChargeType ReadCached(bool? onAc)
    {
        lock (_gate)
        {
            var now = _utcNow();
            if (_onAc != onAc) { _onAc = onAc; Invalidate(); }
            if (_query is { IsCompleted: true })
            {
                if (_queryGeneration == _generation)
                {
                    _current = _query.Status == TaskStatus.RanToCompletion ? _query.Result : BatteryChargeType.Unknown;
                    _sampledUtc = now;
                }
                _query = null;
            }
            if (onAc == true && _query == null && now >= _nextReadUtc)
            {
                _queryGeneration = _generation; _nextReadUtc = now.AddSeconds(2);
                _query = Task.Run(() => { try { return _read(); } catch { return BatteryChargeType.Unknown; } });
            }
            return onAc == true && now - _sampledUtc <= TimeSpan.FromSeconds(10) ? _current : BatteryChargeType.Unknown;
        }
    }
    internal void Invalidate()
    {
        lock (_gate) { _generation++; _current = BatteryChargeType.Unknown; _sampledUtc = _nextReadUtc = DateTime.MinValue; }
    }
}
