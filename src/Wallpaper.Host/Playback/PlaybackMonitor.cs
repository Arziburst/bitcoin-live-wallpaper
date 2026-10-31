using BitcoinWallpaper.Chart.Core.Pause;

namespace BitcoinWallpaper.Host.Playback;

/// <summary>Polls the window state every PollInterval, feeds NotificationGuard (over PauseDecision) and
/// PauseDebouncer and raises PausedChanged. No notification exists for "a fullscreen app started", hence the
/// poll. Nothing a handler throws leaves the timer callback; Dispose waits for a running poll.</summary>
public sealed class PlaybackMonitor : IPlaybackMonitor
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IPauseSnapshotSource source;
    private readonly TimeProvider time;
    private readonly Action<string> log;
    private readonly NotificationGuard guard;
    private readonly PauseDebouncer debouncer;
    private readonly Lock gate = new();

    // Held for a whole poll, handlers included; Dispose takes it to wait for one. Reentrant, so Dispose from
    // a handler on the polling thread does not deadlock.
    private readonly object pollLock = new();
    private ITimer? timer;
    private volatile bool disposed;

    // Bumped by every Start and Stop; a timer callback polls only while its own Start is the current one, so a
    // tick a stopped timer still delivers does nothing.
    private volatile int generation;

    // State and LastVerdict are published together as one immutable object: readers on other threads see
    // the latest one and never a torn verdict.
    private volatile Published published = new(PlaybackState.Running, PauseVerdict.Running);

    public PlaybackMonitor(IPauseSnapshotSource source, TimeProvider? time)
        : this(source, time, null)
    {
    }

    /// <param name="log">Where a caught exception is reported, one call each; the host's log when null.</param>
    public PlaybackMonitor(IPauseSnapshotSource source, TimeProvider? time = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        this.source = source;
        this.time = time ?? TimeProvider.System;
        this.log = log ?? Log.Warn;
        guard = new NotificationGuard(this.time);
        debouncer = new PauseDebouncer(this.time);
    }

    /// <summary>A monitor reading the real desktop through Win32, for the primary monitor.</summary>
    public PlaybackMonitor()
        : this(new Win32PauseSnapshotSource())
    {
    }

    public PlaybackState State => published.State;

    public PauseVerdict LastVerdict => published.Verdict;

    public event EventHandler<PausedChangedEventArgs>? PausedChanged;

    public event EventHandler<PlaybackSample>? Sampled;

    public void Start()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (timer is null)
            {
                var current = ++generation;
                timer = time.CreateTimer(_ => Poll(current), null, TimeSpan.Zero, PollInterval);
            }
        }
    }

    /// <summary>Stops polling (Off from the tray, #23) and returns once a poll running on another thread has
    /// finished; no event is raised after it returns. Start polls again; Stop when not polling does nothing.</summary>
    public void Stop()
    {
        lock (gate)
        {
            if (timer is null)
            {
                return;
            }

            generation++;
            timer.Dispose();
            timer = null;
        }

        lock (pollLock)
        {
        }
    }

    /// <summary>One poll; the timer of the Start numbered <paramref name="current"/> calls it. A tick that
    /// arrives while the previous one runs is skipped, and one that arrives after Stop or Dispose does nothing.
    /// Never throws.</summary>
    internal void Poll(int current)
    {
        if (!IsCurrent(current) || !Monitor.TryEnter(pollLock))
        {
            return;
        }

        try
        {
            if (IsCurrent(current))
            {
                PollOnce(current);
            }
        }
        catch (Exception ex)
        {
            Report("poll", ex);
        }
        finally
        {
            Monitor.Exit(pollLock);
        }
    }

    private bool IsCurrent(int current) => !disposed && generation == current;

    private void PollOnce(int current)
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

        var verdict = guard.Evaluate(snapshot);
        var previous = State;
        var state = debouncer.Update(verdict.State);
        published = new Published(state, verdict);

        Raise(Sampled, new PlaybackSample(snapshot, verdict, state, guard.TimeLeft), nameof(Sampled), current);
        if (state != previous && IsCurrent(current))
        {
            Raise(PausedChanged, new PausedChangedEventArgs(state, verdict), nameof(PausedChanged), current);
        }
    }

    /// <summary>Calls each handler on its own, so one that throws neither stops the others nor the poll.</summary>
    private void Raise<T>(EventHandler<T>? handlers, T args, string name, int current)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            if (!IsCurrent(current))
            {
                return;
            }

            try
            {
                ((EventHandler<T>)handler)(this, args);
            }
            catch (Exception ex)
            {
                Report(name + " handler", ex);
            }
        }
    }

    private void Report(string where, Exception ex)
    {
        try
        {
            log($"Playback monitor: {where} threw {ex.GetType().Name}: {ex.Message}");
        }
        catch
        {
            // A failing log must not take the timer thread down either.
        }
    }

    /// <summary>Stops polling and returns once a poll running on another thread has finished; no event is
    /// raised after it returns.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            timer?.Dispose();
            timer = null;
        }

        lock (pollLock)
        {
        }
    }

    private sealed record Published(PlaybackState State, PauseVerdict Verdict);
}
