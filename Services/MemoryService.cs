using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using alpha_island.Core;
using alpha_island.Interop;

namespace alpha_island.Services
{
    /// <summary>物理内存快照（A 组② 系统状态）。</summary>
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

        /// <summary>本进程工作集（字节）。</summary>
        public long OwnWorkingSetBytes { get; init; }

        /// <summary>已占用的物理内存（字节）。</summary>
        public long UsedBytes => (long)(TotalBytes - AvailableBytes);

        /// <summary>占用率是否超过给定百分比。</summary>
        public bool Exceeds(double percent) => Valid && UsedPercent > percent;
    }

    /// <summary>
    /// 内存占用采样 + 「一键内存整理」（A 组②）。
    /// <para>
    /// 采样走 kernel32 的 <c>GlobalMemoryStatusEx</c>（一次调用拿总量/可用/占用率），
    /// 整理走 psapi 的 <c>EmptyWorkingSet</c> —— <b>只对自己免提权</b>
    /// （别人的进程要 <c>PROCESS_SET_QUOTA</c>，全系统整理属于 "MemoryTrimSystem"，
    /// 需要管理员权限，本批按计划不提供）。所以设置页里只写「仅优化本程序」。
    /// </para>
    /// <para>
    /// 和 <see cref="NetworkSpeedService"/> 一样：本类<b>不自己开定时器</b>，
    /// 只提供 <see cref="Sample"/> / <see cref="TrimOwnWorkingSet"/>，
    /// 节拍由 <c>IslandTicker</c> 的 "memory" 订阅提供。
    /// </para>
    /// </summary>
    public sealed class MemoryService : IDisposable
    {
        private readonly Process _self = Process.GetCurrentProcess();
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

            long ownWorkingSet = 0;
            try
            {
                _self.Refresh();
                ownWorkingSet = _self.WorkingSet64;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                // 进程已退出之类的边缘情况：占用率照样可用，工作集当 0
                ownWorkingSet = 0;
            }

            Current = new MemorySample
            {
                Valid = true,
                TotalBytes = status.ullTotalPhys,
                AvailableBytes = status.ullAvailPhys,
                // dwMemoryLoad 是整数百分比（0..100），比自己算 (Total-Avail)/Total 更贴近任务管理器
                UsedPercent = status.dwMemoryLoad,
                OwnWorkingSetBytes = ownWorkingSet
            };
            return true;
        }

        /// <summary>
        /// 把本进程的工作集挤出去（EmptyWorkingSet），返回释放的字节数（失败/没变化时为 0）。
        /// 只影响 Alpha Island 自己，不动别的进程，不需要提权。
        /// </summary>
        public long TrimOwnWorkingSet()
        {
            if (_disposed) return 0;

            long before;
            try
            {
                _self.Refresh();
                before = _self.WorkingSet64;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                return 0;
            }

            try
            {
                if (!NativeMethods.EmptyWorkingSet(NativeMethods.GetCurrentProcess()))
                {
                    IslandLog.Warn("Memory",
                        "EmptyWorkingSet 失败 win32=" + Marshal.GetLastWin32Error());
                    return 0;
                }
            }
            catch (Exception ex)
            {
                IslandLog.Warn("Memory", "EmptyWorkingSet 调用异常: " + ex.Message);
                return 0;
            }

            // 工作集是懒回收：给它几十毫秒让系统把页表改完，再读一次真实值。
            // （实测不睡也能读到下降，但偶尔会读到整理前的旧值，白报一次 "释放 0 B"。）
            long after = before;
            for (int i = 0; i < 5; i++)
            {
                Thread.Sleep(40);
                try
                {
                    _self.Refresh();
                    after = _self.WorkingSet64;
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                {
                    break;
                }

                if (after < before) break;
            }

            long released = before - after;
            if (released <= 0) return 0;

            IslandLog.Debug("Memory",
                $"一键整理：本进程工作集 {FormatSize(before)} → {FormatSize(after)}，释放 {FormatSize(released)}");
            return released;
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
            _self.Dispose();
        }
    }
}
