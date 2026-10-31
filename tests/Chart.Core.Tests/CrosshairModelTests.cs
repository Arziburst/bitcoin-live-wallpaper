// Contract #41 (D-35), criterion 1: the pure crosshair model.
//
// API SKETCH (written before the code, #237). Namespace BitcoinWallpaper.Chart.Core.Crosshair, src/Chart.Core/Crosshair/.
//
//   // A cursor position in screen px (origin top-left of the wallpaper's monitor, like ChartLayout).
//   public readonly record struct CursorPx(double X, double Y);
//
//   // A shown crosshair. Hidden is "null" (Compute returns null).
//   //   X           the centre of the candle's slot (ChartLayout.CandleCenterX(CandleIndex)); for the candle cut by the
//   //               plot's left edge (index FirstDrawnIndex - 1) whose centre lies left of the plot: Plot.X.
//   //   Y           the cursor's y, unchanged.
//   //   Price       the price at Y (inverse of the price scale: priceMax at Plot.Y, priceMin at Plot.Bottom); null when
//   //               the cursor is over the volume pane (the vertical line and the candle are shown, no horizontal line).
//   //   CandleIndex the index into the candles list (0 = oldest); Candle = candles[CandleIndex].
//   //   OverVolume  the cursor is in the volume pane.
//   public sealed record CrosshairState(double X, double Y, double? Price, int CandleIndex, Candle Candle, bool OverVolume);
//
//   public static class CrosshairModel
//   {
//       // Hidden (null) when: paused; the cursor is not finite; candles is empty; the cursor is outside
//       // Plot (x in [Plot.X, Plot.Right), y in [Plot.Y, Plot.Bottom)) and outside VolumePane (same x, y in
//       // [VolumePane.Y, VolumePane.Bottom)) - so the price-axis column, the time axis, the gap between plot and
//       // volume pane, the margins; or no candle exists under the cursor. Slots are half-open: slot i spans
//       // [CandleCenterX(i) - pitch/2, CandleCenterX(i) + pitch/2). Candles considered: FirstDrawnIndex - 1 (if >= 0, the
//       // one cut by the left edge) .. candles.Count - 1. candles.Count == layout.CandleCount.
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

    private static double MidPlotY(ChartLayout l) => l.Plot.Y + (l.Plot.Height / 2);

    private static ChartLayout Layout(int candles) => LayoutEngine.Compute(2560, 1440, 1.0, candles);

    [Fact]
    public void Over_a_candle_the_line_snaps_to_its_slot_centre_and_carries_the_candle()
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
                Assert.Equal(l.CandleCenterX(i), s.X, 6);
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
        Assert.False(s.Candle.IsClosed);
        Assert.Equal(l.Plot.Right - (l.CandlePitchPx / 2), s.X, 6);
    }

    [Fact]
    public void Only_existing_candles_are_hit_left_of_the_oldest_one()
    {
        var c = Candles(10); // far fewer than the slots: the series hugs the right edge
        var l = Layout(c.Count);
        Assert.Equal(0, l.FirstDrawnIndex);
        var oldestLeft = l.CandleCenterX(0) - (l.CandlePitchPx / 2);
        Assert.True(oldestLeft > l.Plot.X + (5 * l.CandlePitchPx));
        Assert.Null(At(l, c, oldestLeft - 0.5, MidPlotY(l)));
        Assert.Null(At(l, c, l.Plot.X + 1, MidPlotY(l)));
        Assert.Null(At(l, c, l.Plot.X + 1, l.VolumePane.Y + 1));
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
        Assert.Equal(Math.Max(l.CandleCenterX(s.CandleIndex), l.Plot.X), s.X, 6);

        // Just right of the cut slot is the first fully drawn candle.
        Assert.Equal(l.FirstDrawnIndex, At(l, c, firstLeft + 0.5, MidPlotY(l))!.CandleIndex);
        // Left of the plot: nothing.
        Assert.Null(At(l, c, l.Plot.X - 0.5, MidPlotY(l)));
    }

    [Fact]
    public void In_the_price_axis_column_it_is_hidden_from_the_plot_edge_on()
    {
        var c = Candles(100);
        var l = Layout(c.Count);
        Assert.NotNull(At(l, c, l.Plot.Right - 1e-6, MidPlotY(l)));
        Assert.Null(At(l, c, l.Plot.Right, MidPlotY(l)));
        Assert.Null(At(l, c, l.PriceAxis.X + (l.PriceAxis.Width / 2), MidPlotY(l)));
        Assert.Null(At(l, c, l.WidthPx - 1, MidPlotY(l)));
        Assert.Null(At(l, c, l.Plot.Right + 1, l.VolumePane.Y + 1));
    }

    [Fact]
    public void Margins_the_time_axis_and_the_gap_between_plot_and_volume_pane_are_hidden()
    {
        var c = Candles(100);
        var l = Layout(c.Count);
        var x = l.CandleCenterX(c.Count - 5);
        Assert.True(l.Plot.Y > 0);
        Assert.Null(At(l, c, x, l.Plot.Y - 1));
        Assert.Null(At(l, c, x, 0));
        Assert.True(l.VolumePane.Y > l.Plot.Bottom);
        Assert.Null(At(l, c, x, (l.Plot.Bottom + l.VolumePane.Y) / 2));
        Assert.NotNull(At(l, c, x, l.Plot.Y));
        Assert.NotNull(At(l, c, x, l.Plot.Bottom - 1e-6));
        Assert.Null(At(l, c, x, l.Plot.Bottom));
        Assert.NotNull(At(l, c, x, l.VolumePane.Y));
        Assert.Null(At(l, c, x, l.VolumePane.Bottom));
        Assert.Null(At(l, c, x, l.TimeAxis.Y + (l.TimeAxis.Height / 2)));
        Assert.Null(At(l, c, x, l.HeightPx - 1));
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
        Assert.Equal(l.CandleCenterX(60), s.X, 6);
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
    public void A_cursor_that_is_not_on_screen_is_hidden(double x, double y)
    {
        var c = Candles(100);
        var l = Layout(c.Count);
        Assert.Null(At(l, c, x, y));
    }

    [Fact]
    public void No_candles_no_crosshair()
    {
        var l = Layout(0);
        Assert.Null(At(l, new List<Candle>(), l.Plot.X + 100, MidPlotY(l)));
    }

    [Fact]
    public void Same_cursor_same_answer()
    {
        var c = Candles(100);
        var l = Layout(c.Count);
        Assert.Equal(At(l, c, 1234.5, 700.25), At(l, c, 1234.5, 700.25));
    }
}
