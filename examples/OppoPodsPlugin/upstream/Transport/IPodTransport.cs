using System;

namespace OppoPodsManager;

/// <summary>
/// 传输层抽象：屏蔽物理链路(经典 SPP / BLE GATT)差异，只暴露"发命令 / 收帧"。
/// 命令层与解析层依赖本接口，不关心底层是哪条链路。
/// </summary>
public interface IPodTransport : IDisposable
{
    /// <summary>连接成功后解析出的设备蓝牙名称（用于能力检测）。</summary>
    string? DeviceName { get; }

    bool IsConnected { get; }
    string? LastError { get; }

    /// <summary>收到一整帧时触发（在传输层的读线程/读循环上下文）。</summary>
    event Action<PodFrame>? FrameReceived;

    /// <summary>链路断开时触发（对端关闭或读写错误）。</summary>
    event Action? Disconnected;

    /// <summary>同步建立连接，返回是否成功。失败原因见 LastError。</summary>
    bool Connect();

    /// <summary>发送一条命令。</summary>
    void Send(ushort cmd, byte[] payload);

    /// <summary>在 timeoutMs 预算内读取并解码可用数据，对每帧触发 FrameReceived。</summary>
    void Poll(int timeoutMs);

    /// <summary>关闭链路（幂等）。</summary>
    void Close();
}
