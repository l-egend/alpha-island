namespace alpha_island.Services;

/// <summary>Coalesce continuous input per process without losing writes for other apps.</summary>
internal sealed class AppAudioCommandQueue
{
    internal sealed record Command(int? Percent, bool? Muted);
    private readonly object _gate = new();
    private readonly Dictionary<int, Command> _pending = new();
    public bool HasPending { get { lock (_gate) return _pending.Count > 0; } }

    public void SetVolume(int pid, int percent)
    {
        lock (_gate)
        {
            _pending.TryGetValue(pid, out var previous);
            _pending[pid] = new(Math.Clamp(percent, 0, 100), percent > 0 ? false : previous?.Muted);
        }
    }

    public void SetMute(int pid, bool muted)
    {
        lock (_gate)
        {
            _pending.TryGetValue(pid, out var previous);
            _pending[pid] = new(previous?.Percent, muted);
        }
    }

    public Dictionary<int, Command> Drain()
    {
        lock (_gate)
        {
            var result = new Dictionary<int, Command>(_pending);
            _pending.Clear();
            return result;
        }
    }
}
