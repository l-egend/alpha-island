using NAudio.Dsp;

namespace alpha_island.Services;

/// <summary>Reusable microphone FFT. Output is borrowed until the next Push call.</summary>
internal sealed class MicrophoneSpectrum
{
    internal const int Bands = 16;
    private const int Size = 1024;
    private readonly float[] _samples = new float[Size];
    private readonly Complex[] _fft = new Complex[Size];
    private readonly float[] _bands = new float[Bands];
    private int _count;
    internal event Action<float[]>? Frame;
    internal void Push(float sample, int sampleRate)
    {
        _samples[_count++] = float.IsFinite(sample) ? Math.Clamp(sample, -1, 1) : 0;
        if (_count < Size) return;
        _count = 0;
        for (int i = 0; i < Size; i++) _fft[i] = new Complex { X = _samples[i] * (float)FastFourierTransform.HannWindow(i, Size) };
        FastFourierTransform.FFT(true, 10, _fft);
        double top = Math.Min(8000, sampleRate / 2d);
        for (int band = 0; band < Bands; band++)
        {
            int from = Math.Clamp((int)(90 * Math.Pow(top / 90, band / (double)Bands) * Size / sampleRate), 1, Size / 2 - 1);
            int to = Math.Clamp((int)(90 * Math.Pow(top / 90, (band + 1d) / Bands) * Size / sampleRate), from + 1, Size / 2);
            float magnitude = 0;
            for (int i = from; i < to; i++) magnitude = Math.Max(magnitude, MathF.Sqrt(_fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y));
            float value = Math.Clamp(magnitude * 7, 0, 1);
            _bands[band] = value > _bands[band] ? value : _bands[band] * .65f;
        }
        Frame?.Invoke(_bands);
    }
}
