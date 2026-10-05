using System;
using System.Collections.Generic;

namespace OppoPodsManager;

/// <summary>
/// melody BLE GATT 帧格式（无 SPP 的 0xAA 外壳）：
///   cmd(2, 小端) | transId(1) | payLen(2, 小端) | payload
/// 对齐 APK 反编译的 Packet(Ls7/a) 结构：头部 5 字节，LENGTH 为 2 字节小端。
/// 响应帧 cmd 第 15 位(0x8000)置位；本编解码器原样保留 cmd（含响应位），
/// 与 SppFrameCodec 一致，由上层 DispatchFrame 用响应命令字匹配。
/// 纯字节逻辑，无平台依赖，AOT 友好。
/// </summary>
public sealed class GattFrameCodec : IFrameCodec
{
    private const int HeaderLen = 5;       // cmd(2)+transId(1)+payLen(2)
    private const int MaxFrame = 4096;     // 单帧上限（BLE 载荷远小于此，纯防御）
    private byte _txnCounter;              // 每次发送递增，0-255 循环（PacketFactory 每 MAC 计数器）

    public byte[] Encode(ushort cmd, byte[] payload)
    {
        payload ??= Array.Empty<byte>();
        if (payload.Length > MaxFrame) throw new ArgumentOutOfRangeException(nameof(payload), "GATT payload exceeds the decoder limit");
        var pkt = new byte[HeaderLen + payload.Length];
        pkt[0] = (byte)(cmd & 0xFF);
        pkt[1] = (byte)((cmd / 256) & 0xFF);
        pkt[2] = _txnCounter;
        pkt[3] = (byte)(payload.Length & 0xFF);
        pkt[4] = (byte)((payload.Length / 256) & 0xFF);
        Buffer.BlockCopy(payload, 0, pkt, HeaderLen, payload.Length);

        // 递增事务号（发送后自增，下一帧使用新 seq）
        _txnCounter = (byte)((_txnCounter + 1) & 0xFF);
        return pkt;
    }

    public bool TryDecode(List<byte> buffer, out PodFrame frame)
    {
        frame = default;
        if (buffer.Count < HeaderLen) return false;

        ushort cmd = (ushort)(buffer[0] + buffer[1] * 256);
        byte seq = buffer[2];                 // TRANS_ID（请求/响应配对）
        int payLen = buffer[3] + buffer[4] * 256;

        // 防御：非法长度直接丢首字节，避免缓冲卡死
        if (payLen < 0 || payLen > MaxFrame)
        {
            Log.D("GATTCODEC", $"丢弃非法帧: payLen={payLen} cmd=0x{cmd:X4}");
            buffer.RemoveAt(0);
            return false;
        }

        int frameLen = HeaderLen + payLen;
        if (buffer.Count < frameLen) return false;   // 数据不足，等更多字节

        var payload = new byte[payLen];
        buffer.CopyTo(HeaderLen, payload, 0, payLen);
        buffer.RemoveRange(0, frameLen);

        frame = new PodFrame(cmd, payload, seq);
        return true;
    }
}
