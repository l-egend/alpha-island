using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using AlphaIsland.PluginSdk;

namespace alpha_island.Plugins.Isolation;

internal sealed record PluginPacket(long Id, string Kind, JsonElement Data, string? Error = null);
internal sealed record PluginStart(string Directory, string Assembly, string EntryType, string PluginId,
    string DataRoot, Dictionary<string, JsonElement> Configuration);
internal sealed record RemotePage(string Key, string Title, string Glyph, double Height, int Order);
internal sealed record RemoteAction(string Key, string Title, bool Available);
internal sealed record RemoteNetwork(string Key, string Title, int Priority);
internal sealed record RemoteRegistry(RemotePage[] Pages, PluginSetting[] Settings, RemoteAction[] Actions, bool HasSettings,
    RemoteNetwork[] Networks, ToolDefinition[]? Tools = null);
internal sealed record RemoteHostCall(string Id, string Kind, JsonElement Data);
internal sealed record RemoteHostReply(string Id, JsonElement Data, string? Error = null);
internal sealed record RemotePageState(string Key, bool Available, string Summary);
internal sealed record RemoteStatus(RemotePageState[] Pages, RemoteAction[] Actions, PluginNetworkSample? Network,
    string? NetworkTitle, string? NetworkKey, int NetworkPriority, long UiPulse);
internal sealed record RemoteCommand(string View, string Action, int Element = 0, string? Text = null,
    double Number = 0, bool? Checked = null, double X = 0, double Y = 0, double Width = 414, double Height = 294);
internal sealed record RemoteViewRequest(string Key, double Width, double Height, bool Active = true);
internal sealed record RemoteTextInput(string View, string RequestId, string Text = "");
internal sealed record RemoteVisual(int Id, string Kind, string Name, double X, double Y, double Width, double Height,
    string Text, string Foreground, string Background, double FontSize, string FontFamily, bool Enabled,
    bool ReadOnly = false, bool Multiline = false, string[]? Choices = null, int Selected = -1,
    double Minimum = 0, double Maximum = 100, double Value = 0, bool? Checked = null,
    double ClipX = 0, double ClipY = 0, double ClipWidth = 0, double ClipHeight = 0,
    bool Bold = false, bool Wrap = false, string Alignment = "Left", string Artwork = "", string ToolTip = "", double Viewport = 0);
internal sealed record RemoteFrame(double Width, double Height, string Background, RemoteVisual[] Elements, long Revision);
internal sealed record RemoteEvent(string Name, string Key = "", string Text = "", string Detail = "",
    double Seconds = 6, JsonElement? Value = null, PluginBluetoothBattery? Battery = null, PluginDeviceBattery[]? DeviceBatteries = null);

internal static class PluginWire
{
    internal const int MaximumPacketBytes = 6 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, MaxDepth = 32 };
    internal static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
    internal static T Read<T>(JsonElement value) => value.Deserialize<T>(Json)
        ?? throw new InvalidDataException("插件响应缺少必要内容");
    internal static async Task Write(Stream stream, PluginPacket packet, SemaphoreSlim gate, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(packet, Json);
        if (bytes.Length > MaximumPacketBytes) throw new InvalidDataException("插件响应超过容量限制");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { await stream.WriteAsync(header, token).ConfigureAwait(false); await stream.WriteAsync(bytes, token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    internal static async Task<PluginPacket> Receive(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 2 || length > MaximumPacketBytes) throw new InvalidDataException("插件通信长度无效");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<PluginPacket>(bytes, Json) ?? throw new InvalidDataException("插件通信内容无效");
    }
}
