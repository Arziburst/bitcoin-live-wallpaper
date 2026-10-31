using BitcoinWallpaper.Chart.Core.Pause;

namespace BitcoinWallpaper.Host.Playback;

/// <summary>Tells the host when to stop and resume presenting: paused while a fullscreen app, a maximised
/// window or other windows hide the wallpaper, or the session is locked (D-2, D-6).</summary>
public interface IPlaybackMonitor : IDisposable
{
    /// <summary>The debounced state; Running until the first poll says otherwise.</summary>
    PlaybackState State { get; }

    /// <summary>The verdict of the last poll, before debouncing.</summary>
    PauseVerdict LastVerdict { get; }

    /// <summary>Raised when State changes. Runs on a timer thread: the handler marshals to its own thread
    /// without blocking on it (Post / BeginInvoke), because Dispose waits for a running poll.</summary>
    event EventHandler<PausedChangedEventArgs>? PausedChanged;

    /// <summary>Raised after every poll, for diagnostics (tools/PauseProbe). Runs on a timer thread.</summary>
    event EventHandler<PlaybackSample>? Sampled;

    /// <summary>Starts polling; a second call does nothing.</summary>
    void Start();

    /// <summary>Stops polling: no event is raised after it returns, and a poll running on another thread has
    /// finished by then (like Dispose). Start polls again; Stop when not polling does nothing. Not a Dispose.</summary>
    void Stop();
}

public sealed class PausedChangedEventArgs(PlaybackState state, PauseVerdict verdict) : EventArgs
{
    public PlaybackState State { get; } = state;

    public bool IsPaused => State == PlaybackState.Paused;

    /// <summary>The poll verdict that caused the change; its reason is None on resume.</summary>
    public PauseVerdict Verdict { get; } = verdict;
}

/// <summary>One poll: what was seen, what it means and the debounced state after it.</summary>
public sealed class PlaybackSample(PauseSnapshot snapshot, PauseVerdict verdict, PlaybackState state,
    TimeSpan? guardTimeLeft) : EventArgs
{
    public PlaybackSample(PauseSnapshot snapshot, PauseVerdict verdict, PlaybackState state)
        : this(snapshot, verdict, state, null)
    {
    }

    public PauseSnapshot Snapshot { get; } = snapshot;

    public PauseVerdict Verdict { get; } = verdict;

    public PlaybackState State { get; } = state;

    /// <summary>With reason NotificationUncorroborated: how long the stale-flag guard still pauses without
    /// window evidence (NotificationGuard.TimeLeft); otherwise null.</summary>
    public TimeSpan? GuardTimeLeft { get; } = guardTimeLeft;
}
