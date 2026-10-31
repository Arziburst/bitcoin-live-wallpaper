using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Motion;
using BitcoinWallpaper.Chart.Core.Pause;

namespace BitcoinWallpaper.Chart.Core.Tests;

// Contract 22.2 (issue #23), criterion 2: tween coalescing. The Kraken feed sends an update per trade and the
// host polls the data client every 250 ms; each changed forming candle started a 300 ms tween, so the chart
// presented at 120 Hz around the clock (D-6). From now on at most ONE forming-candle tween starts per second
// on any feed. LiveSceneTests.cs and MotionTests.cs stay as they are (they must stay green): ChartMotion.Apply
// does not change, the coalescing lives in LiveScene or a new type in BitcoinWallpaper.Chart.Core.Motion that
// LiveScene uses.
//
// API SKETCH (written before the code, #237). NO new public member is needed: the tests observe only the
// public behaviour of LiveScene - NeedsFrame, TakeFrame (the forming candle's values over time, ShiftPitches,
// Ticker) and the host's RequestFrame / SetPaused calls - on a manual clock. Behaviour, with a "tween start"
// meaning the moment the forming candle of the frames starts moving from the OHLC shown towards a new target
// (ChartMotion.Apply, 300 ms, ease-out, unchanged):
//
// - Window: once a forming tween has started at time T, no further forming tween starts before T + 1 s (the
//   1 s is a public constant on whatever type holds the rule, e.g. ChartMotion.PulseMinInterval or a new one;
//   the tests do not name it). A tween that starts at exactly T + 1 s or later is fine.
// - OnData with a snapshot whose last candle has the SAME OpenTime as the shown one and different OHLC:
//     * no tween started in the last second: the tween starts at once, in that OnData call (first update
//       after a quiet second). The very first snapshot ever is shown at once and starts no tween (as today).
//     * a tween started less than a second ago: the update is HELD, the forming candle keeps showing what it
//       showed (its current tween goes on to its target); a newer held value replaces the older one.
// - The held value is released by an OnData call at or after T + 1 s - ALSO when that call brings the same
//   snapshot as the call before (the host polls every 250 ms with the data client's current snapshot, so the
//   scene must remember the held value; "unchanged snapshot" must not mean "nothing held"). The LAST held
//   value starts the tween (from what is shown then), and a frame is requested (host.RequestFrame, started
//   and not paused) so that NeedsFrame is true in that call.
// - Releasing starts a new window: the next change is held again until 1 s after the release.
// - A new bar (OpenTime greater than the shown one) is not delayed: shown at once, ShiftPitches 1.0 at once
//   (500 ms shift as today); a value still held for the older bar is dropped.
// - Ticker (headline) changes are not coalesced: a ticker change requests a frame at once and TakeFrame shows
//   the new ticker at once, held candle or not.
// - Paused: as today - data is applied, no RequestFrame, NeedsFrame false; a held value is released by an
//   OnData after the second as above, without frames; the resume gives ["resume", "frame"] and the next
//   TakeFrame shows the latest data.
public class TweenCoalescingTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BarA = new(2026, 10, 31, 19, 50, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BarB = BarA.AddMinutes(5);
    private static readonly DateTimeOffset BarC = BarB.AddMinutes(5);

    private readonly ManualClock clock = new(Start);
    private readonly RecordingHost host = new();

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => now.UtcTicks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new NotSupportedException("The live scene must not use timers: the host drives it.");

        public void AtMs(double milliseconds)
        {
            var next = Start.AddTicks((long)Math.Round(milliseconds * TimeSpan.TicksPerMillisecond));
            if (next < now)
            {
                throw new InvalidOperationException("The test clock only moves forward.");
            }

            now = next;
        }
    }

    private sealed class RecordingHost : IFrameHost
    {
        public List<string> Calls { get; } = [];

        public void RequestFrame() => Calls.Add("frame");

        public void SetPaused(bool paused) => Calls.Add(paused ? "pause" : "resume");

        public List<string> Since(int index) => Calls.Skip(index).ToList();
    }

    private static Candle Bar(DateTimeOffset openTime, decimal close, bool closed = false) =>
        new(openTime, Open: 100m, High: Math.Max(close, 100m), Low: Math.Min(close, 100m), Close: close, Volume: 3m,
            IsClosed: closed);

    private static Ticker24h Ticker(decimal last) => new(last, 100m, 120m, 80m, last - 100m, last - 100m, 1000m);

    private static readonly Candle ClosedA = Bar(BarA, 100m, closed: true);

    /// <summary>The snapshot the data client holds while bar B forms with the given close.</summary>
    private static List<Candle> Forming(decimal close) => [ClosedA, Bar(BarB, close)];

    private LiveScene Started()
    {
        var scene = new LiveScene(clock, host);
        scene.OnHostStarted();
        return scene;
    }

    private decimal CloseAt(LiveScene scene, double ms)
    {
        clock.AtMs(ms);
        return scene.TakeFrame().Candles[^1].Close;
    }

    /// <summary>Bar B forms at 100; at 0 ms the first snapshot is shown, nothing tweens afterwards.</summary>
    private LiveScene StartedAtHundred()
    {
        var scene = Started();
        clock.AtMs(0);
        scene.OnData(Forming(100m), Ticker(100m));
        scene.TakeFrame();
        return scene;
    }

    // ---- the first update after a quiet second tweens at once ----

    [Fact]
    public void The_first_update_after_a_quiet_second_tweens_at_once()
    {
        var scene = StartedAtHundred();

        clock.AtMs(2000);
        var before = host.Calls.Count;
        scene.OnData(Forming(110m), Ticker(110m));

        Assert.Equal(["frame"], host.Since(before));
        Assert.True(scene.NeedsFrame);
        Assert.Equal(100m, CloseAt(scene, 2000)); // starts from what was shown
        var mid = CloseAt(scene, 2060);
        Assert.InRange(mid, 100.001m, 109.999m);
        Assert.Equal(110m, CloseAt(scene, 2310));
    }

    [Fact]
    public void An_update_a_second_and_more_after_the_tween_started_tweens_at_once()
    {
        var scene = StartedAtHundred();
        clock.AtMs(2000);
        scene.OnData(Forming(101m), Ticker(101m));
        Assert.Equal(101m, CloseAt(scene, 2400));

        clock.AtMs(3200);
        scene.OnData(Forming(105m), Ticker(105m));

        Assert.Equal(101m, CloseAt(scene, 3200));
        Assert.InRange(CloseAt(scene, 3260), 101.001m, 104.999m);
        Assert.Equal(105m, CloseAt(scene, 3510));
    }

    // ---- an update inside the second is held; the last held value starts the next tween ----

    [Fact]
    public void An_update_inside_the_second_is_held_and_nothing_tweens_towards_it()
    {
        var scene = StartedAtHundred();
        clock.AtMs(2000);
        scene.OnData(Forming(101m), Ticker(101m));
        Assert.Equal(101m, CloseAt(scene, 2400));

        clock.AtMs(2400);
        scene.OnData(Forming(110m), Ticker(110m));

        Assert.Equal(101m, CloseAt(scene, 2400));
        Assert.Equal(101m, CloseAt(scene, 2460));
        Assert.Equal(101m, CloseAt(scene, 2700));
        Assert.False(scene.NeedsFrame, "a held update must not keep the chart presenting");
    }

    [Fact]
    public void The_last_held_value_starts_the_next_tween_when_the_host_polls_the_same_snapshot_again()
    {
        var scene = StartedAtHundred();
        clock.AtMs(2000);
        scene.OnData(Forming(101m), Ticker(101m));
        Assert.Equal(101m, CloseAt(scene, 2400));

        // Three trades inside the second: 102, 103, 104. Only the last one counts.
        clock.AtMs(2400);
        scene.OnData(Forming(102m), Ticker(102m));
        clock.AtMs(2650);
        scene.OnData(Forming(103m), Ticker(103m));
        clock.AtMs(2900);
        scene.OnData(Forming(104m), Ticker(104m));
        Assert.Equal(101m, CloseAt(scene, 2910));

        // The data client has nothing newer: the host polls with the same snapshot every 250 ms.
        clock.AtMs(2999);
        scene.OnData(Forming(104m), Ticker(104m));
        Assert.Equal(101m, CloseAt(scene, 2999)); // 1 ms short of the second: still held
        Assert.False(scene.NeedsFrame);

        clock.AtMs(3001);
        var before = host.Calls.Count;
        scene.OnData(Forming(104m), Ticker(104m));

        Assert.Equal(["frame"], host.Since(before));
        Assert.True(scene.NeedsFrame);
        Assert.Equal(101m, CloseAt(scene, 3001)); // the tween starts from what is shown
        var mid = CloseAt(scene, 3061);
        Assert.InRange(mid, 101.001m, 103.999m);
        Assert.True(mid > 102m, $"the tween goes towards the LAST held value (104), not an earlier one: {mid}");
        Assert.Equal(104m, CloseAt(scene, 3320));
        Assert.False(scene.NeedsFrame);
    }

    [Fact]
    public void A_release_starts_a_new_window_and_the_next_update_is_held_again()
    {
        var scene = StartedAtHundred();
        clock.AtMs(2000);
        scene.OnData(Forming(101m), Ticker(101m));
        clock.AtMs(2500);
        scene.OnData(Forming(104m), Ticker(104m));
        clock.AtMs(3001);
        scene.OnData(Forming(104m), Ticker(104m)); // the release: a tween to 104 starts at 3001
        Assert.Equal(104m, CloseAt(scene, 3320));

        clock.AtMs(3500);
        scene.OnData(Forming(108m), Ticker(108m));
        clock.AtMs(4000); // 999 ms after the release
        scene.OnData(Forming(108m), Ticker(108m));
        Assert.Equal(104m, CloseAt(scene, 4000));
        Assert.False(scene.NeedsFrame);

        clock.AtMs(4002);
        scene.OnData(Forming(108m), Ticker(108m));
        Assert.True(scene.NeedsFrame);
        Assert.Equal(104m, CloseAt(scene, 4002));
        Assert.InRange(CloseAt(scene, 4062), 104.001m, 107.999m);
        Assert.Equal(108m, CloseAt(scene, 4320));
    }

    [Fact]
    public void A_held_value_equal_to_what_is_shown_starts_no_tween()
    {
        var scene = StartedAtHundred();
        clock.AtMs(2000);
        scene.OnData(Forming(101m), Ticker(101m));
        Assert.Equal(101m, CloseAt(scene, 2400));

        clock.AtMs(2500);
        scene.OnData(Forming(107m), Ticker(107m)); // held
        clock.AtMs(2800);
        scene.OnData(Forming(101m), Ticker(101m)); // the price went back: the last value is what is shown

        clock.AtMs(3100);
        scene.OnData(Forming(101m), Ticker(101m));

        Assert.False(scene.NeedsFrame);
        Assert.Equal(101m, CloseAt(scene, 3160));
    }

    // ---- the Kraken case: an update every 250 ms ----

    [Fact]
    public void A_quarter_second_feed_starts_at_most_one_tween_per_second_and_never_stalls()
    {
        var scene = StartedAtHundred();
        var starts = new List<double>();
        var frames = 0;
        var wasNeeding = false;
        decimal shown = 100m;

        // 10 s of a feed that changes the forming close every 250 ms; the host draws on a 10 ms grid.
        for (var ms = 1000; ms <= 11_000; ms += 10)
        {
            clock.AtMs(ms);
            if (ms % 250 == 0)
            {
                var close = 100m + (ms / 250);
                scene.OnData(Forming(close), Ticker(close));
            }

            var needs = scene.NeedsFrame;
            if (needs && !wasNeeding)
            {
                starts.Add(ms);
            }

            wasNeeding = needs;
            if (needs)
            {
                frames++;
                shown = scene.TakeFrame().Candles[^1].Close;
            }
        }

        Assert.True(starts.Count >= 8, $"held values were never released, tweens started at {string.Join(", ", starts)}");
        for (var i = 1; i < starts.Count; i++)
        {
            Assert.True(starts[i] - starts[i - 1] >= 1000,
                $"two tweens started {starts[i] - starts[i - 1]} ms apart ({starts[i - 1]} and {starts[i]})");
        }

        // 300 ms of tween per second: about a third of the 10 ms grid, never the whole time.
        Assert.InRange(frames, 1, 420);

        // The feed stops: the last value is shown within a second and nothing keeps presenting.
        var final = Forming(500m);
        for (var ms = 11_010; ms <= 13_000; ms += 10)
        {
            clock.AtMs(ms);
            scene.OnData(final, Ticker(500m));
            if (scene.NeedsFrame)
            {
                shown = scene.TakeFrame().Candles[^1].Close;
            }
        }

        Assert.Equal(500m, shown);
        Assert.False(scene.NeedsFrame);
    }

    // ---- a new bar is not delayed ----

    [Fact]
    public void A_new_bar_inside_the_second_is_shown_and_shifts_at_once()
    {
        var scene = StartedAtHundred();
        clock.AtMs(2000);
        scene.OnData(Forming(101m), Ticker(101m));
        Assert.Equal(101m, CloseAt(scene, 2400));

        clock.AtMs(2600);
        var before = host.Calls.Count;
        var newBar = Bar(BarC, 102m);
        scene.OnData([ClosedA, Bar(BarB, 101m, closed: true), newBar], Ticker(102m));

        Assert.Equal(["frame"], host.Since(before));
        Assert.True(scene.NeedsFrame);
        clock.AtMs(2600);
        var frame = scene.TakeFrame();
        Assert.Equal(1.0, frame.ShiftPitches);
        Assert.Equal(3, frame.Candles.Count);
        Assert.Equal(newBar, frame.Candles[^1]);
    }

    [Fact]
    public void A_new_bar_drops_a_value_held_for_the_older_bar()
    {
        var scene = StartedAtHundred();
        clock.AtMs(2000);
        scene.OnData(Forming(101m), Ticker(101m));
        clock.AtMs(2400);
        scene.OnData(Forming(130m), Ticker(130m)); // held for bar B

        clock.AtMs(2500);
        var newBar = Bar(BarC, 131m);
        scene.OnData([ClosedA, Bar(BarB, 130m, closed: true), newBar], Ticker(131m));
        Assert.Equal(newBar, scene.TakeFrame().Candles[^1]);

        // Polls after the second: bar C stays as it is, the old held 130 does not come back.
        for (var ms = 3000; ms <= 4000; ms += 250)
        {
            clock.AtMs(ms);
            scene.OnData([ClosedA, Bar(BarB, 130m, closed: true), newBar], Ticker(131m));
            Assert.Equal(newBar, scene.TakeFrame().Candles[^1]);
        }
    }

    // ---- the headline is not coalesced ----

    [Fact]
    public void A_ticker_change_inside_the_second_requests_a_frame_and_shows_at_once()
    {
        var scene = StartedAtHundred();
        clock.AtMs(2000);
        scene.OnData(Forming(101m), Ticker(101m));
        Assert.Equal(101m, CloseAt(scene, 2400));

        clock.AtMs(2500);
        var before = host.Calls.Count;
        scene.OnData(Forming(120m), Ticker(120m));

        Assert.Equal(["frame"], host.Since(before));
        var frame = scene.TakeFrame();
        Assert.Equal(Ticker(120m), frame.Ticker);
        Assert.Equal(101m, frame.Candles[^1].Close); // the candle is still held
    }

    // ---- pause and resume ----

    [Fact]
    public void Paused_a_held_value_is_applied_without_frames_and_the_resume_shows_the_latest_data()
    {
        var scene = StartedAtHundred();
        clock.AtMs(2000);
        scene.OnData(Forming(101m), Ticker(101m));
        Assert.Equal(101m, CloseAt(scene, 2400));
        clock.AtMs(2450);
        scene.OnData(Forming(102m), Ticker(102m)); // held

        clock.AtMs(2500);
        var before = host.Calls.Count;
        scene.OnPlayback(PlaybackState.Paused);
        clock.AtMs(2600);
        scene.OnData(Forming(103m), Ticker(103m)); // held, replaces 102
        Assert.False(scene.NeedsFrame);
        clock.AtMs(3100);
        scene.OnData(Forming(103m), Ticker(103m)); // the second is over: applied, nobody draws it
        Assert.False(scene.NeedsFrame);
        clock.AtMs(3500);
        Assert.False(scene.NeedsFrame);
        Assert.Equal(["pause"], host.Since(before));

        scene.OnPlayback(PlaybackState.Running);

        Assert.Equal(["pause", "resume", "frame"], host.Since(before));
        var frame = scene.TakeFrame();
        Assert.Equal(103m, frame.Candles[^1].Close);
        Assert.Equal(Ticker(103m), frame.Ticker);
        Assert.False(scene.NeedsFrame);
    }

    [Fact]
    public void Pausing_while_a_value_is_held_stops_the_frames_and_no_frame_is_requested_by_later_polls()
    {
        var scene = StartedAtHundred();
        clock.AtMs(2000);
        scene.OnData(Forming(101m), Ticker(101m));
        clock.AtMs(2450);
        scene.OnData(Forming(102m), Ticker(102m)); // held
        scene.OnPlayback(PlaybackState.Paused);
        var before = host.Calls.Count;

        for (var ms = 2700; ms <= 6000; ms += 250)
        {
            clock.AtMs(ms);
            scene.OnData(Forming(102m), Ticker(102m));
            Assert.False(scene.NeedsFrame, $"a frame was asked for while paused at {ms} ms");
        }

        Assert.Empty(host.Since(before));
    }
}
