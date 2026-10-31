using BitcoinWallpaper.Chart.Core.Pause;

namespace BitcoinWallpaper.Host.Playback;

/// <summary>Polls the window state every PollInterval, feeds PauseDecision and PauseDebouncer and raises
/// PausedChanged. No notification exists for "a fullscreen app started", hence the poll.</summary>
public sealed class PlaybackMonitor : IPlaybackMonitor
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IPauseSnapshotSource source;
    private readonly TimeProvider time;
    private readonly PauseDebouncer debouncer;
    private readonly object gate = new();
    private ITimer? timer;
    private int polling;
    private bool disposed;

    public PlaybackMonitor(IPauseSnapshotSource source, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        this.source = source;
        this.time = time ?? TimeProvider.System;
        debouncer = new PauseDebouncer(this.time);
    }

    /// <summary>A monitor reading the real desktop through Win32, for the primary monitor.</summary>
    public PlaybackMonitor()
        : this(new Win32PauseSnapshotSource())
    {
    }

    public PlaybackState State { get; private set; } = PlaybackState.Running;

    public PauseVerdict LastVerdict { get; private set; } = PauseVerdict.Running;

    public event EventHandler<PausedChangedEventArgs>? PausedChanged;

    public event EventHandler<PlaybackSample>? Sampled;

    public void Start()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            timer ??= time.CreateTimer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
        }
    }

    /// <summary>One poll; the timer calls it, and a tick that arrives while the previous one runs is skipped.</summary>
    internal void Poll()
    {
        if (Interlocked.Exchange(ref polling, 1) == 1)
        {
            return;
        }

        try
        {
            PauseSnapshot snapshot;
            try
            {
                snapshot = source.Capture();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                or DllNotFoundException or EntryPointNotFoundException)
            {
                return; // keep the last state; the next tick tries again
            }

            var verdict = PauseDecision.Evaluate(snapshot);
            var previous = State;
            var state = debouncer.Update(verdict.State);
            LastVerdict = verdict;
            State = state;

            Sampled?.Invoke(this, new PlaybackSample(snapshot, verdict, state));
            if (state != previous)
            {
                PausedChanged?.Invoke(this, new PausedChangedEventArgs(state, verdict));
            }
        }
        finally
        {
            Volatile.Write(ref polling, 0);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            timer?.Dispose();
            timer = null;
        }
    }
}
