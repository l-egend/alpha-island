using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace alpha_island.Animation;

/// <summary>Closes a cut clip once, at load time; playback only selects shared frozen frames.</summary>
internal static class AgentAnimationLoop
{
    internal static BitmapSource[] Close(BitmapSource[] frames, double fps, bool retrace)
    {
        if (frames.Length < 2) return frames;
        if (retrace)
        {
            // Resting, thinking and permission gestures return along their own motion.
            // One held frame at each turn softens the reversal; all frames remain shared.
            var cycle = new BitmapSource[frames.Length * 2]; frames.CopyTo(cycle, 0);
            int next = frames.Length; cycle[next++] = frames[^1];
            for (int i = frames.Length - 2; i > 0; i--) cycle[next++] = frames[i];
            cycle[next] = frames[0];
            return cycle;
        }
        int width = frames[0].PixelWidth, height = frames[0].PixelHeight, stride = width * 4;
        byte[] Pixels(BitmapSource frame)
        {
            // Interpolate premultiplied colour and alpha together, avoiding bright/black fringes.
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
            var bytes = new byte[stride * height]; converted.CopyPixels(bytes, stride, 0); return bytes;
        }
        static double Difference(byte[] a, byte[] b)
        {
            long sum = 0; for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
            return sum / (double)a.Length;
        }
        byte[] first = Pixels(frames[0]), previous = first;
        var steps = new double[frames.Length - 1];
        for (int i = 1; i < frames.Length; i++)
        {
            byte[] next = Pixels(frames[i]); steps[i-1] = Difference(previous, next); previous = next;
        }
        byte[] last = previous;
        Array.Sort(steps);
        double typical = Math.Max(1, steps[steps.Length / 2]), seam = Difference(last, first);
        if (seam <= typical * 1.35) return frames;
        int maximum = Math.Max(3, (int)Math.Ceiling(fps * .75));
        int count = Math.Clamp((int)Math.Ceiling(seam / typical * 1.5), 3, maximum);
        var result = new BitmapSource[frames.Length + count]; frames.CopyTo(result, 0);
        for (int i = 1; i <= count; i++)
        {
            double t = i / (double)(count + 1);
            double weight = t * t * (3 - 2 * t);
            var bytes = new byte[last.Length];
            for (int pixel = 0; pixel < bytes.Length; pixel++)
                bytes[pixel] = (byte)Math.Round(last[pixel] * (1 - weight) + first[pixel] * weight);
            var frame = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, bytes, stride);
            frame.Freeze(); result[frames.Length + i - 1] = frame;
        }
        return result;
    }
}
