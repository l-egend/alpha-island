using System;
using System.Collections.Generic;

namespace OppoPodsManager;

/// <summary>
/// 经典 SPP / RFCOMM 的帧格式：0xAA + totalLen(1) + 00 00 + cmd(2 LE) + seq + payLen(2 LE) + payload。
/// 从原 OppoProtocol.BuildPacket / 帧提取逻辑抽取而来。
/// </summary>
public sealed class SppFrameCodec : IFrameCodec
{
    public const byte Header = 0xAA;
    public const byte Seq = 0xF0;
    private const int MaxFrame = 512;

    public byte[] Encode(ushort cmd, byte[] payload)
    {
        payload ??= Array.Empty<byte>();
        if (payload.Length > 248) throw new ArgumentOutOfRangeException(nameof(payload), "SPP payload exceeds the one-byte frame length");
        int totalLen = 7 + payload.Length;
        var pkt = new byte[2 + totalLen];
        pkt[0] = Header;
        pkt[1] = (byte)totalLen;
        pkt[2] = 0x00;
        pkt[3] = 0x00;
        pkt[4] = (byte)(cmd & 0xFF);
        pkt[5] = (byte)((cmd / 256) & 0xFF);
        pkt[6] = Seq;
        pkt[7] = (byte)(payload.Length & 0xFF);
        pkt[8] = (byte)((payload.Length / 256) & 0xFF);
        Buffer.BlockCopy(payload, 0, pkt, 9, payload.Length);
        return pkt;
    }

    public bool TryDecode(List<byte> buffer, out PodFrame frame)
    {
        frame = default;

        int start = buffer.IndexOf(Header);
        if (start < 0) { buffer.Clear(); return false; }
        if (start > 0) buffer.RemoveRange(0, start);
        if (buffer.Count < 2) return false;

        int totalLen = buffer[1];
        int frameLen = totalLen + 2;
        if (totalLen < 7 || frameLen > MaxFrame) { Log.D("CODEC", $"丢弃非法帧头: totalLen={totalLen}"); buffer.RemoveAt(0); return false; }
        if (buffer.Count < frameLen) return false;

        var pkt = buffer.GetRange(0, frameLen).ToArray();
        buffer.RemoveRange(0, frameLen);

        // 头部至少 9 字节才有完整 cmd + payLen 字段
        if (pkt.Length < 9) return false;

        ushort cmd = (ushort)(pkt[4] + pkt[5] * 256);
        byte seq = pkt[6];                    // TRANS_ID（请求/响应配对）
        int payLen = pkt[7] + pkt[8] * 256;
        int payloadStart = 9;

        // 防御：payLen 不能超过实际帧体
        int avail = pkt.Length - payloadStart;
        if (payLen > avail)
        {
            Log.D("CODEC", $"丢弃不完整载荷: 声明 {payLen} > 可用 {avail}, cmd=0x{cmd:X4}");
            return false;
        }

        var payload = new byte[payLen];
        Buffer.BlockCopy(pkt, payloadStart, payload, 0, payLen);
        frame = new PodFrame(cmd, payload, seq);
        return true;
    }
}
