// Contract #4, criteria 1 and 2: the layout engine.
// Contract #18 (D-20): the content spans the full screen width; nothing is reserved for desktop icons any more.
// The tests that asserted the left 8 % icon safe area now assert that the plot starts at the left margin
// (<= 1 % of the width); IconSafeArea is no longer read by these tests (the worker may drop it).
//
// API these tests need (the worker implements it as written, in src/Chart.Core/Layout/):
//
//   namespace BitcoinWallpaper.Chart.Core.Layout;
//
//   public enum DisplayProfile { Desk, Tv }
//
//   // A rectangle in physical pixels, origin top-left of the screen.
//   public readonly record struct RectPx(double X, double Y, double Width, double Height);
//
//   // Text sizes in physical px (never scaled down by the Windows DPI scale).
//   public sealed record TextSizes(double HeadlinePricePx, double PairSourcePx, double ChangePx,
//                                  double StatsRowPx, double AxisLabelPx);
//
//   public sealed class/record ChartLayout   // get-only properties, shape otherwise free
//   {
//       int WidthPx; int HeightPx; double DpiScale; DisplayProfile Profile;
//       RectPx Plot;          // candle (price) pane
//       RectPx PriceAxis;     // price labels, right of the plot
//       RectPx TimeAxis;      // time labels, under the volume pane
//       RectPx VolumePane;    // volume bars, directly under the plot, same x-range as the plot
//       RectPx Headline;      // price, 24h change, H/L/volume row, pair + source
//       (IconSafeArea: dropped from the sketch by D-20 / #18; no test reads it)
//       TextSizes Text;
//       double CandlePitchPx;      // slot width per candle
//       double CandleBodyWidthPx;  // ~62 % of the pitch
//       double WickWidthPx;        // 1 desk, 2 TV
//       int CandleCount;           // the candleCount passed to Compute
//       int CandleSlots;           // floor(Plot.Width / CandlePitchPx)
//       int RightOffsetBars;       // ~8 empty slots right of the newest candle (D-30 as corrected by the owner:
//                                  // no 10 % gap); Plot.X == 0 and PriceAxis.Right at 98.5 % +- 0.5 % of WidthPx
//       int DrawnCandleCount;      // min(CandleCount, CandleSlots - RightOffsetBars)
//       int FirstDrawnIndex;       // CandleCount - DrawnCandleCount (index 0 = oldest candle)
//       double CandleCenterX(int index);
//           // = Plot.X + Plot.Width - (RightOffsetBars + (CandleCount - 1 - index) + 0.5) * CandlePitchPx
//   }
//
//   public static class LayoutEngine
//   {
//       // Tv iff dpiScale == 1.0 (within 0.01) and widthPx >= 3840 and heightPx >= 2160
//       //    and widthPx / heightPx is 16:9 (within 0.01); everything else is Desk.
//       public static DisplayProfile ProfileFor(int widthPx, int heightPx, double dpiScale);
//
//       // widthPx, heightPx, dpiScale > 0 and candleCount >= 0, else ArgumentOutOfRangeException.
//       public static ChartLayout Compute(int widthPx, int heightPx, double dpiScale, int candleCount);
//   }
//
// Where the spec gives a percentage the tests use a tolerance; where it leaves freedom they check
// invariants (inside the screen, no overlap, the plot from the left margin to the price axis,
// readable text) instead of exact positions.

using BitcoinWallpaper.Chart.Core.Layout;

namespace BitcoinWallpaper.Chart.Core.Tests;

public class LayoutEngineTests
{
    private const int RingBuffer = 1000;

    // The six screens the contract names.
    public static TheoryData<int, int, double> Screens => new()
    {
        { 5120, 1440, 1.75 }, // owner's desk monitor, 32:9, 175 %
        { 3840, 2160, 1.0 },  // owner's TV, 16:9, TV profile
        { 3840, 2160, 1.5 },  // 4K monitor at 150 %, desk profile
        { 1920, 1080, 1.0 },
        { 3440, 1440, 1.0 },  // 21:9
        { 1080, 1920, 1.0 },  // portrait
    };

    private static double Right(RectPx r) => r.X + r.Width;

    private static double Bottom(RectPx r) => r.Y + r.Height;

    // Overlap with positive area; touching edges (within half a pixel) is not an overlap.
    private static bool Overlaps(RectPx a, RectPx b) =>
        Math.Min(Right(a), Right(b)) - Math.Max(a.X, b.X) > 0.5 &&
        Math.Min(Bottom(a), Bottom(b)) - Math.Max(a.Y, b.Y) > 0.5;

    private static ChartLayout Layout(int w, int h, double s, int candles = RingBuffer) =>
        LayoutEngine.Compute(w, h, s, candles);

    private static void Near(double expected, double actual, double relTolerance, string what) =>
        Assert.True(Math.Abs(actual - expected) <= expected * relTolerance,
            $"{what}: expected {expected:0.##} ±{relTolerance:P0}, got {actual:0.##}");

    // ---- profile rule -------------------------------------------------------------------------

    [Theory]
    [InlineData(3840, 2160, 1.0, DisplayProfile.Tv)]
    [InlineData(7680, 4320, 1.0, DisplayProfile.Tv)]
    [InlineData(3840, 2160, 1.5, DisplayProfile.Desk)]
    [InlineData(3840, 2160, 1.25, DisplayProfile.Desk)]
    [InlineData(5120, 1440, 1.75, DisplayProfile.Desk)]
    [InlineData(5120, 1440, 1.0, DisplayProfile.Desk)]
    [InlineData(1920, 1080, 1.0, DisplayProfile.Desk)]
    [InlineData(2560, 1440, 1.0, DisplayProfile.Desk)]
    [InlineData(3440, 1440, 1.0, DisplayProfile.Desk)]
    [InlineData(3840, 2400, 1.0, DisplayProfile.Desk)]
    [InlineData(1080, 1920, 1.0, DisplayProfile.Desk)]
    [InlineData(2160, 3840, 1.0, DisplayProfile.Desk)]
    public void Profile_is_tv_only_for_16_9_at_least_3840x2160_at_scale_1(int w, int h, double s, DisplayProfile expected)
    {
        Assert.Equal(expected, LayoutEngine.ProfileFor(w, h, s));
        Assert.Equal(expected, Layout(w, h, s).Profile);
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void Layout_echoes_the_screen_in_physical_px(int w, int h, double s)
    {
        var l = Layout(w, h, s);
        Assert.Equal(w, l.WidthPx);
        Assert.Equal(h, l.HeightPx);
        Assert.Equal(s, l.DpiScale, 6);
        Assert.Equal(RingBuffer, l.CandleCount);
    }

    // ---- rectangles: invariants ---------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Screens))]
    public void Every_rectangle_is_inside_the_screen_and_not_empty(int w, int h, double s)
    {
        var l = Layout(w, h, s);
        foreach (var (name, r) in Rects(l))
        {
            Assert.True(r.Width > 0 && r.Height > 0, $"{name} is empty: {r}");
            Assert.True(r.X >= -0.5 && r.Y >= -0.5 && Right(r) <= w + 0.5 && Bottom(r) <= h + 0.5,
                $"{name} {r} is outside the {w}x{h} screen");
        }
    }

    // D-20 (#18): replaces "Icon_safe_area_is_the_left_8_percent_full_height" and
    // "Nothing_but_the_chart_body_enters_the_icon_safe_area": there is no icon safe area any more.
    // D-30 (#18, owner 2026-10-31): was "..._at_the_left_margin_..." with -0.5 <= Plot.X <= 1 % W; now no left margin.
    [Theory]
    [MemberData(nameof(Screens))]
    public void Plot_starts_at_the_left_screen_edge_nothing_is_reserved_for_icons(int w, int h, double s)
    {
        var l = Layout(w, h, s);
        Assert.True(Math.Abs(l.Plot.X) <= 0.5, $"plot starts at {l.Plot.X}, expected the left screen edge (0)");
        Assert.True(Math.Abs(l.VolumePane.X - l.Plot.X) <= 1, $"volume pane {l.VolumePane} does not start with the plot {l.Plot}");
        Assert.True(Math.Abs(l.TimeAxis.X - l.Plot.X) <= 1, $"time axis {l.TimeAxis} does not start with the plot {l.Plot}");
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void Rectangles_do_not_overlap(int w, int h, double s)
    {
        var l = Layout(w, h, s);
        // The headline may lie over the plot (soft scrim, spec); no other pair may overlap.
        var pairs = new (string, RectPx, string, RectPx)[]
        {
            ("Plot", l.Plot, "PriceAxis", l.PriceAxis),
            ("Plot", l.Plot, "TimeAxis", l.TimeAxis),
            ("Plot", l.Plot, "VolumePane", l.VolumePane),
            ("VolumePane", l.VolumePane, "TimeAxis", l.TimeAxis),
            ("VolumePane", l.VolumePane, "PriceAxis", l.PriceAxis),
            ("TimeAxis", l.TimeAxis, "PriceAxis", l.PriceAxis),
            ("Headline", l.Headline, "PriceAxis", l.PriceAxis),
            ("Headline", l.Headline, "TimeAxis", l.TimeAxis),
            ("Headline", l.Headline, "VolumePane", l.VolumePane),
        };
        foreach (var (an, a, bn, b) in pairs)
            Assert.False(Overlaps(a, b), $"{an} {a} overlaps {bn} {b}");
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void Price_axis_is_on_the_right_next_to_the_plot(int w, int h, double s)
    {
        var l = Layout(w, h, s);
        Assert.True(l.PriceAxis.X > w / 2.0, $"price axis {l.PriceAxis} not on the right");
        Assert.True(Math.Abs(l.PriceAxis.X - Right(l.Plot)) <= 1, $"price axis {l.PriceAxis} not adjacent to plot {l.Plot}");
        Assert.True(l.PriceAxis.Y <= l.Plot.Y + 1 && Bottom(l.PriceAxis) >= Bottom(l.Plot) - 1,
            $"price axis {l.PriceAxis} does not span the plot {l.Plot}");
        Assert.True(l.PriceAxis.Width >= 3 * l.Text.AxisLabelPx,
            $"price axis width {l.PriceAxis.Width} too narrow for {l.Text.AxisLabelPx} px labels");
    }

    // D-30 (#18, owner 2026-10-31), new: the price axis keeps ~1.5 % of the width from the right screen edge.
    [Theory]
    [MemberData(nameof(Screens))]
    public void Price_axis_ends_about_1_5_percent_from_the_right_screen_edge(int w, int h, double s)
    {
        var l = Layout(w, h, s);
        Assert.InRange(Right(l.PriceAxis), 0.98 * w, 0.99 * w);
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void Volume_pane_is_under_the_plot_and_time_axis_under_the_volume(int w, int h, double s)
    {
        var l = Layout(w, h, s);
        Assert.True(Math.Abs(l.VolumePane.X - l.Plot.X) <= 1 && Math.Abs(l.VolumePane.Width - l.Plot.Width) <= 1,
            $"volume pane {l.VolumePane} not aligned with plot {l.Plot}");
        var gap = l.VolumePane.Y - Bottom(l.Plot);
        Assert.True(gap >= -0.5 && gap <= 0.03 * h, $"volume pane {l.VolumePane} not directly under plot {l.Plot}");
        Assert.True(l.TimeAxis.Y >= Bottom(l.VolumePane) - 0.5, $"time axis {l.TimeAxis} not under volume {l.VolumePane}");
        Assert.True(Right(l.TimeAxis) <= Right(l.Plot) + 1, $"time axis {l.TimeAxis} reaches past the plot {l.Plot}");
        Assert.True(l.TimeAxis.Height >= l.Text.AxisLabelPx, $"time axis height {l.TimeAxis.Height} < label {l.Text.AxisLabelPx}");
    }

    [Theory]
    [InlineData(5120, 1440, 1.75, 0.13, 0.15)] // 32:9: 14 %
    [InlineData(3840, 2160, 1.0, 0.15, 0.17)]  // 16:9: 16 %
    [InlineData(3840, 2160, 1.5, 0.15, 0.17)]
    [InlineData(1920, 1080, 1.0, 0.15, 0.17)]
    [InlineData(3440, 1440, 1.0, 0.13, 0.17)]  // 21:9: between the two
    public void Volume_pane_takes_its_share_of_the_chart_height(int w, int h, double s, double min, double max)
    {
        var l = Layout(w, h, s);
        var share = l.VolumePane.Height / (l.Plot.Height + l.VolumePane.Height);
        Assert.InRange(share, min, max);
    }

    [Fact]
    public void Ultrawide_32_9_chart_spans_the_left_edge_to_98_5_percent_with_headline_top_right()
    {
        const int w = 5120, h = 1440;
        var l = Layout(w, h, 1.75);
        // D-20 (#18): was "plot starts at ~8 % (±0.5 %)"; D-30: was "-0.5 <= Plot.X <= 1 % W", now the left edge.
        Assert.True(Math.Abs(l.Plot.X) <= 0.5, $"plot starts at {l.Plot.X}, expected the left screen edge (0)");
        // D-30: was InRange(PriceAxis.Right, 96 % W, 98 % W); now 98.5 % ± 0.5 %.
        Assert.InRange(Right(l.PriceAxis), 0.98 * w, 0.99 * w);
        Assert.True(l.Headline.X >= w / 2.0, $"headline {l.Headline} not on the right");
        Assert.True(l.Headline.Y <= 0.06 * h, $"headline {l.Headline} not at the top");
        var toAxis = l.PriceAxis.X - Right(l.Headline);
        Assert.True(toAxis >= -0.5 && toAxis <= 0.05 * w, $"headline {l.Headline} not top-right next to axis {l.PriceAxis}");
    }

    // D-20 (#18, owner 2026-10-31): was "..._at_10_percent" with |Headline.X - 10 % W| <= 1 % W; the headline now
    // sits at the left margin.
    [Theory]
    [InlineData(3840, 2160, 1.0)]
    [InlineData(3840, 2160, 1.5)]
    [InlineData(1920, 1080, 1.0)]
    public void Wide_16_9_headline_is_top_left_at_the_left_margin(int w, int h, double s)
    {
        var l = Layout(w, h, s);
        Assert.True(l.Headline.X >= -0.5 && l.Headline.X <= 0.02 * w,
            $"headline x {l.Headline.X}, expected the left margin (<= 2 % of {w} = {0.02 * w})");
        Assert.True(l.Headline.Y <= 0.08 * h, $"headline {l.Headline} not at the top");
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void Headline_rectangle_fits_its_four_lines(int w, int h, double s)
    {
        var l = Layout(w, h, s);
        var t = l.Text;
        var lines = t.HeadlinePricePx + t.ChangePx + t.StatsRowPx + t.PairSourcePx;
        Assert.True(l.Headline.Height >= lines, $"headline height {l.Headline.Height} < {lines}");
        Assert.True(l.Headline.Width >= 5 * t.HeadlinePricePx, $"headline width {l.Headline.Width} < 5 x {t.HeadlinePricePx}");
    }

    // ---- candle geometry ----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Screens))]
    public void Candle_pitch_meets_the_profile_minimum(int w, int h, double s)
    {
        var l = Layout(w, h, s);
        var min = l.Profile == DisplayProfile.Tv ? 24 : 15;
        Assert.True(l.CandlePitchPx >= min, $"{l.Profile} pitch {l.CandlePitchPx} < {min}");
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void Candle_body_is_62_percent_of_pitch_and_wick_1px_desk_2px_tv(int w, int h, double s)
    {
        var l = Layout(w, h, s);
        Assert.True(Math.Abs(l.CandleBodyWidthPx - 0.62 * l.CandlePitchPx) <= 1,
            $"body {l.CandleBodyWidthPx}, expected ~{0.62 * l.CandlePitchPx}");
        Assert.Equal(l.Profile == DisplayProfile.Tv ? 2.0 : 1.0, l.WickWidthPx, 6);
    }

    // ---- text sizes ---------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Screens))]
    public void Axis_labels_are_readable(int w, int h, double s)
    {
        var l = Layout(w, h, s);
        if (l.Profile == DisplayProfile.Tv)
            Assert.True(l.Text.AxisLabelPx >= 34, $"TV axis label {l.Text.AxisLabelPx} < 34");
        else
            Assert.InRange(l.Text.AxisLabelPx, 22, 44);
    }

    [Theory]
    [InlineData(5120, 1440, 1.75)]
    [InlineData(5120, 1440, 1.0)]
    [InlineData(3440, 1440, 1.0)]
    [InlineData(3840, 2160, 1.5)]
    [InlineData(1920, 1080, 1.0)]
    public void Desk_text_sizes_follow_screen_height(int w, int h, double s)
    {
        var l = Layout(w, h, s);
        Assert.Equal(DisplayProfile.Desk, l.Profile);
        var t = l.Text;
        Near(0.06 * h, t.HeadlinePricePx, 0.03, "headline price 6 % H");
        Near(Math.Clamp(0.017 * h, 22, 44), t.AxisLabelPx, 0.03, "axis labels clamp(22, 1.7 % H, 44)");
        Assert.True(t.AxisLabelPx >= 22, $"axis label {t.AxisLabelPx} < 22");
        // Secondary text: the spec's percentage; a readable floor of 22 px is allowed above it.
        SecondaryText(0.016 * h, t.PairSourcePx, "pair + source 1.6 % H");
        SecondaryText(0.024 * h, t.ChangePx, "24h change 2.4 % H");
        SecondaryText(0.015 * h, t.StatsRowPx, "H/L/volume row 1.5 % H");
    }

    private static void SecondaryText(double expected, double actual, string what) =>
        Assert.True(actual >= expected * 0.97 && actual <= Math.Max(expected * 1.03, 22),
            $"{what}: expected {expected:0.##} (or up to 22), got {actual:0.##}");

    [Theory]
    [InlineData(3840, 2160)]
    [InlineData(7680, 4320)]
    public void Tv_text_sizes_follow_screen_height_with_34px_minimum(int w, int h)
    {
        var l = Layout(w, h, 1.0);
        Assert.Equal(DisplayProfile.Tv, l.Profile);
        var t = l.Text;
        Near(0.08 * h, t.HeadlinePricePx, 0.03, "TV headline price 8 % H");
        Near(0.020 * h, t.AxisLabelPx, 0.03, "TV axis labels 2.0 % H");
        foreach (var (name, size) in new[]
                 {
                     ("headline", t.HeadlinePricePx), ("pair", t.PairSourcePx), ("change", t.ChangePx),
                     ("stats", t.StatsRowPx), ("axis", t.AxisLabelPx),
                 })
            Assert.True(size >= 34, $"TV {name} text {size} < 34 px");
        Assert.True(t.PairSourcePx >= 0.016 * h * 0.97, $"TV pair {t.PairSourcePx} below 1.6 % H");
        Assert.True(t.ChangePx >= 0.024 * h * 0.97, $"TV change {t.ChangePx} below 2.4 % H");
        Assert.True(t.StatsRowPx >= 0.015 * h * 0.97, $"TV stats {t.StatsRowPx} below 1.5 % H");
    }

    [Fact]
    public void Tv_text_is_never_smaller_than_desk_text_on_the_same_screen()
    {
        var tv = Layout(3840, 2160, 1.0).Text;
        var desk = Layout(3840, 2160, 1.5).Text;
        Assert.True(tv.HeadlinePricePx >= desk.HeadlinePricePx);
        Assert.True(tv.PairSourcePx >= desk.PairSourcePx);
        Assert.True(tv.ChangePx >= desk.ChangePx);
        Assert.True(tv.StatsRowPx >= desk.StatsRowPx);
        Assert.True(tv.AxisLabelPx >= desk.AxisLabelPx);
    }

    [Fact]
    public void Windows_scaling_does_not_shrink_text_on_the_desk_monitor()
    {
        var at100 = Layout(5120, 1440, 1.0).Text;
        var at175 = Layout(5120, 1440, 1.75).Text;
        Assert.Equal(at100.HeadlinePricePx, at175.HeadlinePricePx, 3);
        Assert.Equal(at100.PairSourcePx, at175.PairSourcePx, 3);
        Assert.Equal(at100.ChangePx, at175.ChangePx, 3);
        Assert.Equal(at100.StatsRowPx, at175.StatsRowPx, 3);
        Assert.Equal(at100.AxisLabelPx, at175.AxisLabelPx, 3);
    }

    // ---- candle count and right offset (criterion 2) ------------------------------------------

    [Theory]
    [MemberData(nameof(Screens))]
    public void Candle_slots_are_floor_of_plot_width_over_pitch(int w, int h, double s)
    {
        var l = Layout(w, h, s);
        Assert.Equal((int)Math.Floor(l.Plot.Width / l.CandlePitchPx + 1e-9), l.CandleSlots);
    }

    // D-30 as corrected by the owner (2026-10-31): the 10 % gap before the price axis was not wanted; the right
    // offset is about 8 bars again (this test is the original one, restored).
    [Theory]
    [MemberData(nameof(Screens))]
    public void Right_offset_is_about_8_bars(int w, int h, double s)
    {
        Assert.InRange(Layout(w, h, s).RightOffsetBars, 6, 10);
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void With_a_full_ring_buffer_the_newest_candles_fill_the_slots_left_of_the_offset(int w, int h, double s)
    {
        var l = Layout(w, h, s);
        Assert.Equal(l.CandleSlots - l.RightOffsetBars, l.DrawnCandleCount);
        Assert.Equal(RingBuffer - l.DrawnCandleCount, l.FirstDrawnIndex);
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void Newest_candle_sits_right_offset_bars_left_of_the_plot_edge(int w, int h, double s)
    {
        var l = Layout(w, h, s);
        var p = l.CandlePitchPx;
        var newest = l.CandleCenterX(RingBuffer - 1);
        var expected = Right(l.Plot) - (l.RightOffsetBars + 0.5) * p;
        Assert.True(Math.Abs(newest - expected) <= 1, $"newest candle at {newest}, expected {expected}");
        Assert.Equal(p, newest - l.CandleCenterX(RingBuffer - 2), 3);
        var oldestLeft = l.CandleCenterX(l.FirstDrawnIndex) - p / 2;
        Assert.True(oldestLeft >= l.Plot.X - 1, $"oldest drawn candle starts at {oldestLeft}, left of the plot {l.Plot.X}");
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void A_short_history_is_drawn_whole_and_anchored_right(int w, int h, double s)
    {
        var l = Layout(w, h, s, candles: 20);
        Assert.Equal(20, l.DrawnCandleCount);
        Assert.Equal(0, l.FirstDrawnIndex);
        var expected = Right(l.Plot) - (l.RightOffsetBars + 0.5) * l.CandlePitchPx;
        Assert.True(Math.Abs(l.CandleCenterX(19) - expected) <= 1, $"newest candle at {l.CandleCenterX(19)}, expected {expected}");

        var empty = Layout(w, h, s, candles: 0);
        Assert.Equal(0, empty.DrawnCandleCount);
    }

    [Theory]
    [InlineData(5120, 1440, 1.75, 250, 310)] // spec: ~260-290 candles, ~22-24 h of 5m candles
    [InlineData(3840, 2160, 1.0, 125, 165)]  // spec: ~135-150 candles, ~11-12 h
    public void Owner_screens_show_the_spec_history_span(int w, int h, double s, int min, int max)
    {
        Assert.InRange(Layout(w, h, s).CandleSlots, min, max);
    }

    // ---- arguments ----------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 1440, 1.0, 10)]
    [InlineData(5120, 0, 1.0, 10)]
    [InlineData(-5120, 1440, 1.0, 10)]
    [InlineData(5120, 1440, 0.0, 10)]
    [InlineData(5120, 1440, -1.0, 10)]
    [InlineData(5120, 1440, 1.0, -1)]
    public void Invalid_arguments_are_refused(int w, int h, double s, int candles)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LayoutEngine.Compute(w, h, s, candles));
    }

    private static IEnumerable<(string, RectPx)> Rects(ChartLayout l) =>
    [
        ("Plot", l.Plot), ("PriceAxis", l.PriceAxis), ("TimeAxis", l.TimeAxis),
        ("VolumePane", l.VolumePane), ("Headline", l.Headline),
    ];
}
