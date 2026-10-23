using System.IO;
using System.Media;

namespace alpha_island.Services;

internal static class VoiceWakeChime
{
    private static readonly byte[] Wave = CreateWave();
    internal static Task PlayAsync() => Task.Run(() =>
    {
        try { using var input = new MemoryStream(Wave, false); using var sound = new SoundPlayer(input); sound.PlaySync(); }
        catch (Exception) { /* An unavailable output device must not block microphone use. */ }
    });
    private static byte[] CreateWave()
    {
        const int rate = 22050, count = rate * 16 / 100;
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write("RIFF"u8); writer.Write(36 + count * 2); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(count * 2);
        for (int i = 0; i < count; i++)
        {
            double t = i / (double)rate;
            double envelope = Math.Min(1, i / 110d) * Math.Min(1, (count - i) / 330d);
            double value = (Math.Sin(2 * Math.PI * 660 * t) + .45 * Math.Sin(2 * Math.PI * 990 * t)) * .12 * envelope;
            writer.Write((short)(value * short.MaxValue));
        }
        return output.ToArray();
    }
}
