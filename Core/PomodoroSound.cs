using System.Media;

namespace alpha_island.Core;

internal static class PomodoroSound
{
    internal static void Play(string name)
    {
        (name switch { "完成" => SystemSounds.Asterisk, "警告" => SystemSounds.Exclamation, _ => SystemSounds.Beep }).Play();
    }
}
