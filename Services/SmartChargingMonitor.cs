using alpha_island.Interop;

namespace alpha_island.Services;

/// <summary>The current Windows smart charging indicator; Unknown is never inferred from the battery percentage.</summary>
public enum SmartChargingState { Unknown, NotActive, Active }

/// <summary>One background shell read at most. Keep valid evidence through brief shell failures.</summary>
internal sealed class SmartChargingMonitor
{
    private readonly object _gate = new();
    private readonly Func<SmartChargingState> _read;
    private readonly Func<DateTime> _utcNow;
    private Task<SmartChargingState>? _query;
    private SmartChargingState _current;
    private DateTime _sampledUtc = DateTime.MinValue;
    private DateTime _nextReadUtc = DateTime.MinValue;
    private DateTime _pendingNotActiveUtc = DateTime.MinValue;
    private bool? _onAcPower;
    private int _generation, _queryGeneration;
    private static readonly TimeSpan ValidFor = TimeSpan.FromSeconds(30);

    internal SmartChargingMonitor(Func<SmartChargingState>? read = null, Func<DateTime>? utcNow = null)
    {
        _read = read ?? WindowsSmartChargingReader.Read;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    internal SmartChargingState ReadCached(bool? onAcPower = null)
    {
        lock (_gate)
        {
            var now = _utcNow();
            if (onAcPower.HasValue && onAcPower != _onAcPower)
            {
                _onAcPower = onAcPower;
                _generation++;
                _nextReadUtc = _pendingNotActiveUtc = DateTime.MinValue;
            }
            if (_onAcPower == false)
            {
                // Physical unplug is authoritative and never waits for shell confirmation.
                _current = SmartChargingState.NotActive; _sampledUtc = now;
                if (_query is { IsCompleted: true }) _query = null;
                return _current;
            }
            if (_query is { IsCompleted: true })
            {
                if (_queryGeneration == _generation)
                {
                    var result = _query.IsCompletedSuccessfully ? _query.Result : SmartChargingState.Unknown;
                    if (result == SmartChargingState.Unknown)
                        _pendingNotActiveUtc = DateTime.MinValue; // A read failure does not mean disabled.
                    else if (result == SmartChargingState.NotActive && _current == SmartChargingState.Active
                        && now - _sampledUtc <= ValidFor && _pendingNotActiveUtc == DateTime.MinValue)
                    {
                        // Explorer may briefly omit the smart-charging suffix while updating its battery label.
                        _pendingNotActiveUtc = now; _nextReadUtc = now.AddSeconds(1);
                    }
                    else
                    {
                        _current = result; _sampledUtc = now; _pendingNotActiveUtc = DateTime.MinValue;
                    }
                }
                _query = null;
            }
            if (_query == null && now >= _nextReadUtc)
            {
                _queryGeneration = _generation;
                _nextReadUtc = now.AddSeconds(5);
                _query = Task.Run(() => { try { return _read(); } catch { return SmartChargingState.Unknown; } });
            }
            return now - _sampledUtc <= ValidFor ? _current : SmartChargingState.Unknown;
        }
    }

    internal void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _nextReadUtc = _pendingNotActiveUtc = DateTime.MinValue;
            // Broadcasts request a fresh read; they do not revoke the last valid indicator.
        }
    }
}