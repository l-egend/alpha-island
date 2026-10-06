using AlphaIsland.Hardware;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace alpha_island.Services;

/// <summary>One read-only worker session. Closing the pipe stops the elevated worker without privileged process control.</summary>
internal sealed class HardwarePowerWorkerClient : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _session;
    private NamedPipeServerStream? _pipe;
    private bool _disposed;
    private HardwarePowerReading? _current;
    internal HardwarePowerReading? Current
    {
        get { var value = Volatile.Read(ref _current); return value?.IsFresh(DateTime.UtcNow) == true ? value : null; }
    }
    internal async Task<string> StartAsync(bool elevate = true)
    {
        CancellationTokenSource session;
        NamedPipeServerStream pipe;
        string name;
        lock (_gate)
        {
            if (_disposed) return "硬件功耗监听已关闭";
            if (_session != null) return "独立硬件读取进程正在运行或等待授权";
            if (elevate && HardwareDriverAccess.NeedsInstallation) return HardwareDriverAccess.Description(HardwareDriverAccess.Probe());
            session = new CancellationTokenSource();
            name = "AlphaIsland.Hardware." + Guid.NewGuid().ToString("N");
            pipe = HardwarePowerPipe.CreateReader(name);
            _session = session; _pipe = pipe;
        }
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "hardware", "AlphaIsland.HardwareWorker.exe");
            if (!File.Exists(path)) throw new FileNotFoundException("独立硬件读取程序缺失，请重新安装完整程序", path);
            using var parent = Process.GetCurrentProcess();
            return await LaunchAsync(path, parent, pipe, session, name, elevate);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { StopSession(session); session.Dispose(); return "已取消管理员授权；普通权限的 GPU 读取继续运行"; }
        catch (Exception ex)
        { StopSession(session); session.Dispose(); return "独立硬件读取未启动：" + ex.Message; }
    }
    private async Task<string> LaunchAsync(string path, Process parent, NamedPipeServerStream pipe, CancellationTokenSource session, string name, bool elevate)
    {
        // Generated GUID and numeric arguments only; no shell command is constructed.
        var start = new ProcessStartInfo(path) { UseShellExecute = true, Verb = elevate ? "runas" : "open", WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(path)!, Arguments = $"{name} {parent.Id} {parent.StartTime.ToUniversalTime().Ticks}" };
        using var worker = await Task.Run(() => Process.Start(start), session.Token) ?? throw new IOException("无法启动硬件读取程序");
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(session.Token); connect.CancelAfter(TimeSpan.FromSeconds(35));
        await pipe.WaitForConnectionAsync(connect.Token);
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint client) || client != worker.Id)
            throw new IOException("硬件读取连接的进程身份不符");
        var first = await ReadWithDeadline(pipe, session.Token);
        if (!Publish(session, first)) { session.Dispose(); return "硬件功耗监听已关闭"; }
        _ = ReceiveAsync(pipe, session);
        return first.TotalWatts.HasValue ? "CPU＋GPU 硬件功耗读取已启用" : "读取进程已启动；" + first.Reason;
    }
    private static async Task<HardwarePowerReading> ReadWithDeadline(Stream pipe, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(8));
        return await HardwarePowerProtocol.ReadAsync(pipe, deadline.Token);
    }
    private bool Publish(CancellationTokenSource session, HardwarePowerReading reading)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_session, session)) return false;
            Volatile.Write(ref _current, reading); return true;
        }
    }
    private async Task ReceiveAsync(Stream pipe, CancellationTokenSource session)
    {
        try { while (!session.IsCancellationRequested) if (!Publish(session, await ReadWithDeadline(pipe, session.Token))) break; }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or System.Text.Json.JsonException)
        { if (!session.IsCancellationRequested) Core.IslandLog.Warn("HardwarePower", "读取进程已断开：" + ex.Message); }
        finally { StopSession(session); session.Dispose(); }
    }
    private void StopSession(CancellationTokenSource session)
    {
        lock (_gate) { if (!ReferenceEquals(_session, session)) return; StopLocked(); }
    }
    private void StopLocked()
    {
        _session?.Cancel(); _pipe?.Dispose(); _pipe = null; _session = null;
        // The async operation disposes its cancellation source after it has finished unwinding.
        Volatile.Write(ref _current, null);
    }
    internal void Stop() { lock (_gate) StopLocked(); }
    public void Dispose() { lock (_gate) { _disposed = true; StopLocked(); } }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}

