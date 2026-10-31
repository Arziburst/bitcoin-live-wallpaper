// Contract #18, criterion 3: on a display switch that changes the interval the host reloads the series in full;
// intervals are never mixed - not in the buffer (Market.Data tests) and not on screen: the scene never tweens or
// slides from a candle of the old interval to one of the new.
//
// API SKETCH (written before the code, #237), added to src/Chart.Core/Motion/LiveScene.cs:
//
//   public sealed class LiveScene
//   {
//       // A full reload (IntervalPolicy.OnDisplay(...).Reload): forgets the series shown and the motion state.
//       // Until the next OnData, TakeFrame returns no candles. The next OnData is shown at once - no forming
//       // tween, no new-bar shift, no price pulse - and requests one frame like any data change.
//       // Pause state, stale flag and the started state are kept.
//       public void Reset();
//   }
//
// The host (LiveChartScene) on a display change: d = IntervalPolicy.OnDisplay(current, w, h, scale, period);
// if d.Reload: client.ChangeInterval(d.Interval) and live.Reset(); every ChartFrame then carries d.Interval.

using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Motion;
using BitcoinWallpaper.Chart.Core.Pause;

namespace BitcoinWallpaper.Chart.Core.Tests;

public class LiveSceneReloadTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 31, 19, 0, 0, TimeSpan.Zero);

    private readonly Clock clock = new(Start);
    private readonly Host host = new();

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => now.UtcTicks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new NotSupportedException("The live scene must not use timers: the host drives it.");

        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class Host : IFrameHost
    {
        public List<string> Calls { get; } = [];

        public void RequestFrame() => Calls.Add("frame");

        public void SetPaused(bool paused) => Calls.Add(paused ? "pause" : "resume");
    }

    private static Candle Bar(DateTimeOffset open, decimal o, decimal h, decimal l, decimal c, bool closed) =>
        new(open, o, h, l, c, 10m, closed);

    private static DateTimeOffset At(int day, int hour) => new(2026, 10, day, hour, 0, 0, TimeSpan.Zero);

    // The 4h series on the desk monitor: ..., 12:00 closed, 16:00 forming.
    private static readonly IReadOnlyList<Candle> FourHour =
    [
        Bar(At(31, 8), 70000m, 70500m, 69800m, 70400m, true),
        Bar(At(31, 12), 70400m, 70900m, 70300m, 70800m, true),
        Bar(At(31, 16), 70800m, 71000m, 70700m, 70950m, false),
    ];

    // The 6h series after the switch to the TV, newest bar later than the 4h one: a new-bar shift without Reset.
    private static readonly IReadOnlyList<Candle> SixHourNewer =
    [
        Bar(At(31, 6), 69900m, 70600m, 69700m, 70500m, true),
        Bar(At(31, 12), 70500m, 70950m, 70300m, 70700m, true),
        Bar(At(31, 18), 70700m, 71200m, 70650m, 71100m, false),
    ];

    // The 6h series whose newest bar opens at the same time as the 4h one (12:00 forming in both): a forming tween
    // from the 4h candle's OHLC without Reset.
    private static readonly IReadOnlyList<Candle> FourHourAtNoon =
    [
        Bar(At(31, 4), 69500m, 70100m, 69400m, 70000m, true),
        Bar(At(31, 8), 70000m, 70500m, 69800m, 70400m, true),
        Bar(At(31, 12), 70400m, 70900m, 70300m, 70800m, false),
    ];

    private static readonly IReadOnlyList<Candle> SixHourAtNoon =
    [
        Bar(At(30, 18), 68800m, 69600m, 68700m, 69500m, true),
        Bar(At(31, 0), 69500m, 70200m, 69100m, 70050m, true),
        Bar(At(31, 6), 70050m, 70600m, 69900m, 70300m, true),
        Bar(At(31, 12), 70300m, 71500m, 70100m, 71400m, false),
    ];

    private static readonly Ticker24h Ticker = new(71000m, 69000m, 71500m, 68500m, 2000m, 2.9m, 12000m);

    private LiveScene ShowingFourHour(IReadOnlyList<Candle> series)
    {
        var scene = new LiveScene(clock, host);
        scene.OnHostStarted();
        scene.OnData(series, Ticker);
        scene.TakeFrame();
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(scene.NeedsFrame);
        return scene;
    }

    public static TheoryData<string> Switches => new() { "newer", "same open time" };

    private static (IReadOnlyList<Candle> Before, IReadOnlyList<Candle> After) Series(string which) =>
        which == "newer" ? (FourHour, SixHourNewer) : (FourHourAtNoon, SixHourAtNoon);

    [Theory]
    [MemberData(nameof(Switches))]
    public void After_a_reset_the_new_series_is_shown_at_once_without_motion(string which)
    {
        var (before, after) = Series(which);
        var scene = ShowingFourHour(before);

        scene.Reset();
        scene.OnData(after, Ticker);

        var frame = scene.TakeFrame();
        Assert.Equal(after, frame.Candles); // the newest candle exactly as the 6h feed has it, not tweened from the 4h one
        Assert.Equal(0.0, frame.ShiftPitches);
        Assert.Equal(0.0, frame.Pulse);
        Assert.False(scene.NeedsFrame, "nothing animates after a reload");
    }

    [Theory]
    [MemberData(nameof(Switches))]
    public void No_frame_after_a_reset_shows_a_candle_of_the_old_interval_or_a_blend(string which)
    {
        var (before, after) = Series(which);
        var scene = ShowingFourHour(before);

        scene.Reset();
        Assert.Empty(scene.TakeFrame().Candles); // the old series is gone at once

        scene.OnData(after, Ticker);
        for (var i = 0; i < 120; i++)
        {
            var frame = scene.TakeFrame();
            Assert.Equal(after, frame.Candles);
            clock.Advance(TimeSpan.FromMilliseconds(1000.0 / 120));
        }
    }

    [Fact]
    public void The_reloaded_data_requests_one_frame()
    {
        var scene = ShowingFourHour(FourHour);
        scene.Reset();
        var before = host.Calls.Count;

        scene.OnData(SixHourNewer, Ticker);

        Assert.Equal(["frame"], host.Calls.Skip(before).ToArray());
    }

    [Fact]
    public void After_a_reset_the_new_series_animates_as_usual()
    {
        var scene = ShowingFourHour(FourHour);
        scene.Reset();
        scene.OnData(SixHourNewer, Ticker);
        scene.TakeFrame();
        clock.Advance(TimeSpan.FromSeconds(2));

        // The next 6h bar arrives: the usual new-bar shift.
        var next = SixHourNewer.Append(Bar(At(31, 18).AddHours(6), 71100m, 71150m, 71050m, 71120m, false)).ToArray();
        scene.OnData(next, Ticker);
        var frame = scene.TakeFrame();
        Assert.Equal(1.0, frame.ShiftPitches, 6);
    }

    [Fact]
    public void A_reset_keeps_the_pause()
    {
        var scene = ShowingFourHour(FourHour);
        scene.OnPlayback(PlaybackState.Paused);
        scene.Reset();
        var before = host.Calls.Count;

        scene.OnData(SixHourNewer, Ticker);

        Assert.True(scene.IsPaused);
        Assert.False(scene.NeedsFrame);
        Assert.Empty(host.Calls.Skip(before)); // paused: no frame request
    }

    // Round 8 (PR #37 merged from main: at most one forming-candle tween per second, later updates held). Reset
    // also forgets that coalescing state - the candle last applied, the held update and the time of the last forming
    // tween - so a reload starts clean: the first forming change of the new series tweens at once (no ~1 s hold
    // left over from the old series) and nothing of the old series is ever shown again.
    [Fact]
    public void After_a_reset_the_first_forming_change_tweens_at_once_and_nothing_held_from_the_old_series_shows()
    {
        static IReadOnlyList<Candle> WithLast(IReadOnlyList<Candle> series, decimal high, decimal close) =>
            series.Take(series.Count - 1).Append(series[^1] with { High = high, Close = close }).ToArray();

        var scene = ShowingFourHour(FourHourAtNoon); // 4h, newest 12:00 forming at 70800
        clock.Advance(TimeSpan.FromSeconds(5));

        // A forming tween of the old series starts, and the next update inside the second is held.
        scene.OnData(WithLast(FourHourAtNoon, 70900m, 70850m), Ticker);
        scene.TakeFrame();
        clock.Advance(TimeSpan.FromMilliseconds(200));
        scene.OnData(WithLast(FourHourAtNoon, 70950m, 70933m), Ticker); // held by the coalescing (old series)
        clock.Advance(TimeSpan.FromMilliseconds(100));

        // The reload: the 6h series' newest candle opens at the same time (12:00), 300 ms after the old tween.
        scene.Reset();
        scene.OnData(SixHourAtNoon, Ticker);
        Assert.Equal(SixHourAtNoon, scene.TakeFrame().Candles);
        clock.Advance(TimeSpan.FromMilliseconds(100));

        // The first forming change of the new series: a tween starts now, from 71400 towards 71500.
        var before = host.Calls.Count;
        var update = WithLast(SixHourAtNoon, 71600m, 71500m);
        scene.OnData(update, Ticker);
        Assert.True(scene.NeedsFrame, "the first forming change after the reload did not start a tween (held from the old series?)");
        Assert.Equal(["frame"], host.Calls.Skip(before).ToArray());
        var first = scene.TakeFrame();
        Assert.Equal(71400m, first.Candles[^1].Close); // the tween starts from what is shown
        Assert.Equal(1.0, first.Pulse);

        var old = new[] { 70800m, 70850m, 70933m };
        var sawBetween = false;
        for (var ms = 0; ms < 1500; ms += 25)
        {
            clock.Advance(TimeSpan.FromMilliseconds(25));
            scene.OnData(update, Ticker); // the host polls the same snapshot every few ms
            var frame = scene.TakeFrame();
            var close = frame.Candles[^1].Close;
            Assert.DoesNotContain(close, old);
            Assert.InRange(close, 71400m, 71500m);
            sawBetween |= close > 71400m && close < 71500m;
            Assert.Equal(SixHourAtNoon.Take(SixHourAtNoon.Count - 1), frame.Candles.Take(frame.Candles.Count - 1));
        }

        Assert.True(sawBetween, "no tween frame between 71400 and 71500");
        Assert.Equal(update[^1], scene.TakeFrame().Candles[^1]);
    }
}
