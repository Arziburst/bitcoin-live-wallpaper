namespace BitcoinWallpaper.Chart.Core.Pause;

/// <summary>Pauses at once, resumes only after ResumeDelay of uninterrupted Running observations, so alt-tab
/// flicker does not thrash. No timers: State changes only inside Update.</summary>
public sealed class PauseDebouncer
{
    private readonly TimeProvider time;
    private long? streakStart;

    public PauseDebouncer(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        this.time = time;
    }

    public static TimeSpan ResumeDelay { get; } = TimeSpan.FromSeconds(1);

    /// <summary>Running before the first observation.</summary>
    public PlaybackState State { get; private set; } = PlaybackState.Running;

    /// <summary>Feeds one poll result and returns the debounced State.</summary>
    public PlaybackState Update(PlaybackState observed)
    {
        if (observed == PlaybackState.Paused)
        {
            State = PlaybackState.Paused;
            streakStart = null;
            return State;
        }

        if (State == PlaybackState.Running)
        {
            return State;
        }

        var now = time.GetTimestamp();
        streakStart ??= now;
        if (time.GetElapsedTime(streakStart.Value, now) >= ResumeDelay)
        {
            State = PlaybackState.Running;
            streakStart = null;
        }

        return State;
    }
}
