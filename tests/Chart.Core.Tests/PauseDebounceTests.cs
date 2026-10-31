using BitcoinWallpaper.Chart.Core.Pause;

namespace BitcoinWallpaper.Chart.Core.Tests;

// Contract #7, criterion 2: pause and resume are debounced (resume only after 1 s of Running; pause at
// once) so alt-tab flicker does not thrash.
//
// API SKETCH (written before the code, #237). In src/Chart.Core/Pause/, namespace
// BitcoinWallpaper.Chart.Core.Pause, next to PlaybackState (see PauseDecisionTests.cs):
//
//   public sealed class PauseDebouncer
//   {
//       public static TimeSpan ResumeDelay { get; }          // 1 second
//       public PauseDebouncer(TimeProvider time);
//       public PlaybackState State { get; }                  // Running before the first observation
//       public PlaybackState Update(PlaybackState observed); // feeds one poll result, returns State
//   }
//
// Rules the tests rely on:
// - Time comes only from the TimeProvider given to the constructor (GetUtcNow or GetTimestamp, both are
//   faked consistently here); no timers: State changes only inside Update.
// - Update(Paused) makes State Paused at once.
// - While State is Paused, the first Update(Running) starts a Running streak; State becomes Running at the
//   first Update(Running) made at least ResumeDelay (inclusive) after the streak's first observation.
//   Any Update(Paused) ends the streak; the next Running observation starts a new one.
// - Update(Running) while State is Running keeps it Running.
public class PauseDebounceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);

    /// <summary>A manual clock: time moves only when the test sets it.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => now.UtcTicks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new NotSupportedException("The debouncer must not use timers: State changes only inside Update.");

        public void At(int milliseconds)
        {
            var next = Start.AddMilliseconds(milliseconds);
            if (next < now)
            {
                throw new InvalidOperationException("The test clock only moves forward.");
            }

            now = next;
        }
    }

    private readonly ManualClock clock = new(Start);
    private readonly PauseDebouncer debouncer;

    public PauseDebounceTests() => debouncer = new PauseDebouncer(clock);

    private const PlaybackState Running = PlaybackState.Running;
    private const PlaybackState Paused = PlaybackState.Paused;

    /// <summary>Feeds <paramref name="observed"/> at <paramref name="ms"/> after Start and checks the result.</summary>
    private void Observe(int ms, PlaybackState observed, PlaybackState expected)
    {
        clock.At(ms);
        var returned = debouncer.Update(observed);
        Assert.True(expected == returned, $"at +{ms} ms, observed {observed}: expected {expected}, Update returned {returned}");
        Assert.True(expected == debouncer.State, $"at +{ms} ms, observed {observed}: expected {expected}, State is {debouncer.State}");
    }

    [Fact]
    public void Resume_delay_is_one_second()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), PauseDebouncer.ResumeDelay);
    }

    [Fact]
    public void Starts_running()
    {
        Assert.Equal(Running, debouncer.State);
    }

    [Fact]
    public void Running_observations_keep_it_running()
    {
        Observe(0, Running, Running);
        Observe(500, Running, Running);
        Observe(1000, Running, Running);
    }

    [Fact]
    public void First_observation_paused_pauses_at_once()
    {
        Observe(0, Paused, Paused);
    }

    [Fact]
    public void Pause_applies_at_once_after_a_long_run()
    {
        Observe(0, Running, Running);
        Observe(60_000, Running, Running);
        Observe(60_500, Paused, Paused);
    }

    [Fact]
    public void Resume_waits_one_second_from_the_first_running_observation()
    {
        Observe(0, Paused, Paused);
        Observe(500, Running, Paused);   // streak starts at +500
        Observe(1000, Running, Paused);  // 1 s after the last Paused poll, but only 0.5 s of Running
        Observe(1499, Running, Paused);  // 999 ms of Running
        Observe(1500, Running, Running); // exactly 1 s of Running
        Observe(2000, Running, Running);
    }

    [Fact]
    public void A_long_pause_does_not_shorten_the_resume_delay()
    {
        Observe(0, Paused, Paused);
        Observe(10_000, Paused, Paused);
        Observe(10_000, Running, Paused); // streak starts at +10 000
        Observe(10_999, Running, Paused);
        Observe(11_000, Running, Running);
    }

    [Fact]
    public void Alt_tab_flicker_within_a_second_stays_paused()
    {
        Observe(0, Paused, Paused);
        Observe(200, Running, Paused);  // streak starts at +200
        Observe(400, Paused, Paused);   // streak broken
        Observe(600, Running, Paused);  // new streak starts at +600
        Observe(1300, Running, Paused); // 1.1 s after the first Running, but only 0.7 s of this streak
        Observe(1599, Running, Paused);
        Observe(1600, Running, Running);
    }

    [Fact]
    public void Repeated_flicker_never_resumes()
    {
        Observe(0, Paused, Paused);
        for (var ms = 250; ms <= 10_000; ms += 250)
        {
            // Three Running polls (500 ms of Running), then one Paused poll, over and over.
            var observed = ms % 1000 == 0 ? Paused : Running;
            Observe(ms, observed, Paused);
        }
    }

    [Fact]
    public void A_new_pause_after_resuming_needs_a_fresh_second_of_running()
    {
        Observe(0, Paused, Paused);
        Observe(500, Running, Paused);
        Observe(1500, Running, Running);
        Observe(2000, Paused, Paused);  // pauses at once
        Observe(2100, Running, Paused); // new streak starts at +2100
        Observe(3000, Running, Paused); // 0.9 s
        Observe(3100, Running, Running);
    }

    [Fact]
    public void Update_at_the_same_instant_as_the_streak_start_does_not_resume()
    {
        Observe(0, Paused, Paused);
        Observe(0, Running, Paused);
        Observe(0, Running, Paused);
    }
}
