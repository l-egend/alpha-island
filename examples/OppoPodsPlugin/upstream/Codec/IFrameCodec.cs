using System.Collections.Generic;

namespace OppoPodsManager;

/// <summary>
/// 帧编解码器：负责命令层 (cmd+payload) 与具体链路帧格式之间的转换。
/// SPP 用 0xAA 头，GATT 用 5 字节头，各自实现本接口。
/// </summary>
public interface IFrameCodec
{
    byte[] Encode(ushort cmd, byte[] payload);
    bool TryDecode(List<byte> buffer, out PodFrame frame);
}
