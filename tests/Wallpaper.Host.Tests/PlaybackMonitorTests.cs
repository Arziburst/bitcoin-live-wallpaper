using System.Collections.Concurrent;
using BitcoinWallpaper.Chart.Core.Pause;
using BitcoinWallpaper.Host.Playback;

namespace BitcoinWallpaper.Host.Tests;

// Contract #16, criterion 2: PlaybackMonitor never lets a handler exception escape the timer callback (caught
// and logged), State/LastVerdict are safe to read from other threads, Dispose waits for a running poll, and
// the foreground gets the same visible/cloaked filters as the other windows. Plus criterion 1 end to end: the
// monitor applies NotificationGuard (StaleBusyGuardTests.cs) to every poll.
//
// API SKETCH (written before the code, #237). Namespace BitcoinWallpaper.Host.Playback, src/Wallpaper.Host/
// Playback/. IPlaybackMonitor stays source-compatible (additions only).
//
//   public interface IPauseSnapshotSource { PauseSnapshot Capture(); }      // exists: the seam the tests fake
//
//   public sealed class PlaybackMonitor : IPlaybackMonitor
//   {
//       public PlaybackMonitor(IPauseSnapshotSource source, TimeProvider? time = null,
//           Action<string>? log = null);
//           // NEW optional last parameter: where a caught handler exception is reported, one call per caught
//           // exception, the text containing the exception's Message. Null: the host's Log (Log.Warn/Error).
//       // everything else as today: Start, State, LastVerdict, PausedChanged, Sampled, Dispose, PollInterval
//   }
//
//   public readonly record struct WindowFlags(bool IsVisible, bool IsIconic, bool IsCloaked, bool IsClickThrough);
//   public sealed record CandidateWindow(WindowInfo Info, WindowFlags Flags);
//   public static class WindowFilter
//   {
//       // Really on screen: visible, not minimised, not cloaked, not click-through (WS_EX_TRANSPARENT).
//       public static bool IsOnScreen(WindowFlags flags);
//       // The snapshot from what Win32 reported: Foreground is foreground.Info when it IsOnScreen, else null;
//       // VisibleWindows are the Info of the windows that IsOnScreen, in their order. Monitor, notification
//       // and session pass through. Win32PauseSnapshotSource.Capture builds its snapshot with this (review).
//       public static PauseSnapshot BuildSnapshot(PixelRect monitor, CandidateWindow? foreground,
//           IReadOnlyList<CandidateWindow> windows, NotificationState notification, SessionState session);
//   }
//
// Rules the tests rely on:
// - The monitor polls only through time.CreateTimer(...) of the TimeProvider it was given (as today); the
//   fake here runs the callback when the test says so, on the thread the test chooses.
// - Each poll runs the snapshot through a NotificationGuard (one per monitor, same TimeProvider), then the
//   PauseDebouncer; LastVerdict is the guard's verdict.
// - No exception from a Sampled or PausedChanged handler leaves the timer callback. A throwing Sampled
//   handler does not stop that poll's PausedChanged. Each caught exception is logged through `log`.
// - Dispose returns only after a poll in progress on another thread has finished; no Sampled/PausedChanged
//   is raised after Dispose returned, and a timer callback that still arrives after Dispose (real timers can
//   deliver a queued one) neither captures nor raises anything. Dispose called from inside a handler (on the
//   polling thread) returns without deadlock.
// - State and LastVerdict written by a poll on one thread are seen by a reader spinning on another thread,
//   and LastVerdict is never read torn (a Running verdict always has reason None).
public class PlaybackMonitorTests
{
    private static readonly PixelRect Tv = new(0, 0, 3840, 2160);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static readonly WindowInfo Editor = new("Notepad", new PixelRect(200, 200, 1800, 1200), Handle: 0x100);
    private static readonly WindowInfo Game = new("UnrealWindow", Tv, Handle: 0x200);

    private static PauseSnapshot Quiet() =>
        new(Tv, Editor, [Editor], NotificationState.AcceptsNotifications, SessionState.Active);

    private static PauseSnapshot Fullscreen() =>
        new(Tv, Game, [Game], NotificationState.AcceptsNotifications, SessionState.Active);

    private static PauseSnapshot StaleBusy() =>
        new(Tv, Editor, [Editor], NotificationState.Busy, SessionState.Active);

    // ---- fakes ----

    /// <summary>A manual clock whose timers fire only when the test calls Tick.</summary>
    private sealed class FakeTime : TimeProvider
    {
        private long ticks = new DateTimeOffset(2026, 10, 31, 20, 0, 0, TimeSpan.Zero).UtcTicks;

        public List<FakeTimer> Timers { get; } = [];

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(callback, state);
            lock (Timers)
            {
                Timers.Add(timer);
            }

            return timer;
        }

        /// <summary>The monitor's polling timer: exactly one is created by Start.</summary>
        public FakeTimer Timer
        {
            get
            {
                lock (Timers)
                {
                    return Assert.Single(Timers);
                }
            }
        }
    }

    private sealed class FakeTimer(TimerCallback callback, object? state) : ITimer
    {
        public bool Disposed { get; private set; }

        /// <summary>Runs the timer callback on the calling thread, as the thread pool would; also after
        /// Dispose, because a real timer may still deliver a callback that was already queued.</summary>
        public void Tick() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSource : IPauseSnapshotSource
    {
        private int captures;

        public Func<PauseSnapshot> Next { get; set; } = Quiet;

        /// <summary>When set, Capture signals Entered and blocks until Release is set.</summary>
        public ManualResetEventSlim? Release { get; set; }

        public ManualResetEventSlim Entered { get; } = new();

        public int Captures => Volatile.Read(ref captures);

        public PauseSnapshot Capture()
        {
            Interlocked.Increment(ref captures);
            Entered.Set();
            if (Release is { } release && !release.Wait(Wait))
            {
                throw new TimeoutException("the test never released Capture");
            }

            return Next();
        }
    }

    /// <summary>Runs an action on its own background thread (not the thread pool); an exception it throws is
    /// kept in Error instead of crashing the test run, and a hung one does not keep the process alive.</summary>
    private sealed class Background
    {
        private readonly Thread thread;
        private Exception? error;

        public Background(Action action)
        {
            thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref error, ex);
                }
            }) { IsBackground = true };
            thread.Start();
        }

        public Exception? Error => Volatile.Read(ref error);

        public bool Join(TimeSpan timeout) => thread.Join(timeout);
    }

    private readonly FakeTime time = new();
    private readonly FakeSource source = new();
    private readonly ConcurrentQueue<string> log = new();

    private PlaybackMonitor NewMonitor() => new(source, time, line => log.Enqueue(line));

    private static void AssertNoThrow(Action action, string what)
    {
        var thrown = Record.Exception(action);
        Assert.True(thrown is null, $"{what} let an exception escape the timer callback: {thrown}");
    }

    // ---- handler exceptions ----

    [Fact]
    public void Throwing_PausedChanged_handler_is_caught_logged_and_polling_goes_on()
    {
        using var monitor = NewMonitor();
        monitor.PausedChanged += (_, _) => throw new InvalidOperationException("boom-paused-changed");
        var samples = 0;
        monitor.Sampled += (_, _) => Interlocked.Increment(ref samples);
        monitor.Start();

        source.Next = Fullscreen;
        AssertNoThrow(time.Timer.Tick, "a throwing PausedChanged handler");
        Assert.Equal(PlaybackState.Paused, monitor.State);
        Assert.Contains(log, line => line.Contains("boom-paused-changed", StringComparison.Ordinal));

        // The next polls still run and still resume.
        source.Next = Quiet;
        AssertNoThrow(time.Timer.Tick, "a poll after a handler threw");
        time.Advance(PauseDebouncer.ResumeDelay);
        AssertNoThrow(time.Timer.Tick, "a resuming poll with a throwing PausedChanged handler");
        Assert.Equal(3, source.Captures);
        Assert.Equal(3, samples);
        Assert.Equal(PlaybackState.Running, monitor.State);
    }

    [Fact]
    public void Throwing_Sampled_handler_is_caught_logged_and_the_state_change_is_still_announced()
    {
        using var monitor = NewMonitor();
        monitor.Sampled += (_, _) => throw new InvalidOperationException("boom-sampled");
        var changes = new ConcurrentQueue<PlaybackState>();
        monitor.PausedChanged += (_, e) => changes.Enqueue(e.State);
        monitor.Start();

        source.Next = Fullscreen;
        AssertNoThrow(time.Timer.Tick, "a throwing Sampled handler");
        Assert.Contains(log, line => line.Contains("boom-sampled", StringComparison.Ordinal));
        Assert.Equal([PlaybackState.Paused], changes);

        // A lost PausedChanged(Running) would keep the wallpaper paused for good.
        source.Next = Quiet;
        AssertNoThrow(time.Timer.Tick, "a throwing Sampled handler");
        time.Advance(PauseDebouncer.ResumeDelay);
        AssertNoThrow(time.Timer.Tick, "a throwing Sampled handler");
        Assert.Equal([PlaybackState.Paused, PlaybackState.Running], changes);
        Assert.Equal(3, source.Captures);
    }

    [Theory]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(System.ComponentModel.Win32Exception))]
    [InlineData(typeof(ObjectDisposedException))]
    public void Any_handler_exception_type_is_caught(Type exceptionType)
    {
        using var monitor = NewMonitor();
        monitor.PausedChanged += (_, _) => throw MakeException(exceptionType, "boom-" + exceptionType.Name);
        monitor.Start();

        source.Next = Fullscreen;
        AssertNoThrow(time.Timer.Tick, $"a PausedChanged handler throwing {exceptionType.Name}");
        Assert.Contains(log, line => line.Contains("boom-" + exceptionType.Name, StringComparison.Ordinal));
    }

    private static Exception MakeException(Type type, string message) => type == typeof(ObjectDisposedException)
        ? new ObjectDisposedException("object", message)
        : (Exception)Activator.CreateInstance(type, message)!;

    // ---- Dispose ----

    [Fact]
    public void Dispose_waits_for_a_poll_in_progress()
    {
        var monitor = NewMonitor();
        monitor.Start();
        using var release = new ManualResetEventSlim();
        source.Release = release;
        source.Next = Fullscreen;

        var poll = new Background(time.Timer.Tick);
        Assert.True(source.Entered.Wait(Wait), "the poll never reached Capture");

        var dispose = new Background(monitor.Dispose);
        var returnedEarly = dispose.Join(TimeSpan.FromMilliseconds(500));
        release.Set();
        Assert.True(poll.Join(Wait), "the poll did not finish after Capture was released");
        Assert.Null(poll.Error);
        Assert.True(dispose.Join(Wait), "Dispose did not return after the poll finished");
        Assert.False(returnedEarly, "Dispose returned while a poll was still inside Capture");
    }

    [Fact]
    public void No_event_is_raised_after_Dispose_returned()
    {
        var monitor = NewMonitor();
        var disposeReturned = 0;
        var late = new ConcurrentQueue<string>();
        monitor.Sampled += (_, _) =>
        {
            if (Volatile.Read(ref disposeReturned) == 1)
            {
                late.Enqueue("Sampled");
            }
        };
        monitor.PausedChanged += (_, _) =>
        {
            if (Volatile.Read(ref disposeReturned) == 1)
            {
                late.Enqueue("PausedChanged");
            }
        };
        monitor.Start();
        var timer = time.Timer;

        // A poll in progress while Dispose runs on another thread.
        using var release = new ManualResetEventSlim();
        source.Release = release;
        source.Next = Fullscreen;
        var poll = new Background(timer.Tick);
        Assert.True(source.Entered.Wait(Wait), "the poll never reached Capture");
        var dispose = new Background(() =>
        {
            monitor.Dispose();
            Volatile.Write(ref disposeReturned, 1);
        });
        dispose.Join(TimeSpan.FromMilliseconds(500));
        release.Set();
        Assert.True(poll.Join(Wait), "the poll did not finish after Capture was released");
        Assert.Null(poll.Error);
        Assert.True(dispose.Join(Wait), "Dispose did not return");

        // A tick the timer had already queued arrives after Dispose.
        source.Release = null;
        var capturesBefore = source.Captures;
        AssertNoThrow(timer.Tick, "a tick arriving after Dispose");

        Assert.Empty(late);
        Assert.Equal(capturesBefore, source.Captures);
    }

    [Fact]
    public void Dispose_from_inside_a_handler_returns_and_stops_polling()
    {
        var monitor = NewMonitor();
        monitor.PausedChanged += (_, _) => monitor.Dispose();
        monitor.Start();
        var timer = time.Timer;

        source.Next = Fullscreen;
        var poll = new Background(timer.Tick);
        Assert.True(poll.Join(Wait), "Dispose called from a PausedChanged handler deadlocked the poll");
        Assert.Null(poll.Error);

        var samples = 0;
        monitor.Sampled += (_, _) => Interlocked.Increment(ref samples);
        AssertNoThrow(timer.Tick, "a tick arriving after Dispose");
        Assert.Equal(1, source.Captures);
        Assert.Equal(0, samples);
    }

    // ---- reads from other threads ----

    [Fact]
    public void A_reader_spinning_on_another_thread_sees_the_published_State_and_LastVerdict()
    {
        using var monitor = NewMonitor();
        monitor.Start();
        var timer = time.Timer;

        using var spinning = new CountdownEvent(2);
        var sawState = new ManualResetEventSlim();
        var sawVerdict = new ManualResetEventSlim();
        StartSpinner(() => SpinUntilPausedState(monitor, spinning, sawState));
        StartSpinner(() => SpinUntilPausedVerdict(monitor, spinning, sawVerdict));
        Assert.True(spinning.Wait(Wait), "the readers never started");
        Thread.Sleep(300); // let the readers' loops be compiled with optimisations (OSR / tier-1)

        source.Next = Fullscreen;
        var poll = new Background(timer.Tick);
        Assert.True(poll.Join(Wait), "the poll did not finish");

        Assert.True(sawState.Wait(Wait), "a reader on another thread never saw State become Paused");
        Assert.True(sawVerdict.Wait(Wait), "a reader on another thread never saw LastVerdict become Paused");
    }

    private static void StartSpinner(Action loop) => new Thread(() => loop()) { IsBackground = true }.Start();

    private static void SpinUntilPausedState(PlaybackMonitor monitor, CountdownEvent spinning, ManualResetEventSlim saw)
    {
        spinning.Signal();
        while (monitor.State == PlaybackState.Running)
        {
        }

        saw.Set();
    }

    private static void SpinUntilPausedVerdict(PlaybackMonitor monitor, CountdownEvent spinning, ManualResetEventSlim saw)
    {
        spinning.Signal();
        while (monitor.LastVerdict.State == PlaybackState.Running)
        {
        }

        saw.Set();
    }

    [Fact]
    public void LastVerdict_is_never_read_torn_while_polls_change_it()
    {
        using var monitor = NewMonitor();
        monitor.Start();
        var timer = time.Timer;
        var fullscreen = Fullscreen();
        var quiet = Quiet();
        var flip = 0;
        source.Next = () => Interlocked.Increment(ref flip) % 2 == 0 ? fullscreen : quiet;

        var expected = new HashSet<PauseVerdict>
        {
            PauseVerdict.Running,
            PauseVerdict.Paused(PauseReason.Fullscreen),
        };
        var stop = 0;
        var torn = new ConcurrentQueue<PauseVerdict>();
        var reader = new Thread(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                var verdict = monitor.LastVerdict;
                if (!expected.Contains(verdict))
                {
                    torn.Enqueue(verdict);
                }
            }
        }) { IsBackground = true };
        reader.Start();

        var until = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < until && torn.IsEmpty)
        {
            for (var i = 0; i < 1000; i++)
            {
                timer.Tick();
            }
        }

        Volatile.Write(ref stop, 1);
        Assert.True(reader.Join(Wait), "the reader did not stop");
        Assert.True(torn.IsEmpty, $"LastVerdict was read torn: {string.Join(", ", torn.Take(5))}");
    }

    // ---- criterion 1 end to end: the monitor applies the stale Busy guard ----

    [Fact]
    public void A_stale_busy_flag_pauses_for_two_minutes_then_the_monitor_resumes()
    {
        using var monitor = NewMonitor();
        var changes = new ConcurrentQueue<PausedChangedEventArgs>();
        monitor.PausedChanged += (_, e) => changes.Enqueue(e);
        monitor.Start();
        source.Next = StaleBusy;

        time.Timer.Tick();
        Assert.Equal(PlaybackState.Paused, monitor.State);
        Assert.Equal(PauseVerdict.Paused(PauseReason.NotificationUncorroborated), monitor.LastVerdict);

        time.Advance(NotificationGuard.UncorroboratedLimit - TimeSpan.FromMilliseconds(1));
        time.Timer.Tick();
        Assert.Equal(PlaybackState.Paused, monitor.State);

        time.Advance(TimeSpan.FromMilliseconds(1));
        time.Timer.Tick();
        Assert.Equal(PauseVerdict.Running, monitor.LastVerdict);

        time.Advance(PauseDebouncer.ResumeDelay);
        time.Timer.Tick();
        Assert.Equal(PlaybackState.Running, monitor.State);
        Assert.Equal([PlaybackState.Paused, PlaybackState.Running], changes.Select(e => e.State));
        Assert.Equal(PauseReason.NotificationUncorroborated, changes.First().Verdict.Reason);
    }

    [Fact]
    public void A_corroborated_busy_flag_keeps_the_monitor_paused()
    {
        using var monitor = NewMonitor();
        monitor.Start();
        source.Next = () => new PauseSnapshot(Tv, Game, [Game], NotificationState.RunningD3DFullScreen,
            SessionState.Active);

        time.Timer.Tick();
        time.Advance(TimeSpan.FromMinutes(10));
        time.Timer.Tick();
        Assert.Equal(PlaybackState.Paused, monitor.State);
        Assert.Equal(PauseVerdict.Paused(PauseReason.NotificationCorroborated), monitor.LastVerdict);
    }

    // ---- the foreground gets the same on-screen filters as the other windows ----

    public static TheoryData<bool, bool, bool, bool> AllFlagCombinations()
    {
        var data = new TheoryData<bool, bool, bool, bool>();
        foreach (var visible in new[] { true, false })
        foreach (var iconic in new[] { true, false })
        foreach (var cloaked in new[] { true, false })
        foreach (var clickThrough in new[] { true, false })
        {
            data.Add(visible, iconic, cloaked, clickThrough);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllFlagCombinations))]
    public void On_screen_means_visible_not_minimised_not_cloaked_not_click_through(
        bool visible, bool iconic, bool cloaked, bool clickThrough)
    {
        var flags = new WindowFlags(visible, iconic, cloaked, clickThrough);
        Assert.Equal(visible && !iconic && !cloaked && !clickThrough, WindowFilter.IsOnScreen(flags));
    }

    [Theory]
    [MemberData(nameof(AllFlagCombinations))]
    public void Foreground_is_filtered_exactly_like_the_enumerated_windows(
        bool visible, bool iconic, bool cloaked, bool clickThrough)
    {
        var candidate = new CandidateWindow(Game, new WindowFlags(visible, iconic, cloaked, clickThrough));
        var snapshot = WindowFilter.BuildSnapshot(Tv, candidate, [candidate], NotificationState.AcceptsNotifications,
            SessionState.Active);

        var keptInList = snapshot.VisibleWindows.Contains(Game);
        var keptAsForeground = snapshot.Foreground is not null;
        Assert.Equal(keptInList, keptAsForeground);
        Assert.Equal(WindowFilter.IsOnScreen(candidate.Flags), keptAsForeground);
        if (keptAsForeground)
        {
            Assert.Same(Game, snapshot.Foreground);
        }
    }

    [Theory]
    [InlineData(false, false, true, false)] // cloaked: on another virtual desktop, a suspended UWP app
    [InlineData(false, false, false, true)] // click-through overlay (WS_EX_TRANSPARENT)
    [InlineData(true, false, false, false)] // hidden
    public void Filtered_fullscreen_foreground_does_not_pause(bool hidden, bool iconic, bool cloaked, bool clickThrough)
    {
        var foreground = new CandidateWindow(Game, new WindowFlags(!hidden, iconic, cloaked, clickThrough));
        var snapshot = WindowFilter.BuildSnapshot(Tv, foreground, [foreground], NotificationState.AcceptsNotifications,
            SessionState.Active);

        Assert.Null(snapshot.Foreground);
        Assert.Equal(PauseVerdict.Running, PauseDecision.Evaluate(snapshot));
    }

    [Fact]
    public void BuildSnapshot_keeps_on_screen_windows_in_order_and_passes_the_rest_through()
    {
        var onScreen = new WindowFlags(IsVisible: true, IsIconic: false, IsCloaked: false, IsClickThrough: false);
        var a = new WindowInfo("A", new PixelRect(0, 0, 10, 10), Handle: 1);
        var b = new WindowInfo("B", new PixelRect(0, 0, 20, 20), Handle: 2);
        var c = new WindowInfo("C", new PixelRect(0, 0, 30, 30), Handle: 3);
        var monitor = new PixelRect(3840, 0, 8960, 1440);
        var windows = new[]
        {
            new CandidateWindow(a, onScreen),
            new CandidateWindow(b, onScreen with { IsCloaked = true }),
            new CandidateWindow(c, onScreen),
        };

        var snapshot = WindowFilter.BuildSnapshot(monitor, new CandidateWindow(c, onScreen), windows,
            NotificationState.QuietTime, SessionState.Locked);

        Assert.Equal(monitor, snapshot.Monitor);
        Assert.Same(c, snapshot.Foreground);
        Assert.Equal([a, c], snapshot.VisibleWindows);
        Assert.Equal(NotificationState.QuietTime, snapshot.Notification);
        Assert.Equal(SessionState.Locked, snapshot.Session);
    }

    [Fact]
    public void BuildSnapshot_without_a_foreground_has_none()
    {
        var snapshot = WindowFilter.BuildSnapshot(Tv, null, [], NotificationState.AcceptsNotifications,
            SessionState.Active);
        Assert.Null(snapshot.Foreground);
        Assert.Empty(snapshot.VisibleWindows);
    }
}
