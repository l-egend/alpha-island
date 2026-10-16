using System.Diagnostics;

namespace alpha_island.Services;

/// <summary>A bounded signal and one delayed follow-up replace per-broadcast background tasks.</summary>
internal sealed class BatterySampleCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _ready = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Action _sample;
    private Task? _worker;
    private System.Threading.Timer? _followup;
    private bool _disposed;
    internal BatterySampleCoordinator(Action sample) => _sample = sample;
    internal void Request(int delayMs = 0)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _worker ??= Task.Run(Run);
            if (delayMs > 0)
            {
                _followup ??= new System.Threading.Timer(_ => Request());
                _followup.Change(delayMs, Timeout.Infinite);
            }
            else if (_ready.CurrentCount == 0) _ready.Release();
        }
    }
    private async Task Run()
    {
        long last = 0;
        try
        {
            while (true)
            {
                await _ready.WaitAsync(_stop.Token).ConfigureAwait(false);
                double remaining = last == 0 ? 0 : 50 - Stopwatch.GetElapsedTime(last).TotalMilliseconds;
                if (remaining > 0) await Task.Delay((int)Math.Ceiling(remaining), _stop.Token).ConfigureAwait(false);
                while (_ready.Wait(0)) { }
                _stop.Token.ThrowIfCancellationRequested();
                try { _sample(); } catch (Exception ex) { Debug.WriteLine("[BatterySampler] " + ex.Message); }
                last = Stopwatch.GetTimestamp();
            }
        }
        catch (OperationCanceledException) { }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _followup?.Dispose(); _stop.Cancel();
            if (_worker == null) { _ready.Dispose(); _stop.Dispose(); }
            else _ = _worker.ContinueWith(_ => { _ready.Dispose(); _stop.Dispose(); }, TaskScheduler.Default);
        }
    }
}
