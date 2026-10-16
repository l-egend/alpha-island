using System.Runtime.InteropServices;

namespace AlphaIsland.Hardware;

/// <summary>Loads NVML from NVIDIA's installed driver; never launches nvidia-smi or a helper process.</summary>
internal sealed class NvidiaGpuPowerSource : IComponentPowerSource
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Init();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Count(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Handle(uint index, out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Power(IntPtr handle, out uint milliwatts);
    private IntPtr _library;
    private Init? _shutdown;
    private Count? _count;
    private Handle? _handle;
    private Power? _power;
    private bool _initialized, _disposed;
    private DateTime _retryAfter;
    private string _unavailable = "未找到支持功耗读取的 NVIDIA 驱动接口；其他显卡可通过项目接口接入";
    private const string Source = "NVIDIA NVML";

    public ComponentPowerReading Sample()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            if (!_initialized)
            {
                if (DateTime.UtcNow < _retryAfter) return new(null, _unavailable, Source);
                Initialize();
            }
            Verify(_count!(out uint count));
            if (count is < 1 or > 32) throw new InvalidOperationException("未找到可读取功耗的 NVIDIA 独立显卡");
            double watts = 0;
            for (uint index = 0; index < count; index++)
            {
                Verify(_handle!(index, out IntPtr handle));
                Verify(_power!(handle, out uint milliwatts));
                watts += milliwatts / 1000.0;
            }
            return new(watts, "", Source);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            Close();
            _retryAfter = DateTime.UtcNow.AddSeconds(30);
            _unavailable = "独立 GPU 功耗无法读取：" + ex.Message;
            return new(null, _unavailable, Source);
        }
    }

    private void Initialize()
    {
        // Absolute driver locations only: do not resolve an arbitrary nvml.dll from PATH or the app folder.
        string[] paths = [Path.Combine(Environment.SystemDirectory, "nvml.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll")];
        foreach (string path in paths)
            if (NativeLibrary.TryLoad(path, out _library)) break;
        if (_library == IntPtr.Zero) throw new DllNotFoundException("NVIDIA NVML 不可用，当前显卡或驱动未提供该接口");
        T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
        var init = Export<Init>("nvmlInit_v2");
        _shutdown = Export<Init>("nvmlShutdown");
        _count = Export<Count>("nvmlDeviceGetCount_v2");
        _handle = Export<Handle>("nvmlDeviceGetHandleByIndex_v2");
        _power = Export<Power>("nvmlDeviceGetPowerUsage");
        Verify(init());
        _initialized = true;
    }

    private static void Verify(int code)
    {
        if (code == 0) return;
        throw new InvalidOperationException(code switch
        {
            3 => "显卡不支持功耗读数",
            4 => "显卡驱动拒绝普通权限读取",
            9 => "NVIDIA 驱动未加载",
            15 => "显卡已断开或驱动需要恢复",
            _ => $"NVIDIA 驱动返回错误 {code}"
        });
    }

    private void Close()
    {
        if (_initialized) _shutdown?.Invoke();
        _initialized = false;
        _shutdown = null; _count = null; _handle = null; _power = null;
        if (_library != IntPtr.Zero) { NativeLibrary.Free(_library); _library = IntPtr.Zero; }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Close(); }
}
