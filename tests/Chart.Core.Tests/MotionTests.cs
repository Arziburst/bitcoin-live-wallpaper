using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Motion;

namespace BitcoinWallpaper.Chart.Core.Tests;

// Contract #8, criterion 1: an animation model in src/Chart.Core/Motion (pure, TimeProvider-driven) tweens the
// forming candle from its previous OHLC to the new one (300 ms ease-out), shifts the chart one pitch on a new
// bar (500 ms), pulses the price colour (250 ms, at most once per second), and reports "idle" when no tween is
// active; frame pacing asks for one frame per display refresh only while a tween runs (D-10).
//
// API SKETCH (written before the code, #237). In src/Chart.Core/Motion/, namespace
// BitcoinWallpaper.Chart.Core.Motion:
//
//   public sealed class ChartMotion
//   {
//       public static TimeSpan FormingDuration { get; }   // 300 ms, ease-out
//       public static TimeSpan ShiftDuration { get; }     // 500 ms
//       public static TimeSpan PulseDuration { get; }     // 250 ms
//       public static TimeSpan PulseMinInterval { get; }  // 1 s
//
//       public ChartMotion(TimeProvider time);
//
//       // The series' latest candle (forming or just closed) as the feed has it now.
//       public void Apply(Candle latest);
//
//       // Everything below is evaluated at time.GetUtcNow(); reading it has no side effects.
//       public Candle? Forming { get; }      // the latest candle as shown now; null before the first Apply
//       public double ShiftPitches { get; }  // horizontal offset of the whole series, in candle pitches
//       public double Pulse { get; }         // price colour pulse strength, 0..1
//       public bool PulseUp { get; }         // direction of the close change that started the latest pulse
//       public bool IsIdle { get; }          // true when no tween is active
//       public bool NeedsFrame { get; }      // the host should present a frame at this display refresh
//       public void FrameTaken();            // the host drew a frame at time.GetUtcNow()
//   }
//
// Rules the tests rely on:
// - Time comes only from the TimeProvider (GetUtcNow or GetTimestamp, faked consistently here); no timers.
// - The first Apply shows its candle at once: no tween, no shift, no pulse.
// - Apply with the same OpenTime as the current candle and a different OHLC starts a forming tween of
//   FormingDuration: at its start Forming shows exactly the OHLC shown just before the Apply (no jump, also
//   when a tween was still running: it restarts from the current interpolated values), then moves each of
//   Open/High/Low/Close monotonically towards the new values along an ease-out curve (always at or ahead of
//   linear progress), and shows exactly the new values from start + FormingDuration on. Forming keeps the
//   new candle's OpenTime and IsClosed. Apply with a candle equal to the current target starts nothing.
// - Apply with a later OpenTime (a new bar) shows the new candle at once (no OHLC tween across bars) and
//   starts a shift of ShiftDuration: ShiftPitches is 1.0 at its start, non-increasing, exactly 0 from
//   start + ShiftDuration on; 0 when no shift runs.
// - A close different from the previously applied close starts a pulse if no pulse started within the last
//   PulseMinInterval (a change at exactly PulseMinInterval after the last pulse's start does start one); a
//   change within that interval does not start or restart a pulse. Pulse is 1.0 at the start, non-increasing,
//   exactly 0 from start + PulseDuration on; 0 when no pulse runs. PulseUp: new close > previous close.
// - IsIdle is true when now >= the end of every tween (forming, shift, pulse), including before the first
//   Apply.
// - NeedsFrame is true while a tween is active, and after the last one ends until FrameTaken() is called
//   at or after that end (so the frame with the exact end values is drawn); false otherwise. The host
//   presents one frame per display refresh while NeedsFrame is true and none otherwise.
public class MotionTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BarA = new(2026, 10, 31, 19, 55, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BarB = BarA.AddMinutes(5);

    private readonly ManualClock clock = new(Start);

    /// <summary>A manual clock: time moves only when the test sets it.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => now.UtcTicks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new NotSupportedException("The motion model must not use timers: it is evaluated at the clock's now.");

        public void AtMs(double milliseconds) => AtTicks((long)Math.Round(milliseconds * TimeSpan.TicksPerMillisecond));

        public void AtTicks(long ticksSinceStart)
        {
            var next = Start.AddTicks(ticksSinceStart);
            if (next < now)
            {
                throw new InvalidOperationException("The test clock only moves forward.");
            }

            now = next;
        }
    }

    private static Candle Bar(DateTimeOffset openTime, decimal o, decimal h, decimal l, decimal c) =>
        new(openTime, o, h, l, c, Volume: 12.5m, IsClosed: false);

    private static decimal[] Ohlc(Candle c) => [c.Open, c.High, c.Low, c.Close];

    private static Candle FormingOf(ChartMotion motion) =>
        motion.Forming ?? throw new Xunit.Sdk.XunitException("Forming is null after Apply.");

    [Fact]
    public void Durations_follow_the_motion_rules()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(300), ChartMotion.FormingDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(500), ChartMotion.ShiftDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(250), ChartMotion.PulseDuration);
        Assert.Equal(TimeSpan.FromSeconds(1), ChartMotion.PulseMinInterval);
    }

    [Fact]
    public void Before_any_data_the_model_is_idle_and_asks_for_no_frames()
    {
        var motion = new ChartMotion(clock);

        Assert.Null(motion.Forming);
        Assert.True(motion.IsIdle);
        Assert.False(motion.NeedsFrame);
        Assert.Equal(0.0, motion.ShiftPitches);
        Assert.Equal(0.0, motion.Pulse);
    }

    [Fact]
    public void The_first_candle_is_shown_at_once_without_any_tween()
    {
        var motion = new ChartMotion(clock);
        var first = Bar(BarA, 100m, 110m, 90m, 105m);

        motion.Apply(first);

        Assert.Equal(first, motion.Forming);
        Assert.True(motion.IsIdle);
        Assert.Equal(0.0, motion.ShiftPitches);
        Assert.Equal(0.0, motion.Pulse);
    }

    [Fact]
    public void Forming_candle_tweens_from_the_previous_ohlc_to_the_new_one_in_300_ms()
    {
        var motion = new ChartMotion(clock);
        var previous = Bar(BarA, 100m, 110m, 90m, 105m);
        var next = Bar(BarA, 100m, 120m, 85m, 118m);
        motion.Apply(previous);
        clock.AtMs(1000);

        motion.Apply(next);

        // At the start: exactly the previous values (no jump).
        Assert.Equal(Ohlc(previous), Ohlc(FormingOf(motion)));
        Assert.False(motion.IsIdle);

        // Midway: strictly between, for every value that changes.
        clock.AtMs(1150);
        var mid = FormingOf(motion);
        Assert.InRange(mid.High, 110.0001m, 119.9999m);
        Assert.InRange(mid.Low, 85.0001m, 89.9999m);
        Assert.InRange(mid.Close, 105.0001m, 117.9999m);
        Assert.Equal(100m, mid.Open);
        Assert.Equal(BarA, mid.OpenTime);
        Assert.False(mid.IsClosed);
        Assert.False(motion.IsIdle);

        // At the end: exactly the new values, and idle.
        clock.AtMs(1300);
        Assert.Equal(Ohlc(next), Ohlc(FormingOf(motion)));
        Assert.Equal(BarA, FormingOf(motion).OpenTime);
        Assert.True(motion.IsIdle);

        clock.AtMs(5000);
        Assert.Equal(Ohlc(next), Ohlc(FormingOf(motion)));
        Assert.True(motion.IsIdle);
    }

    [Fact]
    public void Forming_tween_is_monotone_and_eases_out()
    {
        var motion = new ChartMotion(clock);
        var previous = Bar(BarA, 100m, 110m, 90m, 105m);
        var next = Bar(BarA, 100m, 120m, 85m, 118m);
        motion.Apply(previous);
        motion.Apply(next);

        var last = Ohlc(previous);
        for (var ms = 0; ms <= 300; ms++)
        {
            clock.AtMs(ms);
            var now = Ohlc(FormingOf(motion));
            for (var i = 0; i < 4; i++)
            {
                var direction = Math.Sign(Ohlc(next)[i] - Ohlc(previous)[i]);
                Assert.True((now[i] - last[i]) * direction >= 0m,
                    $"value {i} moved away from its target at {ms} ms: {last[i]} -> {now[i]}");
            }

            // Ease-out: never behind linear progress.
            var progress = (double)((now[3] - 105m) / (118m - 105m));
            Assert.True(progress >= (ms / 300.0) - 1e-9, $"close progress {progress:F4} is behind linear at {ms} ms");
            last = now;
        }
    }

    [Fact]
    public void Forming_tween_is_ahead_of_linear_at_its_midpoint()
    {
        var motion = new ChartMotion(clock);
        motion.Apply(Bar(BarA, 100m, 110m, 90m, 100m));
        motion.Apply(Bar(BarA, 100m, 110m, 90m, 110m));

        clock.AtMs(150);
        var progress = (double)((FormingOf(motion).Close - 100m) / 10m);

        Assert.True(progress > 0.55, $"ease-out should be well ahead of linear at the midpoint, got {progress:F4}");
        Assert.True(progress < 1.0, $"the tween ended early, got {progress:F4}");
    }

    [Fact]
    public void A_new_update_mid_tween_restarts_from_the_current_value_without_a_jump()
    {
        var motion = new ChartMotion(clock);
        motion.Apply(Bar(BarA, 100m, 110m, 90m, 100m));
        clock.AtMs(1000);
        motion.Apply(Bar(BarA, 100m, 120m, 90m, 120m));

        clock.AtMs(1100);
        var shown = Ohlc(FormingOf(motion));
        var target = Bar(BarA, 100m, 130m, 90m, 130m);
        motion.Apply(target);

        // Same instant: the picture does not change.
        Assert.Equal(shown, Ohlc(FormingOf(motion)));

        // The old tween would have ended at 1300 ms; the restarted one runs until 1400 ms.
        var lastClose = shown[3];
        for (var ms = 1101; ms < 1400; ms++)
        {
            clock.AtMs(ms);
            var close = FormingOf(motion).Close;
            Assert.True(close >= lastClose, $"close went back at {ms} ms: {lastClose} -> {close}");
            Assert.True(close < 130m, $"the restarted tween reached its target early, at {ms} ms");
            Assert.False(motion.IsIdle, $"idle during the restarted tween at {ms} ms");
            lastClose = close;
        }

        clock.AtMs(1400);
        Assert.Equal(Ohlc(target), Ohlc(FormingOf(motion)));
        Assert.True(motion.IsIdle);
    }

    [Fact]
    public void An_unchanged_candle_starts_nothing()
    {
        var motion = new ChartMotion(clock);
        var candle = Bar(BarA, 100m, 110m, 90m, 105m);
        motion.Apply(candle);
        clock.AtMs(1000);
        motion.FrameTaken();

        motion.Apply(candle with { });

        Assert.True(motion.IsIdle);
        Assert.False(motion.NeedsFrame);
        Assert.Equal(0.0, motion.Pulse);
        Assert.Equal(candle, motion.Forming);
    }

    [Fact]
    public void A_new_bar_shifts_the_chart_one_pitch_over_500_ms()
    {
        var motion = new ChartMotion(clock);
        motion.Apply(Bar(BarA, 100m, 110m, 90m, 105m));
        clock.AtMs(1000);
        var newBar = Bar(BarB, 105m, 105m, 105m, 105m);

        motion.Apply(newBar);

        // The new bar is shown at once, not tweened from the previous bar's OHLC.
        Assert.Equal(newBar, motion.Forming);
        Assert.Equal(1.0, motion.ShiftPitches);
        Assert.False(motion.IsIdle);

        var last = 1.0;
        for (var ms = 1001; ms < 1500; ms++)
        {
            clock.AtMs(ms);
            var shift = motion.ShiftPitches;
            Assert.True(shift <= last, $"shift went back at {ms} ms: {last} -> {shift}");
            Assert.True(shift > 0.0, $"shift ended early at {ms} ms");
            Assert.False(motion.IsIdle, $"idle during the shift at {ms} ms");
            last = shift;
        }

        clock.AtMs(1500);
        Assert.Equal(0.0, motion.ShiftPitches);
        Assert.True(motion.IsIdle);
    }

    [Fact]
    public void Shift_is_strictly_between_its_ends_halfway()
    {
        var motion = new ChartMotion(clock);
        motion.Apply(Bar(BarA, 100m, 110m, 90m, 105m));
        motion.Apply(Bar(BarB, 105m, 105m, 105m, 105m));

        clock.AtMs(250);

        Assert.InRange(motion.ShiftPitches, 0.001, 0.999);
    }

    [Fact]
    public void A_price_change_pulses_the_colour_for_250_ms()
    {
        var motion = new ChartMotion(clock);
        motion.Apply(Bar(BarA, 100m, 110m, 90m, 105m));
        clock.AtMs(1000);

        motion.Apply(Bar(BarA, 100m, 110m, 90m, 106m));

        Assert.Equal(1.0, motion.Pulse);
        Assert.True(motion.PulseUp);

        var last = 1.0;
        for (var ms = 1001; ms < 1250; ms++)
        {
            clock.AtMs(ms);
            Assert.True(motion.Pulse <= last, $"pulse grew at {ms} ms");
            Assert.True(motion.Pulse > 0.0, $"pulse ended early at {ms} ms");
            last = motion.Pulse;
        }

        clock.AtMs(1250);
        Assert.Equal(0.0, motion.Pulse);
    }

    [Fact]
    public void A_falling_price_pulses_down()
    {
        var motion = new ChartMotion(clock);
        motion.Apply(Bar(BarA, 100m, 110m, 90m, 105m));
        clock.AtMs(1000);

        motion.Apply(Bar(BarA, 100m, 110m, 90m, 104m));

        Assert.Equal(1.0, motion.Pulse);
        Assert.False(motion.PulseUp);
    }

    [Fact]
    public void A_second_change_within_one_second_does_not_restart_the_pulse()
    {
        var motion = new ChartMotion(clock);
        var reference = new ChartMotion(clock);
        motion.Apply(Bar(BarA, 100m, 110m, 90m, 105m));
        reference.Apply(Bar(BarA, 100m, 110m, 90m, 105m));
        clock.AtMs(1000);
        motion.Apply(Bar(BarA, 100m, 110m, 90m, 106m));
        reference.Apply(Bar(BarA, 100m, 110m, 90m, 106m));

        // 100 ms later the price changes again: the running pulse goes on as if nothing happened.
        clock.AtMs(1100);
        motion.Apply(Bar(BarA, 100m, 110m, 90m, 107m));
        Assert.Equal(reference.Pulse, motion.Pulse);
        clock.AtMs(1200);
        Assert.Equal(reference.Pulse, motion.Pulse);
        clock.AtMs(1250);
        Assert.Equal(0.0, motion.Pulse);

        // 600 ms after the pulse started: still within the second, no new pulse.
        clock.AtMs(1600);
        motion.Apply(Bar(BarA, 100m, 110m, 90m, 108m));
        Assert.Equal(0.0, motion.Pulse);
        clock.AtMs(1700);
        Assert.Equal(0.0, motion.Pulse);
    }

    [Fact]
    public void A_change_one_second_after_the_last_pulse_starts_a_new_one()
    {
        var motion = new ChartMotion(clock);
        motion.Apply(Bar(BarA, 100m, 110m, 90m, 105m));
        clock.AtMs(1000);
        motion.Apply(Bar(BarA, 100m, 110m, 90m, 106m));
        clock.AtMs(1600);
        motion.Apply(Bar(BarA, 100m, 110m, 90m, 107m)); // within the second: no pulse

        clock.AtMs(2000);
        motion.Apply(Bar(BarA, 100m, 110m, 90m, 104m));

        Assert.Equal(1.0, motion.Pulse);
        Assert.False(motion.PulseUp);
        clock.AtMs(2250);
        Assert.Equal(0.0, motion.Pulse);
    }

    [Fact]
    public void Idle_only_when_no_tween_runs()
    {
        var motion = new ChartMotion(clock);
        motion.Apply(Bar(BarA, 100m, 110m, 90m, 105m));
        Assert.True(motion.IsIdle);

        clock.AtMs(1000);
        motion.Apply(Bar(BarB, 105m, 106m, 105m, 106m)); // new bar: shift 500 ms and a pulse 250 ms

        clock.AtMs(1299);
        Assert.False(motion.IsIdle); // the pulse ended, the shift still runs
        clock.AtMs(1499);
        Assert.False(motion.IsIdle);
        clock.AtMs(1500);
        Assert.True(motion.IsIdle);
    }

    [Fact]
    public void Needs_frames_while_a_tween_runs_then_one_more_for_the_end_values_then_none()
    {
        var motion = new ChartMotion(clock);
        motion.Apply(Bar(BarA, 100m, 110m, 90m, 105m));
        clock.AtMs(1000);
        motion.FrameTaken();
        Assert.False(motion.NeedsFrame);

        var target = Bar(BarA, 100m, 115m, 90m, 112m);
        motion.Apply(target);
        Assert.True(motion.NeedsFrame);
        motion.FrameTaken();
        Assert.True(motion.NeedsFrame); // the tween still runs

        clock.AtMs(1299);
        Assert.True(motion.NeedsFrame);
        motion.FrameTaken();

        // The tween has ended but the frame with its exact end values has not been drawn yet.
        clock.AtMs(1301);
        Assert.True(motion.IsIdle);
        Assert.True(motion.NeedsFrame);
        Assert.Equal(Ohlc(target), Ohlc(FormingOf(motion)));
        motion.FrameTaken();
        Assert.False(motion.NeedsFrame);

        clock.AtMs(3000);
        Assert.False(motion.NeedsFrame);
    }

    // Updates every 2 s; the display refreshes at hz. The host presents at a refresh only when NeedsFrame.
    // Expected frames per update: duration x hz (+ the end-values frame, + 1 of rounding), none in between.
    [Theory]
    [InlineData(120)]
    [InlineData(60)]
    public void Frames_come_once_per_refresh_only_while_a_tween_runs(int hz)
    {
        var updates = new (Candle Candle, double ExpectedSeconds)[]
        {
            (Bar(BarA, 100m, 101m, 99m, 100m), double.NaN),   // first data: not counted
            (Bar(BarA, 100m, 102m, 99m, 101.5m), 0.3),        // forming tween + pulse
            (Bar(BarA, 100m, 102m, 98m, 99m), 0.3),
            (Bar(BarA, 100m, 102m, 98m, 99m), 0.0),           // nothing changed: nothing drawn
            (Bar(BarA, 100m, 103m, 98m, 102.5m), 0.3),
            (Bar(BarB, 102.5m, 102.7m, 102.5m, 102.7m), 0.5), // new bar: shift + pulse
            (Bar(BarB, 102.5m, 103m, 102.4m, 102.9m), 0.3),
            (Bar(BarB, 102.5m, 103m, 102.1m, 102.2m), 0.3),
            (Bar(BarB, 102.5m, 103.4m, 102.1m, 103.3m), 0.3),
            (Bar(BarB, 102.5m, 103.4m, 102.1m, 103.3m), 0.0),
        };

        var motion = new ChartMotion(clock);
        var refreshesPerUpdate = 2 * hz;
        var frames = new int[updates.Length];
        var late = new int[updates.Length];
        Candle? lastDrawn = null;

        for (long k = 0; k < (long)updates.Length * refreshesPerUpdate; k++)
        {
            clock.AtTicks(k * TimeSpan.TicksPerSecond / hz);
            var update = (int)(k / refreshesPerUpdate);
            var offset = k % refreshesPerUpdate;
            if (offset == 0)
            {
                motion.Apply(updates[update].Candle);
            }

            if (motion.NeedsFrame)
            {
                lastDrawn = motion.Forming;
                motion.FrameTaken();
                frames[update]++;
                if (offset > (0.5 * hz) + 2)
                {
                    late[update]++;
                }
            }
        }

        for (var i = 1; i < updates.Length; i++)
        {
            var expected = updates[i].ExpectedSeconds * hz;
            Assert.True(frames[i] >= expected && frames[i] <= expected + 2,
                $"update {i} at {hz} Hz: {frames[i]} frames, expected {expected} to {expected + 2}");
            Assert.True(late[i] == 0, $"update {i} at {hz} Hz: {late[i]} frames drawn while nothing moved");
        }

        Assert.Equal(updates[^1].Candle, lastDrawn);
    }

    [Fact]
    public void Ten_quiet_seconds_ask_for_no_frame()
    {
        var motion = new ChartMotion(clock);
        motion.Apply(Bar(BarA, 100m, 110m, 90m, 105m));
        motion.FrameTaken();

        for (long k = 1; k <= 10 * 120; k++)
        {
            clock.AtTicks(k * TimeSpan.TicksPerSecond / 120);
            Assert.False(motion.NeedsFrame, $"a frame was asked for at refresh {k} with nothing moving");
        }
    }
}
