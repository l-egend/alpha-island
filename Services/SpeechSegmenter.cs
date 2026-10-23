using AlphaIsland.Speech;

namespace alpha_island.Services;

/// <summary>Bounded local segmentation: short pre-roll, silence endpoint, fixed maximum duration; silent frames never upload.</summary>
internal sealed class SpeechSegmenter(double maximumSeconds = 10, double silenceSeconds = .5)
{
    private readonly short[] _samples = new short[(int)(SpeechAudio.SampleRate * (maximumSeconds + 1))];
    private readonly Queue<short> _preRoll = new();
    private int _count, _voiced, _silence;
    internal event Action<SpeechAudio>? Ready;
    internal bool Push(ReadOnlySpan<short> pcm)
    {
        double energy = 0;
        foreach (short sample in pcm) { double value = sample / 32768d; energy += value * value; }
        bool active = pcm.Length > 0 && Math.Sqrt(energy / pcm.Length) >= .008;
        if (_count == 0 && !active)
        {
            foreach (short sample in pcm) _preRoll.Enqueue(sample);
            while (_preRoll.Count > SpeechAudio.SampleRate / 5) _preRoll.Dequeue();
            return false;
        }
        if (_count == 0) { while (_preRoll.TryDequeue(out short sample)) _samples[_count++] = sample; }
        foreach (short sample in pcm)
        {
            _samples[_count++] = sample;
            if (active) { _voiced++; _silence = 0; } else _silence++;
            if (_count >= SpeechAudio.SampleRate * maximumSeconds || _silence >= SpeechAudio.SampleRate * silenceSeconds) Flush();
        }
        return active;
    }
    internal void Flush()
    {
        if (_voiced >= SpeechAudio.SampleRate * .15)
            Ready?.Invoke(SpeechAudio.FromPcm(_samples.AsSpan(0, _count)));
        _count = _voiced = _silence = 0; _preRoll.Clear();
    }
    internal void Clear() { Array.Clear(_samples); _count = _voiced = _silence = 0; _preRoll.Clear(); }
}
