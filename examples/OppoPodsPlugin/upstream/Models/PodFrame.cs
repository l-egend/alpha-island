using System;

namespace OppoPodsManager;

/// <summary>一条已解码的协议帧：命令字 + 载荷 + 事务号（不含任何链路层封装）。</summary>
public readonly struct PodFrame
{
    public readonly ushort Cmd;
    public readonly byte[] Payload;
    public readonly byte Seq;
    public PodFrame(ushort cmd, byte[] payload, byte seq = 0)
    {
        Cmd = cmd;
        Payload = payload;
        Seq = seq;
    }
}
