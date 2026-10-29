using L = AlphaIsland.PluginSdk.Localization.UiText;
using System.Buffers.Binary;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Principal;
using AlphaIsland.PluginSdk;

namespace AlphaIsland.Plugins.TrafficMonitor;

/// <summary>只读、同用户的 80 字节快照；奇偶序号防止两次写入之间读出撕裂数据。</summary>
public sealed class BridgeReader : IDisposable
{
    public const int SnapshotSize = 80;
    public const uint Magic = 0x4149544d;
    private readonly string _mapName;
    private readonly byte[] _buffer = new byte[SnapshotSize];
    private MemoryMappedFile? _mapping;
    private MemoryMappedViewAccessor? _view;
    private bool _disposed;
    private PluginNetworkSample? _last;
    public string Status { get; private set; } = L.T("等待 TrafficMonitor 桥接");
    public BridgeReader(string? mapName = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        _mapName = mapName ?? "Local\\AlphaIsland.TrafficMonitor.v1_" + (identity.User?.Value ?? throw new InvalidOperationException(L.T("无法获取用户 SID")));
    }
    public PluginNetworkSample? Read(DateTimeOffset? now = null)
    {
        if (_disposed) return null;
        try
        {
            if (_view == null)
            {
                _mapping = MemoryMappedFile.OpenExisting(_mapName, MemoryMappedFileRights.Read);
                _view = _mapping.CreateViewAccessor(0, SnapshotSize, MemoryMappedFileAccess.Read);
            }
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int sequence = _view.ReadInt32(8);
                if ((sequence & 1) != 0) { Status = L.T("TrafficMonitor 正在更新"); continue; }
                Thread.MemoryBarrier();
                if (_view.ReadArray(0, _buffer, 0, SnapshotSize) != SnapshotSize) break;
                Thread.MemoryBarrier();
                if (_view.ReadInt32(8) != sequence || BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(8)) != sequence) continue;
                var sample = Decode(_buffer, now ?? DateTimeOffset.UtcNow);
                if (sample == null) { Status = L.T("桥接数据无效或超过 5 秒未更新"); Close(); return null; }
                Status = L.T("已连接 TrafficMonitor"); _last = sample; return sample;
            }
            var age = (now ?? DateTimeOffset.UtcNow) - (_last?.Timestamp ?? DateTimeOffset.MinValue);
            return age >= TimeSpan.Zero && age <= TimeSpan.FromSeconds(5) ? _last : null;
        }
        catch (FileNotFoundException) { Status = L.T("等待 TrafficMonitor 桥接（请检查 DLL 是否已加载）"); Close(); return null; }
        catch (UnauthorizedAccessException) { Status = L.T("桥接无法读取，请确认两程序使用同一 Windows 用户"); Close(); return null; }
        catch (IOException) { Status = L.T("桥接暂不可用，正在重试"); Close(); return null; }
        catch (ArgumentException) { Status = L.T("桥接格式不兼容"); Close(); return null; }
    }
    public static PluginNetworkSample? Decode(ReadOnlySpan<byte> bytes, DateTimeOffset now)
    {
        if (bytes.Length != SnapshotSize || U32(bytes, 0) != Magic || U32(bytes, 4) != 1 || U32(bytes, 12) != SnapshotSize
            || (U32(bytes, 8) & 1) != 0 || U32(bytes, 24) == 0 || (U32(bytes, 28) & ~1u) != 0) return null;
        DateTimeOffset timestamp;
        try { timestamp = DateTime.FromFileTimeUtc(BinaryPrimitives.ReadInt64LittleEndian(bytes[16..])); }
        catch (ArgumentOutOfRangeException) { return null; }
        var age = now - timestamp;
        if (age > TimeSpan.FromSeconds(5) || age < TimeSpan.FromSeconds(-1)) return null;
        ulong down = U64(bytes, 32), up = U64(bytes, 40), todayDown = U64(bytes, 48), todayUp = U64(bytes, 56);
        if (down > long.MaxValue || up > long.MaxValue || todayDown > long.MaxValue || todayUp > long.MaxValue) return null;
        uint date = U32(bytes, 72);
        bool hasToday = (U32(bytes, 28) & 1) != 0 && date == uint.Parse(now.ToLocalTime().ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
        string epoch = $"{U32(bytes, 24)}:{U64(bytes, 64)}:{date}";
        return new(timestamp, down, up, hasToday ? (long)todayDown : null, hasToday ? (long)todayUp : null, epoch,
            hasToday ? (long)todayDown : null, hasToday ? (long)todayUp : null, L.T("TrafficMonitor 所选网卡"));
    }
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
    private void Close() { _view?.Dispose(); _view = null; _mapping?.Dispose(); _mapping = null; }
    public void Dispose() { if (_disposed) return; _disposed = true; Close(); }
}
