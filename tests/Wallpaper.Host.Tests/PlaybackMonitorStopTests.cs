using System.Collections.Concurrent;
using BitcoinWallpaper.Chart.Core.Pause;
using BitcoinWallpaper.Host.Playback;

namespace BitcoinWallpaper.Host.Tests;

// Contract 22.2 (issue #23 comment from #9), criterion 3: Off from the tray stops the pause monitor, so the
// wallpaper's footprint while Off is near zero. IPlaybackMonitor gets Stop(); PlaybackMonitorTests.cs stays as it is.
//
// API SKETCH (written before the code, #237). In src/Wallpaper.Host/Playback/, namespace
// BitcoinWallpaper.Host.Playback. Additions only:
//
//   public interface IPlaybackMonitor : IDisposable
//   {
//       // ... as today ...
//
//       // Stops polling: no Sampled and no PausedChanged is raised after Stop returned, and a poll running on
//       // another thread has finished by then (like Dispose). Start() after Stop polls again. Stop before
//       // Start and a second Stop do nothing. The monitor is not disposed by Stop.
//       void Stop();
//   }
//
// Rules the tests rely on:
// - PlaybackMonitor polls only through time.CreateTimer(...) of the TimeProvider it was given (as today).
//   Stop disposes the timer; a Start after Stop may create a new timer through the same TimeProvider (the
//   tests tick the newest timer) or reuse one - either way polling resumes.
// - A timer callback that still arrives after Stop (real timers can deliver one already queued) neither
//   captures nor raises anything.
// - Start after Dispose throws ObjectDisposedException, also when Stop came before the Dispose.
public class PlaybackMonitorStopTests
{
    private static readonly PixelRect Tv = new(0, 0, 3840, 2160);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static readonly WindowInfo Editor = new("Notepad", new PixelRect(200, 200, 1800, 1200), Handle: 0x100);
    private static readonly WindowInfo Game = new("UnrealWindow", Tv, Handle: 0x200);

    private static PauseSnapshot Quiet() =>
        new(Tv, Editor, [Editor], NotificationState.AcceptsNotifications, SessionState.Active);

    private static PauseSnapshot Fullscreen() =>
        new(Tv, Game, [Game], NotificationState.AcceptsNotifications, SessionState.Active);

    // ---- fakes ----

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

        public int TimersCreated
        {
            get
            {
                lock (Timers)
                {
                    return Timers.Count;
                }
            }
        }

        /// <summary>The timer the monitor created last.</summary>
        public FakeTimer Newest
        {
            get
            {
                lock (Timers)
                {
                    return Timers[^1];
                }
            }
        }

        /// <summary>Every callback the monitor has registered, as a real timer still delivers queued ones.</summary>
        public void TickAll()
        {
            FakeTimer[] all;
            lock (Timers)
            {
                all = [.. Timers];
            }

            foreach (var timer in all)
            {
                timer.Tick();
            }
        }
    }

    private sealed class FakeTimer(TimerCallback callback, object? state) : ITimer
    {
        public bool Disposed { get; private set; }

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

    private IPlaybackMonitor NewMonitor() => new PlaybackMonitor(source, time, line => log.Enqueue(line));

    private sealed class Events
    {
        public int Sampled;
        public int Changed;
        public ConcurrentQueue<PlaybackState> States { get; } = new();

        public Events(IPlaybackMonitor monitor)
        {
            monitor.Sampled += (_, _) => Interlocked.Increment(ref Sampled);
            monitor.PausedChanged += (_, e) =>
            {
                Interlocked.Increment(ref Changed);
                States.Enqueue(e.State);
            };
        }
    }

    // ---- Stop ----

    [Fact]
    public void After_Stop_no_event_is_raised_even_when_time_advances_and_the_timer_still_ticks()
    {
        using var monitor = NewMonitor();
        var events = new Events(monitor);
        monitor.Start();
        time.Newest.Tick();
        Assert.Equal(1, events.Sampled);
        var capturesBefore = source.Captures;

        monitor.Stop();
        source.Next = Fullscreen; // would pause, if anything polled
        time.Advance(TimeSpan.FromMinutes(5));
        time.TickAll(); // a queued callback of a real timer
        time.Advance(PauseDebouncer.ResumeDelay);
        time.TickAll();

        Assert.Equal(1, events.Sampled);
        Assert.Equal(0, events.Changed);
        Assert.Equal(capturesBefore, source.Captures);
        Assert.Equal(PlaybackState.Running, monitor.State);
    }

    [Fact]
    public void Stop_ends_the_polling_timer()
    {
        using var monitor = NewMonitor();
        monitor.Start();
        var timer = time.Newest;

        monitor.Stop();

        Assert.True(timer.Disposed, "Stop left the polling timer running");
    }

    [Fact]
    public void Start_after_Stop_polls_again()
    {
        using var monitor = NewMonitor();
        var events = new Events(monitor);
        monitor.Start();
        time.Newest.Tick();
        monitor.Stop();
        time.TickAll();
        Assert.Equal(1, events.Sampled);

        monitor.Start();
        source.Next = Fullscreen;
        time.Newest.Tick();

        Assert.Equal(2, events.Sampled);
        Assert.Equal(2, source.Captures);
        Assert.Equal(PlaybackState.Paused, monitor.State);
        Assert.Equal([PlaybackState.Paused], events.States);
    }

    [Fact]
    public void Stop_and_Start_can_repeat_and_each_start_polls_once_per_tick()
    {
        using var monitor = NewMonitor();
        var events = new Events(monitor);

        for (var round = 1; round <= 3; round++)
        {
            monitor.Start();
            time.Newest.Tick();
            Assert.Equal(round, events.Sampled);
            monitor.Stop();
            time.TickAll();
            Assert.Equal(round, events.Sampled);
        }
    }

    [Fact]
    public void A_second_Start_after_Stop_and_Start_still_does_nothing()
    {
        using var monitor = NewMonitor();
        monitor.Start();
        monitor.Stop();
        monitor.Start();
        var created = time.TimersCreated;

        monitor.Start();

        Assert.Equal(created, time.TimersCreated);
    }

    [Fact]
    public void Stop_before_Start_does_nothing_and_Start_still_works()
    {
        using var monitor = NewMonitor();
        var events = new Events(monitor);

        var thrown = Record.Exception(monitor.Stop);

        Assert.Null(thrown);
        Assert.Equal(0, time.TimersCreated);
        Assert.Equal(0, events.Sampled);

        monitor.Start();
        time.Newest.Tick();
        Assert.Equal(1, events.Sampled);
    }

    [Fact]
    public void A_second_Stop_does_nothing()
    {
        using var monitor = NewMonitor();
        var events = new Events(monitor);
        monitor.Start();
        time.Newest.Tick();
        monitor.Stop();

        var thrown = Record.Exception(monitor.Stop);

        Assert.Null(thrown);
        time.TickAll();
        Assert.Equal(1, events.Sampled);

        // The monitor is still usable: Stop does not dispose it.
        monitor.Start();
        time.Newest.Tick();
        Assert.Equal(2, events.Sampled);
    }

    [Fact]
    public void Stop_does_not_dispose_the_monitor_and_Dispose_after_Stop_is_fine()
    {
        var monitor = NewMonitor();
        monitor.Start();
        monitor.Stop();

        var thrown = Record.Exception(monitor.Dispose);

        Assert.Null(thrown);
    }

    // ---- Dispose ----

    [Fact]
    public void Start_after_Dispose_still_throws_ObjectDisposedException()
    {
        var monitor = NewMonitor();
        monitor.Dispose();

        Assert.Throws<ObjectDisposedException>(monitor.Start);
    }

    [Fact]
    public void Start_after_Stop_and_Dispose_throws_ObjectDisposedException_and_polls_nothing()
    {
        var monitor = NewMonitor();
        var events = new Events(monitor);
        monitor.Start();
        monitor.Stop();
        monitor.Dispose();

        Assert.Throws<ObjectDisposedException>(monitor.Start);

        time.TickAll();
        Assert.Equal(0, events.Sampled);
        Assert.Equal(0, source.Captures);
    }

    // ---- a poll on another thread ----

    [Fact]
    public void Stop_waits_for_a_poll_in_progress()
    {
        using var monitor = NewMonitor();
        monitor.Start();
        using var release = new ManualResetEventSlim();
        source.Release = release;
        source.Next = Fullscreen;

        var timer = time.Newest;
        var poll = new Background(timer.Tick);
        Assert.True(source.Entered.Wait(Wait), "the poll never reached Capture");

        var stop = new Background(monitor.Stop);
        var returnedEarly = stop.Join(TimeSpan.FromMilliseconds(500));
        release.Set();
        Assert.True(poll.Join(Wait), "the poll did not finish after Capture was released");
        Assert.Null(poll.Error);
        Assert.True(stop.Join(Wait), "Stop did not return after the poll finished");
        Assert.Null(stop.Error);
        Assert.False(returnedEarly, "Stop returned while a poll was still inside Capture");
    }

    [Fact]
    public void No_event_is_raised_after_Stop_returned_when_a_poll_was_running()
    {
        using var monitor = NewMonitor();
        var stopReturned = 0;
        var late = new ConcurrentQueue<string>();
        monitor.Sampled += (_, _) =>
        {
            if (Volatile.Read(ref stopReturned) == 1)
            {
                late.Enqueue("Sampled");
            }
        };
        monitor.PausedChanged += (_, _) =>
        {
            if (Volatile.Read(ref stopReturned) == 1)
            {
                late.Enqueue("PausedChanged");
            }
        };
        monitor.Start();
        var timer = time.Newest;

        using var release = new ManualResetEventSlim();
        source.Release = release;
        source.Next = Fullscreen;
        var poll = new Background(timer.Tick);
        Assert.True(source.Entered.Wait(Wait), "the poll never reached Capture");
        var stop = new Background(() =>
        {
            monitor.Stop();
            Volatile.Write(ref stopReturned, 1);
        });
        stop.Join(TimeSpan.FromMilliseconds(500));
        release.Set();
        Assert.True(poll.Join(Wait), "the poll did not finish after Capture was released");
        Assert.Null(poll.Error);
        Assert.True(stop.Join(Wait), "Stop did not return");

        source.Release = null;
        var capturesBefore = source.Captures;
        time.TickAll();

        Assert.Empty(late);
        Assert.Equal(capturesBefore, source.Captures);
    }
}
