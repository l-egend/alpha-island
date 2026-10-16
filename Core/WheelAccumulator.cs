namespace alpha_island.Core;

internal sealed class WheelAccumulator
{
    private int _remainder;
    private long _lastTick;
    internal int Consume(int delta, long? tick = null)
    {
        long now = tick ?? Environment.TickCount64;
        if (now - _lastTick > 500 || Math.Sign(delta) != Math.Sign(_remainder)) _remainder = 0;
        _lastTick = now;
        _remainder = (int)Math.Clamp((long)_remainder + delta, -1200, 1200);
        int steps = _remainder / 120;
        _remainder %= 120;
        return steps;
    }
}
