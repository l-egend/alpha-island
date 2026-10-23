using System;
using System.Runtime.InteropServices;
using alpha_island.Interop;

namespace alpha_island.Services
{
    /// <summary>物理内存快照。</summary>
    public readonly struct MemorySample
    {
        /// <summary>采样是否成功（拿不到 GlobalMemoryStatusEx 时为 false）。</summary>
        public bool Valid { get; init; }

        /// <summary>物理内存总量（字节）。</summary>
        public ulong TotalBytes { get; init; }

        /// <summary>当前可用物理内存（字节）。</summary>
        public ulong AvailableBytes { get; init; }

        /// <summary>物理内存占用率（0..100，来自 dwMemoryLoad）。</summary>
        public double UsedPercent { get; init; }

        /// <summary>已占用的物理内存（字节）。</summary>
        public long UsedBytes => (long)(TotalBytes - AvailableBytes);

        /// <summary>占用率是否超过给定百分比。</summary>
        public bool Exceeds(double percent) => Valid && UsedPercent > percent;
    }

    /// <summary>
    /// 物理内存占用采样。通过 GlobalMemoryStatusEx 只读取总量、可用量与占用率。
    /// <para>
    /// 和 <see cref="NetworkSpeedService"/> 一样：本类<b>不自己开定时器</b>，
    /// 只提供 <see cref="Sample"/>，
    /// 节拍由 <c>IslandTicker</c> 的 "memory" 订阅提供。
    /// </para>
    /// </summary>
    public sealed class MemoryService : IDisposable
    {
        private bool _disposed;

        /// <summary>最近一次采样结果；未采样过时 <c>Valid == false</c>。</summary>
        public MemorySample Current { get; private set; }

        /// <summary>采一拍。返回是否成功。</summary>
        public bool Sample()
        {
            if (_disposed) return false;

            var status = new NativeMethods.MEMORYSTATUSEX();
            status.dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>();
            if (!NativeMethods.GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
            {
                Current = default;
                return false;
            }

            Current = new MemorySample
            {
                Valid = true,
                TotalBytes = status.ullTotalPhys,
                AvailableBytes = status.ullAvailPhys,
                // dwMemoryLoad 是整数百分比（0..100），比自己算 (Total-Avail)/Total 更贴近任务管理器
                UsedPercent = status.dwMemoryLoad
            };
            return true;
        }

        /// <summary>字节数转人类可读大小（B / KB / MB / GB，MB/GB 保留一位小数）。</summary>
        public static string FormatSize(long bytes)
        {
            double value = bytes;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit <= 1 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }
}
