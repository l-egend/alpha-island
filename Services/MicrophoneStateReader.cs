using NAudio.CoreAudioApi;

namespace alpha_island.Services;

internal sealed record MicrophoneState(bool Available, bool Muted, string Device, DateTimeOffset SampledAt);
/// <summary>Reads capture endpoint controls; does not open an audio stream or record speech.</summary>
internal static class MicrophoneStateReader
{
    internal static MicrophoneState Read()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            return new(true, device.AudioEndpointVolume.Mute, device.FriendlyName, DateTimeOffset.UtcNow);
        }
        catch (Exception) { return new(false, false, "", DateTimeOffset.UtcNow); }
    }
}
