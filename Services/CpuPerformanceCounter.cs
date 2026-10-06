using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace alpha_island.Services;

internal readonly record struct CpuCounterReading(bool Available, double? Percent);
internal interface ICpuPerformanceCounter : IDisposable
{
    CpuCounterReading Read();
}

internal sealed class CpuPerformanceCounter : ICpuPerformanceCounter
{
    private readonly string _path;
    private QueryHandle? _query;
    private IntPtr _counter;
    private bool _primed, _disposed;
    internal CpuPerformanceCounter(bool utility) : this(utility
        ? @"\Processor Information(_Total)\% Processor Utility"
        : @"\Processor Information(_Total)\% Processor Time") { }
    internal CpuPerformanceCounter(string path) => _path = path;

    public CpuCounterReading Read()
    {
        if (_disposed) return new(false, null);
        if (_query == null)
        {
            if (PdhOpenQuery(IntPtr.Zero, UIntPtr.Zero, out var query) != 0)
            { query?.Dispose(); return new(false, null); }
            _query = query;
            // English paths work on Chinese Windows as well as other display languages.
            if (PdhAddEnglishCounter(query, _path, UIntPtr.Zero, out _counter) != 0)
            { DisposeQuery(); return new(false, null); }
        }
        if (PdhCollectQueryData(_query) != 0)
        { DisposeQuery(); return new(false, null); }
        if (!_primed) { _primed = true; return new(true, null); }
        uint result = PdhGetFormattedCounterValue(_counter, 0x200 | 0x8000, IntPtr.Zero, out var value);
        if (result != 0 || value.Status is not (0 or 1))
        { DisposeQuery(); return new(false, null); }
        return new(true, CpuUsageService.NormalizePercent(value.Number));
    }

    private void DisposeQuery()
    {
        _query?.Dispose(); _query = null; _counter = IntPtr.Zero; _primed = false;
    }
    public void Dispose() { _disposed = true; DisposeQuery(); }

    // PDH's DWORD status precedes an eight-byte-aligned value union.
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct FormattedValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Number;
    }
    private sealed class QueryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public QueryHandle() : base(true) { }
        protected override bool ReleaseHandle() => PdhCloseQuery(handle) == 0;
    }
    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", ExactSpelling = true)]
    private static extern uint PdhOpenQuery(IntPtr source, UIntPtr user, out QueryHandle query);
    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounter(QueryHandle query, string path, UIntPtr user, out IntPtr counter);
    [DllImport("pdh.dll", ExactSpelling = true)] private static extern uint PdhCollectQueryData(QueryHandle query);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out FormattedValue value);
    [DllImport("pdh.dll", ExactSpelling = true)] private static extern uint PdhCloseQuery(IntPtr query);
}
