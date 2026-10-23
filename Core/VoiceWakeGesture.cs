using System.Windows.Input;

namespace alpha_island.Core;

/// <summary>A held single key activates once. Chords and key repeat never activate it.</summary>
internal sealed class VoiceWakeGesture
{
    private Key _key = Key.RightCtrl;
    private long _pressedAt;
    private bool _down, _fired, _blocked;
    internal bool Pending => _down && !_fired;
    internal void Configure(Key key) { Reset(); _key = key; }
    internal void KeyChanged(Key key, bool down, ModifierKeys modifiers, long now, bool injected = false)
    {
        if (injected) return;
        if (key != _key) { if (down && (_down || _fired)) { _blocked = true; _down = false; } return; }
        if (!down) { Reset(); return; }
        ModifierKeys own = key is Key.RightCtrl or Key.LeftCtrl ? ModifierKeys.Control
            : key is Key.RightAlt or Key.LeftAlt ? ModifierKeys.Alt : key is Key.RightShift or Key.LeftShift ? ModifierKeys.Shift : ModifierKeys.None;
        if (_blocked) return;
        if ((modifiers & ~own) != ModifierKeys.None) { _blocked = true; _down = false; return; }
        if (!_down) { _down = true; _pressedAt = now; }
    }
    internal bool Tick(long now)
    {
        if (!Pending || now - _pressedAt < 600) return false;
        _fired = true; return true;
    }
    internal void Reset() { _down = _fired = _blocked = false; _pressedAt = 0; }
}
