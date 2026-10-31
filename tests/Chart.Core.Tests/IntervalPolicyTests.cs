// Contract #18, criterion 1 (D-13): the candle interval is chosen per screen so that the period (30 days now; #25
// adds 1d / 7d / 1y) fits the drawable candle slots; criterion 3: a display switch that changes the interval is a
// full reload (the pure decision; the host acts on it).
//
// API SKETCH (written before the code, #237). In src/Chart.Core/Layout/IntervalPolicy.cs, namespace
// BitcoinWallpaper.Chart.Core.Layout. Pure: no clock, no I/O.
//
//   public sealed record IntervalDecision(TimeSpan Interval, bool Reload);
//
//   public static class IntervalPolicy
//   {
//       // 30 days (D-13).
//       public static TimeSpan DefaultPeriod { get; }
//
//       // The intervals the policy picks from, ascending: 1h, 2h, 4h, 6h, 8h, 12h, 1d.
//       public static IReadOnlyList<TimeSpan> Ladder { get; }
//
//       // ceil(period / interval): 30 d / 4 h = 180, 30 d / 6 h = 120.
//       // period <= 0 or interval <= 0: ArgumentOutOfRangeException.
//       public static int CandleCount(TimeSpan period, TimeSpan interval);
//
//       // The smallest Ladder interval i with CandleCount(period, i) + rightOffsetBars <= candleSlots;
//       // when none fits, the largest (1d).
//       // candleSlots < 0, rightOffsetBars < 0 or period <= 0: ArgumentOutOfRangeException.
//       public static TimeSpan ForSlots(int candleSlots, int rightOffsetBars, TimeSpan period);
//
//       // ForSlots(l.CandleSlots, l.RightOffsetBars, period) with l = LayoutEngine.Compute(widthPx, heightPx,
//       // dpiScale, candleCount: 0) - the slots do not depend on the candle count. Pitch rules unchanged.
//       public static TimeSpan ForScreen(int widthPx, int heightPx, double dpiScale, TimeSpan period);
//
//       // On start and on every display change. Interval = ForScreen(...); Reload = (current != Interval);
//       // current == null (nothing loaded yet) -> Reload = true.
//       public static IntervalDecision OnDisplay(TimeSpan? current, int widthPx, int heightPx, double dpiScale,
//                                                TimeSpan period);
//   }
//
// Expected intervals at 30 days, with the D-30 layout as corrected by the owner (plot from x = 0 to the price
// axis whose column runs to the screen edge, Plot.Right where it was (the axis width + ~1.5 % from the edge),
// right offset 0 bars (owner 2026-10-31); pitch unchanged):
//   5120x1440 @1.75  pitch 16, ~305 slots  -> 4h (180 <= 305; 2h: 360 does not fit); ~305 drawn = ~50.8 days
//   3840x2160 @1.0   TV, pitch 24, ~145    -> 6h (120 <= 145; 4h: 180 does not fit); ~145 drawn = ~36 days
//   3840x2160 @1.5   desk, pitch 24, ~147  -> 6h
//   3440x1440 @1.0   pitch 16, ~200-202    -> 4h (180 <= 200)
//   1920x1080 @1.0   pitch 15, ~116        -> 8h (90; 6h: 120 does not fit)
//   1080x1920 @1.0   pitch 21, ~40         -> 1d (30 <= 40; 12h: 60 does not fit)

using BitcoinWallpaper.Chart.Core.Layout;

namespace BitcoinWallpaper.Chart.Core.Tests;

public class IntervalPolicyTests
{
    private static readonly TimeSpan ThirtyDays = TimeSpan.FromDays(30);

    private static TimeSpan H(double hours) => TimeSpan.FromHours(hours);

    // ---- the ladder and the candle count ------------------------------------------------------

    [Fact]
    public void The_default_period_is_30_days()
    {
        Assert.Equal(ThirtyDays, IntervalPolicy.DefaultPeriod);
    }

    [Fact]
    public void The_ladder_is_1h_2h_4h_6h_8h_12h_1d_ascending()
    {
        Assert.Equal([H(1), H(2), H(4), H(6), H(8), H(12), H(24)], IntervalPolicy.Ladder.ToArray());
    }

    [Theory]
    [InlineData(1, 720)]
    [InlineData(2, 360)]
    [InlineData(4, 180)]
    [InlineData(6, 120)]
    [InlineData(8, 90)]
    [InlineData(12, 60)]
    [InlineData(24, 30)]
    public void Thirty_days_hold_this_many_candles(double hours, int expected)
    {
        Assert.Equal(expected, IntervalPolicy.CandleCount(ThirtyDays, H(hours)));
    }

    [Fact]
    public void A_period_that_is_not_a_whole_number_of_candles_rounds_up()
    {
        Assert.Equal(2, IntervalPolicy.CandleCount(H(5), H(4)));
        Assert.Equal(1, IntervalPolicy.CandleCount(TimeSpan.FromMinutes(1), H(4)));
    }

    // ---- the rule on slots --------------------------------------------------------------------

    [Theory]
    [InlineData(188, 8, 4)]   // exactly 180 + 8
    [InlineData(187, 8, 6)]   // one slot short of 4h
    [InlineData(368, 8, 2)]   // exactly 360 + 8
    [InlineData(728, 8, 1)]   // exactly 720 + 8
    [InlineData(5000, 8, 1)]  // plenty: the smallest
    [InlineData(128, 8, 6)]
    [InlineData(127, 8, 8)]
    [InlineData(98, 8, 8)]
    [InlineData(97, 8, 12)]
    [InlineData(68, 8, 12)]
    [InlineData(67, 8, 24)]
    [InlineData(38, 8, 24)]
    [InlineData(188, 0, 4)]   // the right offset counts: without it 180 slots are enough
    [InlineData(180, 0, 4)]
    public void The_smallest_ladder_interval_whose_count_plus_offset_fits_the_slots(int slots, int offset, double expectedHours)
    {
        Assert.Equal(H(expectedHours), IntervalPolicy.ForSlots(slots, offset, ThirtyDays));
    }

    [Theory]
    [InlineData(37)]
    [InlineData(10)]
    [InlineData(0)]
    public void When_nothing_fits_the_largest_interval_is_used(int slots)
    {
        Assert.Equal(H(24), IntervalPolicy.ForSlots(slots, 8, ThirtyDays));
    }

    [Fact]
    public void The_period_is_a_parameter()
    {
        // #25 adds 1d / 7d / 1y; the rule is the same.
        Assert.Equal(H(1), IntervalPolicy.ForSlots(176, 8, TimeSpan.FromDays(7)));   // 168 + 8
        Assert.Equal(H(2), IntervalPolicy.ForSlots(175, 8, TimeSpan.FromDays(7)));   // 84 + 8
        Assert.Equal(H(1), IntervalPolicy.ForSlots(32, 8, TimeSpan.FromDays(1)));    // 24 + 8
        Assert.Equal(H(24), IntervalPolicy.ForSlots(297, 8, TimeSpan.FromDays(365))); // nothing fits
    }

    [Theory]
    [InlineData(-1, 8, 30)]
    [InlineData(100, -1, 30)]
    [InlineData(100, 8, 0)]
    [InlineData(100, 8, -1)]
    public void Invalid_arguments_are_refused(int slots, int offset, double periodDays)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => IntervalPolicy.ForSlots(slots, offset, TimeSpan.FromDays(periodDays)));
    }

    [Fact]
    public void A_zero_period_or_interval_count_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => IntervalPolicy.CandleCount(TimeSpan.Zero, H(4)));
        Assert.Throws<ArgumentOutOfRangeException>(() => IntervalPolicy.CandleCount(ThirtyDays, TimeSpan.Zero));
    }

    // ---- per screen ---------------------------------------------------------------------------

    [Theory]
    [InlineData(5120, 1440, 1.75, 4, 180)] // the owner's desk monitor (D-13)
    [InlineData(3840, 2160, 1.0, 6, 120)]  // the owner's TV (D-13)
    public void Owner_screens_get_4h_with_180_candles_and_6h_with_120(int w, int h, double s, double hours, int candles)
    {
        var interval = IntervalPolicy.ForScreen(w, h, s, ThirtyDays);
        Assert.Equal(H(hours), interval);
        Assert.Equal(candles, IntervalPolicy.CandleCount(ThirtyDays, interval));

        // The 30 days fit in front of the right offset, with the pitch rules unchanged.
        var layout = LayoutEngine.Compute(w, h, s, candles);
        Assert.Equal(candles, layout.DrawnCandleCount);
        Assert.Equal(0, layout.FirstDrawnIndex);
    }

    [Theory]
    [InlineData(3840, 2160, 1.5, 6)]
    [InlineData(3440, 1440, 1.0, 4)]
    [InlineData(1920, 1080, 1.0, 8)]
    [InlineData(1080, 1920, 1.0, 24)]
    public void Other_screens_get_a_sensible_interval(int w, int h, double s, double hours)
    {
        Assert.Equal(H(hours), IntervalPolicy.ForScreen(w, h, s, ThirtyDays));
    }

    public static TheoryData<int, int, double> Screens => new()
    {
        { 5120, 1440, 1.75 },
        { 3840, 2160, 1.0 },
        { 3840, 2160, 1.5 },
        { 1920, 1080, 1.0 },
        { 3440, 1440, 1.0 },
        { 1080, 1920, 1.0 },
        { 2560, 1440, 1.0 },
        { 7680, 4320, 1.0 },
    };

    [Theory]
    [MemberData(nameof(Screens))]
    public void Per_screen_the_rule_is_applied_to_the_layout_slots(int w, int h, double s)
    {
        var l = LayoutEngine.Compute(w, h, s, 0);
        var interval = IntervalPolicy.ForScreen(w, h, s, ThirtyDays);
        Assert.Equal(IntervalPolicy.ForSlots(l.CandleSlots, l.RightOffsetBars, ThirtyDays), interval);
        Assert.Contains(interval, IntervalPolicy.Ladder);

        // Sensible: 30 days are on screen, and the next smaller interval would not fit them.
        var count = IntervalPolicy.CandleCount(ThirtyDays, interval);
        Assert.True(count + l.RightOffsetBars <= l.CandleSlots,
            $"{w}x{h}@{s}: {count} candles of {interval} + {l.RightOffsetBars} offset > {l.CandleSlots} slots");
        var index = IntervalPolicy.Ladder.ToList().IndexOf(interval);
        if (index > 0)
        {
            var smaller = IntervalPolicy.Ladder[index - 1];
            Assert.True(IntervalPolicy.CandleCount(ThirtyDays, smaller) + l.RightOffsetBars > l.CandleSlots,
                $"{w}x{h}@{s}: {smaller} would fit too, {interval} is not the smallest");
        }
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void The_same_screen_always_gets_the_same_interval(int w, int h, double s)
    {
        Assert.Equal(IntervalPolicy.ForScreen(w, h, s, ThirtyDays), IntervalPolicy.ForScreen(w, h, s, ThirtyDays));
    }

    // ---- display switch -> reload decision (criterion 3) ----------------------------------------

    [Fact]
    public void At_start_the_screen_interval_is_loaded()
    {
        var d = IntervalPolicy.OnDisplay(null, 5120, 1440, 1.75, ThirtyDays);
        Assert.Equal(new IntervalDecision(H(4), Reload: true), d);
    }

    [Fact]
    public void Switching_from_the_desk_monitor_to_the_tv_reloads_with_6h()
    {
        var d = IntervalPolicy.OnDisplay(H(4), 3840, 2160, 1.0, ThirtyDays);
        Assert.Equal(new IntervalDecision(H(6), Reload: true), d);
    }

    [Fact]
    public void Switching_from_the_tv_to_the_desk_monitor_reloads_with_4h()
    {
        var d = IntervalPolicy.OnDisplay(H(6), 5120, 1440, 1.75, ThirtyDays);
        Assert.Equal(new IntervalDecision(H(4), Reload: true), d);
    }

    [Theory]
    [InlineData(4, 5120, 1440, 1.75)] // the same monitor again (a DPI or mode change that keeps the interval)
    [InlineData(4, 5120, 1440, 1.0)]  // same pitch and slots at 100 %
    [InlineData(6, 3840, 2160, 1.5)]  // TV -> 4K desk monitor at 150 %: also 6h
    [InlineData(6, 3840, 2160, 1.0)]
    public void A_display_change_that_keeps_the_interval_does_not_reload(double currentHours, int w, int h, double s)
    {
        var d = IntervalPolicy.OnDisplay(H(currentHours), w, h, s, ThirtyDays);
        Assert.Equal(new IntervalDecision(H(currentHours), Reload: false), d);
    }

    [Fact]
    public void A_change_of_period_on_the_same_screen_reloads_when_the_interval_changes()
    {
        var d = IntervalPolicy.OnDisplay(H(4), 5120, 1440, 1.75, TimeSpan.FromDays(7));
        Assert.Equal(new IntervalDecision(H(1), Reload: true), d);
    }
}
