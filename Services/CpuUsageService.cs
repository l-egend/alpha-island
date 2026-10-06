using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using alpha_island.Core;

namespace alpha_island.Services;

internal enum CpuMeasurement { ProcessorUtility, ProcessorTime, SystemTimes }

public sealed class CpuUsageService : IDisposable
{
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    private long _idle, _kernel, _user;
    private bool _primed, _disposed;
    private readonly object _gate = new();
    private readonly bool _useUtility;
    private readonly Func<ICpuPerformanceCounter> _counterFactory;
    private readonly Func<(long Idle, long Kernel, long User)?> _readTimes;
    private ICpuPerformanceCounter? _counter;
    private long _retryAfter;
    private sealed record Reading(double? Percent, CpuMeasurement Measurement);
    private Reading _reading = new(null, CpuMeasurement.SystemTimes);
    public double? CurrentPercent => Volatile.Read(ref _reading).Percent;
    internal CpuMeasurement Measurement => Volatile.Read(ref _reading).Measurement;

    public CpuUsageService()
    {
        _useUtility = UsesUtilityMetric(Environment.OSVersion.Version.Build, WindowsRevision());
        _counterFactory = () => new CpuPerformanceCounter(_useUtility);
        _readTimes = ReadSystemTimes;
    }
    internal CpuUsageService(bool useUtility, Func<ICpuPerformanceCounter> counterFactory,
        Func<(long Idle, long Kernel, long User)?> readTimes)
    { _useUtility = useUtility; _counterFactory = counterFactory; _readTimes = readTimes; }

    // Windows 11 24H2's March 2025 update changed Task Manager to time-based
    // workload. Older client builds (including 23H2) use frequency-aware utility.
    internal static bool UsesUtilityMetric(int build, int revision) => build < 26100 || (build == 26100 && revision < 3624);
    private static int WindowsRevision()
    {
        try { return Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", 0) as int? ?? 0; }
        catch { return 0; }
    }
    private static (long Idle, long Kernel, long User)? ReadSystemTimes() =>
        GetSystemTimes(out var idle, out var kernel, out var user) ? (idle, kernel, user) : null;

    public void Sample()
    {
        lock (_gate)
        {
            if (_disposed) return;
            double? fallback = SampleTimes(); // Keep a baseline ready if PDH becomes unavailable.
            if (_counter == null && Environment.TickCount64 < _retryAfter)
            { Publish(fallback, CpuMeasurement.SystemTimes); return; }
            try
            {
                _counter ??= _counterFactory();
                var reading = _counter.Read();
                if (reading.Available)
                {
                    Publish(reading.Percent is double value ? NormalizePercent(value) : null,
                        _useUtility ? CpuMeasurement.ProcessorUtility : CpuMeasurement.ProcessorTime);
                    return;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            { IslandLog.Debug("Cpu", "性能计数器不可用: " + ex.GetType().Name); }
            _counter?.Dispose(); _counter = null;
            _retryAfter = Environment.TickCount64 + 30_000;
            IslandLog.Debug("Cpu", "性能计数器暂不可用，回退系统计时并在 30 秒后重试");
            Publish(fallback, CpuMeasurement.SystemTimes);
        }
    }
    private double? SampleTimes()
    {
        if (_readTimes() is not { } times) { _primed = false; return null; }
        var result = _primed ? Calculate(times.Idle - _idle, times.Kernel - _kernel, times.User - _user) : null;
        (_idle, _kernel, _user) = times;
        _primed = true;
        return result;
    }
    private void Publish(double? percent, CpuMeasurement measurement)
    {
        if (_reading.Measurement != measurement)
            IslandLog.Debug("Cpu", $"采样口径={measurement}，每秒刷新");
        Volatile.Write(ref _reading, new Reading(percent, measurement));
    }
    internal static double? NormalizePercent(double value) =>
        double.IsFinite(value) && value >= 0 ? Math.Min(value, 100) : null;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _counter?.Dispose(); _counter = null;
            Volatile.Write(ref _reading, new Reading(null, CpuMeasurement.SystemTimes));
        }
    }
    internal static double? Calculate(long idle, long kernel, long user)
    {
        double total = (double)kernel + user;
        if (idle < 0 || kernel < 0 || user < 0 || total <= 0 || idle > total) return null;
        return Math.Clamp((total - idle) * 100 / total, 0, 100);
    }
}
