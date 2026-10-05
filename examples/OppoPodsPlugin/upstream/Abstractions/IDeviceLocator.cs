namespace OppoPodsManager;

/// <summary>
/// 设备发现（平台服务，与 socket I/O 分开）。
/// Windows 走注册表；Linux 将走 BlueZ；macOS 走 IOBluetooth。各平台各自实现。
/// </summary>
public interface IDeviceLocator
{
    /// <summary>查找一台已配对的目标耳机。找到返回蓝牙地址(48位)与名称，找不到 addr=0。</summary>
    (ulong addr, string? name) Locate();
}
