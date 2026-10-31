// Contract #41 (D-35), criterion 1: the pure crosshair model.
//
// API SKETCH (written before the code, #237). Namespace BitcoinWallpaper.Chart.Core.Crosshair, src/Chart.Core/Crosshair/.
//
//   // A cursor position in screen px (origin top-left of the wallpaper's monitor, like ChartLayout).
//   public readonly record struct CursorPx(double X, double Y);
//   // A shown crosshair (round 6: a WHOLE-MONITOR crosshair; hidden is "null", Compute returns null). Both lines run edge to
//   // edge through the cursor, wherever it is on the screen; the chart features below are null / false where they do not apply.
//   //   X           the cursor x, exactly (no snapping, round 2).
//   //   Y           the cursor's y, unchanged.
//   //   Price       the price at Y (inverse of the price scale: priceMax at Plot.Y, priceMin at Plot.Bottom) when Y is in the
//   //               plot, [Plot.Y, Plot.Bottom), for ANY x on the screen (also in the price-axis column); otherwise null.
//   //   CandleIndex the index into the candles list (0 = oldest) of the candle whose half-open slot holds X (slot i spans
//   //               [CandleCenterX(i) - pitch/2, CandleCenterX(i) + pitch/2); considered: FirstDrawnIndex - 1 (if >= 0, the
//   //               one cut by the left edge) .. Count - 1), for ANY y on the screen; null when X is not over an existing slot
//   //               (left of the oldest candle, in the price-axis column, no candles).
//   //   Candle      candles[CandleIndex], null exactly when CandleIndex is null.
//   //   OverVolume  Y is in the volume pane [VolumePane.Y, VolumePane.Bottom).
//   //   HighlightedIndices (init-only, default empty) the indices, ascending, of the candles a crosshair line crosses, which
//   //               the renderer draws slightly brighter: (a) when Price is not null, every candle of FirstDrawnIndex-1 (if >= 0)
//   //               .. Count-1 with Low <= Price <= High (the horizontal line spans the screen); (b) the slot's candle when
//   //               |X - CandleCenterX(CandleIndex)| <= CandleBodyWidthPx/2 + 1 (the vertical line crosses its body or wick;
//   //               independent of Y).
//   //   OnCandle    (init-only, default false) the cursor is ON the slot's candle: Price and Candle not null and either inside
//   //               its body rect (|X - center| <= CandleBodyWidthPx/2, y between the y of Open and of Close) or on its wick
//   //               (|X - center| <= WickWidthPx/2 + 2, y between the y of High and of Low). The y of a price is
//   //               Plot.Y + (priceMax - price) / (priceMax - priceMin) * Plot.Height.
//   public sealed record CrosshairState(double X, double Y, double? Price, int? CandleIndex, Candle? Candle, bool OverVolume)
//   { public IReadOnlyList<int> HighlightedIndices { get; init; } = []; public bool OnCandle { get; init; } }
//
//   public static class CrosshairModel
//   {

//       // y outside [0, HeightPx)). Anything on the screen gives a state (possibly with null Price / Candle). An empty
//       // candles list gives a state with no Candle. candles.Count == layout.CandleCount.
//       // priceMax/priceMin: the price scale of the plot (Plot.Y <-> priceMax, Plot.Bottom <-> priceMin).
//       public static CrosshairState? Compute(CursorPx cursor, ChartLayout layout, IReadOnlyList<Candle> candles,
//                                             double priceMax, double priceMin, bool paused);
//
//       // Linear, decreasing in y: PriceAt(plot.Y) == priceMax, PriceAt(plot.Bottom) == priceMin.
//       public static double PriceAt(double y, RectPx plot, double priceMax, double priceMin);
//   }

using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Crosshair;
using BitcoinWallpaper.Chart.Core.Layout;

namespace BitcoinWallpaper.Chart.Core.Tests;

public class CrosshairModelTests
{
    private const double Max = 70000, Min = 60000;

    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 8, 0, 0, TimeSpan.Zero);

    private static List<Candle> Candles(int n) =>
        Enumerable.Range(0, n)
            .Select(i => new Candle(T0.AddMinutes(5 * i), 65000 + i, 65100 + i, 64900 + i, 65050 + i, 10 + i, i < n - 1))
            .ToList();

    private static CrosshairState? At(ChartLayout l, IReadOnlyList<Candle> c, double x, double y, bool paused = false) =>
        CrosshairModel.Compute(new CursorPx(x, y), l, c, Max, Min, paused);

    // On the screen but over no candle slot (round 6): the lines still show, there is no candle.
    private static void NoCandle(CrosshairState? s)
    {
        Assert.NotNull(s);
        Assert.Null(s.CandleIndex);
        Assert.Null(s.Candle);
        Assert.False(s.OnCandle);
    }

    private static double MidPlotY(ChartLayout l) => l.Plot.Y + (l.Plot.Height / 2);

    private static ChartLayout Layout(int candles) => LayoutEngine.Compute(2560, 1440, 1.0, candles);

    [Fact]
    public void Over_a_candle_the_line_follows_the_cursor_x_exactly_and_the_candle_is_the_slot_s()
    {
        var c = Candles(100);
        var l = Layout(c.Count);
        foreach (var i in new[] { l.FirstDrawnIndex, l.FirstDrawnIndex + 17, c.Count - 2, c.Count - 1 })
        {
            foreach (var dx in new[] { -0.4 * l.CandlePitchPx, 0, 0.4 * l.CandlePitchPx })
            {
                var s = At(l, c, l.CandleCenterX(i) + dx, MidPlotY(l));
                Assert.NotNull(s);
                Assert.Equal(i, s.CandleIndex);
                Assert.Same(c[i], s.Candle);
                Assert.Equal(l.CandleCenterX(i) + dx, s.X, 9); // no snapping
                Assert.Equal(MidPlotY(l), s.Y, 6);
                Assert.False(s.OverVolume);
                Assert.NotNull(s.Price);
            }
        }
    }

    [Fact]
    public void Slots_are_half_open_at_their_boundaries()
    {
        var c = Candles(100);
        var l = Layout(c.Count);
        var i = l.FirstDrawnIndex + 20;
        var edge = l.CandleCenterX(i) + (l.CandlePitchPx / 2); // the boundary between slot i and i + 1
        Assert.Equal(i, At(l, c, edge - 1e-6, MidPlotY(l))!.CandleIndex);
        Assert.Equal(i + 1, At(l, c, edge, MidPlotY(l))!.CandleIndex);
        Assert.Equal(i + 1, At(l, c, edge + 1e-6, MidPlotY(l))!.CandleIndex);
    }

    [Fact]
    public void The_newest_forming_candle_is_hit_at_the_right_end_of_the_plot()
    {
        var c = Candles(100);
        var l = Layout(c.Count);
        Assert.False(c[^1].IsClosed);
        var s = At(l, c, l.Plot.Right - 0.5, MidPlotY(l));
        Assert.NotNull(s);
        Assert.Equal(c.Count - 1, s.CandleIndex);
        Assert.False(s.Candle!.IsClosed);
        Assert.Equal(l.Plot.Right - 0.5, s.X, 9);
    }

    [Fact]
    public void Only_existing_candles_are_hit_left_of_the_oldest_one()
    {
        var c = Candles(10); // far fewer than the slots: the series hugs the right edge
        var l = Layout(c.Count);
        Assert.Equal(0, l.FirstDrawnIndex);
        var oldestLeft = l.CandleCenterX(0) - (l.CandlePitchPx / 2);
        Assert.True(oldestLeft > l.Plot.X + (5 * l.CandlePitchPx));
        NoCandle(At(l, c, oldestLeft - 0.5, MidPlotY(l)));
        NoCandle(At(l, c, l.Plot.X + 1, MidPlotY(l)));
        NoCandle(At(l, c, l.Plot.X + 1, l.VolumePane.Y + 1));
        Assert.Equal(0, At(l, c, oldestLeft, MidPlotY(l))!.CandleIndex);
    }

    [Fact]
    public void The_candle_cut_by_the_left_edge_is_hit_and_its_line_stays_on_the_plot()
    {
        // A width where the plot is not a whole number of slots, so a candle is cut at x = 0.
        ChartLayout? l = null;
        for (var w = 2400; w < 2500 && l is null; w++)
        {
            var probe = LayoutEngine.Compute(w, 1440, 1.0, 1000);
            var leftover = probe.Plot.Width - (probe.CandleSlots * probe.CandlePitchPx);
            if (leftover >= 4 && probe.FirstDrawnIndex > 0)
            {
                l = probe;
            }
        }

        Assert.NotNull(l);
        var c = Candles(1000);
        var firstLeft = l.CandleCenterX(l.FirstDrawnIndex) - (l.CandlePitchPx / 2);
        Assert.InRange(firstLeft, l.Plot.X + 4, l.Plot.X + l.CandlePitchPx);
        var s = At(l, c, l.Plot.X + (firstLeft / 2), MidPlotY(l));
        Assert.NotNull(s);
        Assert.Equal(l.FirstDrawnIndex - 1, s.CandleIndex);
        Assert.Same(c[l.FirstDrawnIndex - 1], s.Candle);
        Assert.InRange(s.X, l.Plot.X, l.Plot.Right);
        Assert.Equal(l.Plot.X + (firstLeft / 2), s.X, 9);

        // Just right of the cut slot is the first fully drawn candle.
        Assert.Equal(l.FirstDrawnIndex, At(l, c, firstLeft + 0.5, MidPlotY(l))!.CandleIndex);
        // Left of the plot: nothing.
        Assert.Null(At(l, c, l.Plot.X - 0.5, MidPlotY(l)));
    }

    [Fact]
    public void In_the_price_axis_column_the_lines_and_the_price_stay_but_there_is_no_candle()
    {
        var c = Candles(100);
        var l = Layout(c.Count);
        Assert.NotNull(At(l, c, l.Plot.Right - 1e-6, MidPlotY(l))!.Candle);
        foreach (var x in new[] { l.Plot.Right, l.PriceAxis.X + (l.PriceAxis.Width / 2), l.WidthPx - 1 })
        {
            var s = At(l, c, x, MidPlotY(l));
            NoCandle(s);
            Assert.Equal(x, s!.X, 9);
            Assert.NotNull(s.Price); // y is inside the plot: the price tag shows
            Assert.Equal(CrosshairModel.PriceAt(MidPlotY(l), l.Plot, Max, Min), s.Price!.Value, 9);
            Assert.False(s.OnCandle);
        }

        // The price rule reaches across the whole screen: a candle whose range holds the price is lit from the axis column too.
        var lit = Tight(l, c, l.Plot.Right + 10, YOf(l, 65150))!;
        Assert.Equal(Enumerable.Range(50, 50), lit.HighlightedIndices);
        Assert.Null(At(l, c, l.WidthPx, MidPlotY(l))); // the screen's right edge is outside
    }

    [Fact]
    public void Everywhere_on_the_screen_there_are_lines_margins_gaps_and_axes_give_no_price()
    {
        var c = Candles(100);
        var l = Layout(c.Count);
        var x = l.CandleCenterX(c.Count - 5);

        // No price (and not OverVolume) in the margins, the gap between plot and volume pane and the time axis, but the
        // candle under x is known: the time tag shows.
        foreach (var y in new[] { 0.0, l.Plot.Y - 1, (l.Plot.Bottom + l.VolumePane.Y) / 2, l.TimeAxis.Y + (l.TimeAxis.Height / 2), l.HeightPx - 1.0 })
        {
            var s = At(l, c, x, y);
            Assert.NotNull(s);
            Assert.Null(s.Price);
            Assert.False(s.OverVolume);
            Assert.Equal(c.Count - 5, s.CandleIndex);
            Assert.Equal(y, s.Y, 9);
            Assert.False(s.OnCandle);
        }

        // Pane edges: half-open.
        Assert.NotNull(At(l, c, x, l.Plot.Y)!.Price);
        Assert.NotNull(At(l, c, x, l.Plot.Bottom - 1e-6)!.Price);
        Assert.Null(At(l, c, x, l.Plot.Bottom)!.Price);
        Assert.True(At(l, c, x, l.VolumePane.Y)!.OverVolume);
        Assert.False(At(l, c, x, l.VolumePane.Bottom)!.OverVolume);
        Assert.False(At(l, c, x, l.Plot.Bottom)!.OverVolume);

        // Both coordinates off the plot: lines only.
        var corner = At(l, c, l.WidthPx - 1, l.HeightPx - 1);
        NoCandle(corner);
        Assert.Null(corner!.Price);
        Assert.Empty(corner.HighlightedIndices);
    }

    [Fact]
    public void Over_a_headline_or_margin_the_candle_under_x_is_still_known()
    {
        var c = Candles(100);
        var l = Layout(c.Count);
        var s = At(l, c, l.CandleCenterX(60), l.Headline.Y + 2)!;
        Assert.Equal(60, s.CandleIndex);
        Assert.NotNull(s.Price); // inside the plot
        Assert.Equal([60], Tight(l, c, l.CandleCenterX(60), 2)!.HighlightedIndices); // top margin: only the vertical rule
    }


    [Fact]
    public void Paused_hides_it_everywhere()
    {
        var c = Candles(100);
        var l = Layout(c.Count);
        Assert.NotNull(At(l, c, l.CandleCenterX(50), MidPlotY(l), paused: false));
        Assert.Null(At(l, c, l.CandleCenterX(50), MidPlotY(l), paused: true));
        Assert.Null(At(l, c, l.CandleCenterX(50), l.VolumePane.Y + 2, paused: true));
    }

    [Fact]
    public void Over_the_volume_pane_the_vertical_line_and_the_candle_stay_but_no_price()
    {
        var c = Candles(100);
        var l = Layout(c.Count);
        var y = l.VolumePane.Y + (l.VolumePane.Height / 2);
        var s = At(l, c, l.CandleCenterX(60) + 2, y);
        Assert.NotNull(s);
        Assert.True(s.OverVolume);
        Assert.Null(s.Price);
        Assert.Equal(60, s.CandleIndex);
        Assert.Equal(l.CandleCenterX(60) + 2, s.X, 9);
        Assert.Equal(y, s.Y, 6);
    }

    [Fact]
    public void Price_is_the_inverse_of_the_scale_exact_at_both_ends_and_monotonic()
    {
        var c = Candles(100);
        var l = Layout(c.Count);
        var x = l.CandleCenterX(70);
        Assert.Equal(Max, CrosshairModel.PriceAt(l.Plot.Y, l.Plot, Max, Min), 9);
        Assert.Equal(Min, CrosshairModel.PriceAt(l.Plot.Bottom, l.Plot, Max, Min), 9);
        Assert.Equal((Max + Min) / 2, CrosshairModel.PriceAt(l.Plot.Y + (l.Plot.Height / 2), l.Plot, Max, Min), 9);
        Assert.Equal(Max, At(l, c, x, l.Plot.Y)!.Price!.Value, 9);

        var last = double.PositiveInfinity;
        for (var y = l.Plot.Y; y < l.Plot.Bottom; y += 7.3)
        {
            var p = At(l, c, x, y)!.Price!.Value;
            Assert.True(p < last, $"price {p} at y {y} is not below {last}");
            Assert.InRange(p, Min, Max);
            Assert.Equal(Max - ((y - l.Plot.Y) / l.Plot.Height * (Max - Min)), p, 6);
            last = p;
        }
    }

    [Theory]
    [InlineData(double.NaN, 100)]
    [InlineData(100, double.NaN)]
    [InlineData(double.PositiveInfinity, 100)]
    [InlineData(-5, -5)]
    [InlineData(1e9, 100)]
    [InlineData(100, 1e9)]
    [InlineData(-0.5, 100)]
    public void A_cursor_that_is_not_on_screen_is_hidden(double x, double y)
    {
        var c = Candles(100);
        var l = Layout(c.Count);
        Assert.Null(At(l, c, x, y));
    }

    [Fact]
    public void No_candles_no_candle_but_the_lines_and_price_stay()
    {
        var l = Layout(0);
        var s = At(l, new List<Candle>(), l.Plot.X + 100, MidPlotY(l));
        NoCandle(s);
        Assert.NotNull(s!.Price);
    }

    [Fact]
    public void Same_cursor_same_answer()
    {
        var c = Candles(100);
        var l = Layout(c.Count);
        var p = At(l, c, 1234.5, 700.25)!;
        var q = At(l, c, 1234.5, 700.25)!;
        Assert.Equal((p.X, p.Y, p.Price, p.CandleIndex, p.OnCandle, p.OverVolume), (q.X, q.Y, q.Price, q.CandleIndex, q.OnCandle, q.OverVolume));
        Assert.Equal(p.HighlightedIndices, q.HighlightedIndices);
    }

    // ---- round 3: crossed candles and "on a candle" -----------------------------------------------------------

    // A tight scale (about 1 unit per px) so bodies and wicks have real sizes: candle i has Open 65000+i, Close 65050+i,
    // High 65100+i, Low 64900+i.
    private const double TMax = 65500, TMin = 64500;

    private static double YOf(ChartLayout l, double price) => l.Plot.Y + ((TMax - price) / (TMax - TMin) * l.Plot.Height);

    private static CrosshairState? Tight(ChartLayout l, IReadOnlyList<Candle> c, double x, double y) =>
        CrosshairModel.Compute(new CursorPx(x, y), l, c, TMax, TMin, false);

    [Fact]
    public void The_horizontal_line_lights_every_candle_whose_range_holds_the_cursor_price_over_the_whole_plot()
    {
        var c = Candles(100);
        var l = Layout(c.Count);
        Assert.Equal(0, l.FirstDrawnIndex);
        // Price 65150: Low (64900+i) <= 65150 for all; High (65100+i) >= 65150 for i >= 50. Cursor over candle 10 (not lit by price),
        // far from the lit ones: x is in slot 10, outside its body width + 1.
        var x = l.CandleCenterX(10) + (l.CandleBodyWidthPx / 2) + 2.5;
        var s = Tight(l, c, x, YOf(l, 65150));
        Assert.NotNull(s);
        Assert.Equal(10, s.CandleIndex);
        Assert.Equal(Enumerable.Range(50, 50), s.HighlightedIndices);

        // Above every candle: none. Below every candle: none.
        Assert.Empty(Tight(l, c, x, YOf(l, 65400))!.HighlightedIndices);
        Assert.Empty(Tight(l, c, x, YOf(l, 64600))!.HighlightedIndices);
    }

    [Fact]
    public void The_vertical_line_lights_the_slots_candle_only_within_its_body_width_or_wick()
    {
        var c = Candles(100);
        var l = Layout(c.Count);
        var centre = l.CandleCenterX(30);
        var half = l.CandleBodyWidthPx / 2;
        var y = YOf(l, 65400); // above every candle's high (65100 + i <= 65199): no price match
        Assert.Equal([30], Tight(l, c, centre, y)!.HighlightedIndices);
        Assert.Equal([30], Tight(l, c, centre + half - 0.5, y)!.HighlightedIndices);
        Assert.Equal([30], Tight(l, c, centre - half + 0.5, y)!.HighlightedIndices);
        var outside = centre + half + 2.5;
        Assert.True(outside < centre + (l.CandlePitchPx / 2));
        var s = Tight(l, c, outside, y)!;
        Assert.Equal(30, s.CandleIndex);
        Assert.Empty(s.HighlightedIndices);
        Assert.Empty(Tight(l, c, centre - half - 2.5, y)!.HighlightedIndices);
    }

    [Fact]
    public void Both_rules_together_are_ascending_and_without_duplicates()
    {
        var c = Candles(100);
        var l = Layout(c.Count);
        // Cursor on candle 70's wick at price 65150 (inside its range 64970..65170): lit by price (50..99) and by x (70).
        var s = Tight(l, c, l.CandleCenterX(70), YOf(l, 65150))!;
        Assert.Equal(Enumerable.Range(50, 50), s.HighlightedIndices);
        // Cursor on candle 20: lit by x (20) and by price (50..99).
        var t = Tight(l, c, l.CandleCenterX(20), YOf(l, 65150))!;
        Assert.Equal(new[] { 20 }.Concat(Enumerable.Range(50, 50)), t.HighlightedIndices);
    }

    [Fact]
    public void Over_the_volume_pane_only_the_vertical_rule_applies()
    {
        var c = Candles(100);
        var l = Layout(c.Count);
        var y = l.VolumePane.Y + (l.VolumePane.Height / 2);
        var on = Tight(l, c, l.CandleCenterX(40), y)!;
        Assert.True(on.OverVolume);
        Assert.Equal([40], on.HighlightedIndices);
        Assert.False(on.OnCandle);
        var off = Tight(l, c, l.CandleCenterX(40) + (l.CandleBodyWidthPx / 2) + 2.5, y)!;
        Assert.Empty(off.HighlightedIndices);
        Assert.False(off.OnCandle);
    }

    [Fact]
    public void OnCandle_is_true_inside_the_body_or_on_the_wick_and_false_elsewhere()
    {
        var c = Candles(100);
        var l = Layout(c.Count);
        const int i = 30; // Open 65030, Close 65080, High 65130, Low 64930
        var centre = l.CandleCenterX(i);
        var half = l.CandleBodyWidthPx / 2;
        var bodyMid = YOf(l, 65055);
        bool On(double dx, double price) => Tight(l, c, centre + dx, YOf(l, price))!.OnCandle;

        Assert.True(On(0, 65055));
        Assert.True(On(half - 0.5, 65055));
        Assert.True(On(-half + 0.5, 65055));
        Assert.False(On(half + 1.5, 65055)); // beside the body, outside the wick band
        Assert.False(On(-half - 1.5, 65055));
        Assert.True(On(0, 65031) && On(0, 65079)); // body ends
        Assert.True(On(1.5, 65110)); // upper wick, 1.5 px off the centre
        Assert.True(On(-1.5, 64950)); // lower wick
        Assert.True(On(0, 65128) && On(0, 64932)); // wick tips
        Assert.False(On(4, 65110)); // beside the wick (the body is lower), more than 2 px off
        Assert.False(On(0, 65140)); // above the high
        Assert.False(On(0, 64920)); // below the low
        Assert.True(Math.Abs(YOf(l, 65140) - YOf(l, 65130)) > 5, "the scale is too coarse for this test");
        Assert.NotNull(Tight(l, c, centre, bodyMid)!.Candle);
    }

    [Fact]
    public void OnCandle_never_for_an_axis_or_volume_cursor_and_the_default_is_empty()
    {
        var c = Candles(100);
        var l = Layout(c.Count);
        var y = l.VolumePane.Y + 3;
        Assert.False(Tight(l, c, l.CandleCenterX(40), y)!.OnCandle);
        NoCandle(Tight(l, c, l.Plot.Right + 3, YOf(l, 65055)));
        // The state of a plain record has no highlights and is not on a candle.
        var plain = new CrosshairState(1, 2, 3, 0, c[0], false);
        Assert.Empty(plain.HighlightedIndices);
        Assert.False(plain.OnCandle);
    }
}
