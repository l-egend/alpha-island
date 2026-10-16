namespace alpha_island.Core;

public interface IPausable
{
    bool IsPaused { get; }
    void SetPaused(bool paused);
}

internal readonly record struct ResourceSamplingProfile(
    TimeSpan Cpu, TimeSpan Memory, TimeSpan HardwarePower, TimeSpan Network, bool PauseHardware);

internal static class ResourceSamplingPolicy
{
    internal static ResourceSamplingProfile For(bool resourcesVisible, bool networkVisible,
        bool hidden, bool gameQuiet, bool powerSave)
    {
        if (hidden || gameQuiet)
            return new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), true);
        double cpu = resourcesVisible ? 1 : 5;
        double memory = resourcesVisible ? 2 : 5;
        double hardware = resourcesVisible ? 2 : 15;
        if (powerSave) { cpu *= 2; memory *= 2; hardware *= 2; }
        // The visible bar, download alerts and session counters still consume network samples.
        return new(TimeSpan.FromSeconds(cpu), TimeSpan.FromSeconds(memory),
            TimeSpan.FromSeconds(hardware), TimeSpan.FromSeconds(networkVisible ? 1 : 2), false);
    }
}
