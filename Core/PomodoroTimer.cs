namespace alpha_island.Core;

internal sealed record PomodoroOptions(
    int FocusMinutes = 25, int ShortBreakMinutes = 5, bool UseLongBreak = false,
    int LongBreakMinutes = 15, int LongBreakEvery = 4, bool AutoStart = false,
    bool ShowSeconds = true, bool AutoCycle = false, int CycleCount = 4,
    bool DoubleClickOpensSettings = false, bool PlaySound = false, string Sound = "提示")
{
    internal static PomodoroOptions From(IslandOptions options) => new(
        Math.Clamp(SettingsRegistry.GetInt(options, "PomodoroFocusMinutes", 25), 1, 240),
        Math.Clamp(SettingsRegistry.GetInt(options, "PomodoroShortBreakMinutes", 5), 1, 120),
        SettingsRegistry.GetBool(options, "PomodoroUseLongBreak", false),
        Math.Clamp(SettingsRegistry.GetInt(options, "PomodoroLongBreakMinutes", 15), 1, 240),
        Math.Clamp(SettingsRegistry.GetInt(options, "PomodoroLongBreakEvery", 4), 1, 20),
        SettingsRegistry.GetBool(options, "PomodoroAutoStart", false),
        SettingsRegistry.GetBool(options, "PomodoroShowSeconds", true),
        SettingsRegistry.GetBool(options, "PomodoroAutoCycle", false),
        Math.Clamp(SettingsRegistry.GetInt(options, "PomodoroCycleCount", 4), 1, 100),
        SettingsRegistry.GetBool(options, "PomodoroDoubleClickSettings", false),
        SettingsRegistry.GetBool(options, "PomodoroPlaySound", false),
        SettingsRegistry.GetText(options, "PomodoroSound", "提示"));
}

internal sealed class PomodoroTimer
{
    public TimeSpan Remaining { get; private set; } = TimeSpan.FromMinutes(25);
    public bool Running { get; private set; }
    public bool HasStarted => _phaseStarted;
    public bool IsBreak { get; private set; }
    public bool IsLongBreak { get; private set; }
    public bool CycleFinished { get; private set; }
    public int CompletedFocus { get; private set; }
    public PomodoroOptions Options { get; private set; } = new();
    private DateTime _deadline;
    private bool _phaseStarted;

    public void Configure(PomodoroOptions options)
    {
        Options = options;
        // An unstarted phase uses edited durations immediately; a running/paused phase keeps its progress.
        if (!_phaseStarted)
        {
            IsLongBreak = IsBreak && options.UseLongBreak && CompletedFocus % options.LongBreakEvery == 0;
            Remaining = PhaseDuration;
        }
    }
    private TimeSpan PhaseDuration => TimeSpan.FromMinutes(!IsBreak ? Options.FocusMinutes
        : IsLongBreak ? Options.LongBreakMinutes : Options.ShortBreakMinutes);

    public void Start(DateTime now)
    {
        if (Running) return;
        if (CycleFinished) Reset();
        if (Remaining <= TimeSpan.Zero) Remaining = PhaseDuration;
        _deadline = now + Remaining;
        _phaseStarted = true;
        Running = true;
    }
    public void Pause(DateTime now)
    {
        if (!Running) return;
        Remaining = _deadline > now ? _deadline - now : TimeSpan.Zero;
        Running = false;
    }
    public void Reset()
    {
        Running = false; IsBreak = false; IsLongBreak = false; CycleFinished = false;
        _phaseStarted = false; CompletedFocus = 0; Remaining = PhaseDuration;
    }
    public bool Tick(DateTime now)
    {
        if (!Running) return false;
        Remaining = _deadline > now ? _deadline - now : TimeSpan.Zero;
        if (Remaining > TimeSpan.Zero) return false;
        Running = false; _phaseStarted = false;
        if (IsBreak && Options.AutoCycle && CompletedFocus >= Options.CycleCount)
        {
            IsBreak = false; IsLongBreak = false; CycleFinished = true; Remaining = PhaseDuration;
            return true;
        }
        if (!IsBreak) CompletedFocus++;
        IsBreak = !IsBreak;
        IsLongBreak = IsBreak && Options.UseLongBreak && CompletedFocus % Options.LongBreakEvery == 0;
        Remaining = PhaseDuration;
        // Resume from now so sleep cannot cause a flood of missed phase notifications.
        if (Options.AutoCycle) Start(now);
        return true;
    }
    internal string DisplayRemaining()
    {
        if (!Options.ShowSeconds) return $"{(int)Math.Ceiling(Remaining.TotalMinutes):00} 分钟";
        int seconds = Math.Max(0, (int)Math.Ceiling(Remaining.TotalSeconds));
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }
}
