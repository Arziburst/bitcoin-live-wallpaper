// Contract #4, decision D-11: a 24h high or low outside the visible price range is shown as an edge marker on the
// price scale ("24h high ↑ <price>" at the plot's top edge, "24h low ↓ <price>" at its bottom edge); the scale is
// not stretched for it.
//
// Access point (named by the coordinator, implemented by the worker):
//   DayRangeMarkers.For(ChartFrame) -> IReadOnlyList<DayRangeMarker(Extreme, Price, Text, Bounds: RectPx)>
// Everything else is observed from outside: the recorded fixture frames with synthetic 24h tickers, and the
// rendered pixels.
//
// "The visible price range" here is the visible candles (index FirstDrawnIndex onward) and the MA20 / MA50 drawn
// over them. A price inside it is always on the scale; a price three ranges beyond it is always off the scale.

using System.Globalization;
using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Indicators;
using BitcoinWallpaper.Chart.Core.Layout;
using BitcoinWallpaper.Chart.Render.D2D;

namespace BitcoinWallpaper.Chart.Render.Tests;

[Collection(nameof(RendererCollection))]
public class EdgeMarkerTests(RendererFixture fx)
{
    private const string Desk = "btc-5120x1440.png";
    private const string Tv = "btc-3840x2160.png";

    // ---- which markers ------------------------------------------------------------------------

    [Theory]
    [InlineData(Desk)]
    [InlineData(Tv)]
    public void No_marker_when_both_extremes_are_on_the_scale(string fileName)
    {
        var frame = fx.Frame(fileName);
        var (hi, lo) = VisibleRange(frame);
        var close = frame.Candles[^1].Close;

        Assert.Empty(DayRangeMarkers.For(WithDay(frame, hi, lo)));
        Assert.Empty(DayRangeMarkers.For(WithDay(frame, close, close)));
    }

    [Theory]
    [InlineData(Desk)]
    [InlineData(Tv)]
    public void One_marker_for_each_off_scale_extreme_with_its_text_and_price(string fileName)
    {
        var frame = fx.Frame(fileName);
        var (hi, lo) = VisibleRange(frame);
        var range = hi - lo;
        var farHigh = Round(hi + (3 * range));
        var farLow = Round(lo - (3 * range));

        var both = DayRangeMarkers.For(WithDay(frame, farHigh, farLow));
        Assert.Equal(2, both.Count);
        AssertMarker(Assert.Single(both, m => m.Extreme == DayExtreme.High), DayExtreme.High, farHigh);
        AssertMarker(Assert.Single(both, m => m.Extreme == DayExtreme.Low), DayExtreme.Low, farLow);

        var highOnly = DayRangeMarkers.For(WithDay(frame, farHigh, lo));
        AssertMarker(Assert.Single(highOnly), DayExtreme.High, farHigh);

        var lowOnly = DayRangeMarkers.For(WithDay(frame, hi, farLow));
        AssertMarker(Assert.Single(lowOnly), DayExtreme.Low, farLow);
    }

    [Theory]
    [InlineData(Desk)]
    [InlineData(Tv)]
    public void An_extreme_just_beyond_the_visible_range_gets_a_marker_too(string fileName)
    {
        // A quarter of the visible range below the lowest visible price: close to the candles, still off the scale.
        // A scale that stretched for "near" extremes would show it as a line and give no marker.
        var frame = fx.Frame(fileName);
        var (hi, lo) = VisibleRange(frame);
        var nearLow = Round(lo - (0.25m * (hi - lo)));

        AssertMarker(Assert.Single(DayRangeMarkers.For(WithDay(frame, hi, nearLow))), DayExtreme.Low, nearLow);
    }

    // ---- where the markers are ----------------------------------------------------------------

    [Theory]
    [InlineData(Desk)]
    [InlineData(Tv)]
    public void The_high_sits_at_the_top_edge_and_the_low_at_the_bottom_edge_of_the_plot(string fileName)
    {
        var frame = fx.Frame(fileName);
        var plot = frame.Layout.Plot;
        var markers = DayRangeMarkers.For(OffScale(frame));

        var high = Assert.Single(markers, m => m.Extreme == DayExtreme.High).Bounds;
        Assert.True(high.Y >= plot.Y && high.Y - plot.Y < high.Height,
            $"the 24h high marker {high} is not at the plot's top edge {plot}");

        var low = Assert.Single(markers, m => m.Extreme == DayExtreme.Low).Bounds;
        Assert.True(low.Bottom <= plot.Bottom && plot.Bottom - low.Bottom < low.Height,
            $"the 24h low marker {low} is not at the plot's bottom edge {plot}");
    }

    // D-20 (#18): was "..._and_outside_the_icon_safe_area" with the assertion b.X >= IconSafeArea.Right, removed:
    // there is no icon safe area any more.
    [Theory]
    [InlineData(Desk)]
    [InlineData(Tv)]
    public void Marker_bounds_are_inside_the_plot(string fileName)
    {
        var frame = fx.Frame(fileName);
        var plot = frame.Layout.Plot;
        var markers = DayRangeMarkers.For(OffScale(frame));
        Assert.Equal(2, markers.Count);

        foreach (var marker in markers)
        {
            var b = marker.Bounds;
            Assert.True(b.Width > 0 && b.Height > 0, $"{marker.Extreme} marker has empty bounds {b}");
            Assert.True(b.X >= plot.X && b.Right <= plot.Right && b.Y >= plot.Y && b.Bottom <= plot.Bottom,
                $"{marker.Extreme} marker {b} is not inside the plot {plot}");
        }
    }

    // D-20 (#18): replaces "With_off_scale_markers_the_icon_safe_area_is_still_bare_background". The low marker
    // (bottom-left, never under the headline) sits at the plot's left margin, inside the former 8 % icon area;
    // only the margin left of the plot stays bare.
    [Theory]
    [InlineData(Desk)]
    [InlineData(Tv)]
    public void With_off_scale_markers_the_low_marker_sits_at_the_left_margin_and_left_of_the_plot_is_bare(string fileName)
    {
        var frame = OffScale(fx.Frame(fileName));
        var w = frame.Layout.WidthPx;
        var low = Assert.Single(DayRangeMarkers.For(frame), m => m.Extreme == DayExtreme.Low).Bounds;
        Assert.True(low.X < 0.08 * w, $"the 24h low marker {low} starts right of the former icon safe area (x >= {0.08 * w})");

        var full = fx.Renderer.Render(frame);
        var empty = fx.Renderer.Render(ChartFrame.Create(
            frame.Layout.WidthPx, frame.Layout.HeightPx, frame.Layout.DpiScale, [], null, frame.Now, frame.TimeZone));

        var plot = frame.Layout.Plot;
        var differing = FirstDiffering(full, empty, (x, _) => x < Math.Floor(plot.X));
        Assert.True(differing is null, $"pixel {differing} left of the plot {plot} differs from the bare background");
    }

    [Theory]
    [InlineData(Desk)]
    [InlineData(Tv)]
    public void The_marker_is_drawn_inside_its_bounds(string fileName)
    {
        // Same candles, so the same scale: inside a marker's bounds the off-scale render shows the marker's text,
        // the on-scale render does not.
        var frame = fx.Frame(fileName);
        var close = frame.Candles[^1].Close;
        var off = OffScale(frame);
        var withMarkers = fx.Renderer.Render(off);
        var without = fx.Renderer.Render(WithDay(frame, close, close));

        foreach (var marker in DayRangeMarkers.For(off))
        {
            var b = marker.Bounds;
            var differing = FirstDiffering(withMarkers, without, (x, y) => Inside(b, x, y));
            Assert.True(differing is not null, $"nothing is drawn inside the {marker.Extreme} marker's bounds {b}");
        }
    }

    // ---- the scale is not stretched -----------------------------------------------------------

    [Theory]
    [InlineData(Desk)]
    [InlineData(Tv)]
    public void Near_or_far_off_scale_extremes_draw_the_same_chart_outside_headline_and_markers(string fileName)
    {
        // Two tickers whose extremes are both off the scale: one just beyond the visible range, one far beyond.
        // An unstretched scale draws the same candles, averages, grid, price axis and last-price line for both;
        // only the headline's 24h numbers and the markers' text differ. A scale that stretched for either one
        // moves every candle.
        var frame = fx.Frame(fileName);
        var (hi, lo) = VisibleRange(frame);
        var range = hi - lo;
        var near = WithDay(frame, Round(hi + (3 * range)), Round(lo - (0.25m * range)));
        var far = WithDay(frame, Round(hi + (5 * range)), Round(lo - (4 * range)));

        var nearMarkers = DayRangeMarkers.For(near);
        var farMarkers = DayRangeMarkers.For(far);
        Assert.Equal(2, nearMarkers.Count);
        Assert.Equal(2, farMarkers.Count);

        var a = fx.Renderer.Render(near);
        var b = fx.Renderer.Render(far);
        var headline = frame.Layout.Headline;
        var masked = nearMarkers.Concat(farMarkers).Select(m => m.Bounds).Append(headline).ToArray();
        var differing = FirstDiffering(a, b, (x, y) => !masked.Any(r => Inside(r, x, y)));
        Assert.True(differing is null,
            $"pixel {differing} outside the headline and the markers changed with an off-scale 24h extreme: the price scale moved");
    }

    [Theory]
    [InlineData(Desk)]
    [InlineData(Tv)]
    public void The_scale_is_the_same_with_and_without_an_off_scale_extreme(string fileName)
    {
        // Without: the 24h high and low both at the last close (on the scale): one dotted line with its two
        // labels, its axis tags hidden under the last-price tag. With: both far off the scale, two edge markers.
        // Outside the headline and the markers the renders may differ only in that one dotted-line band; a
        // stretched scale moves every candle and the last-price line.
        var frame = fx.Frame(fileName);
        var close = frame.Candles[^1].Close;
        var without = WithDay(frame, close, close);
        var with = OffScale(frame);
        var markers = DayRangeMarkers.For(with);
        Assert.Empty(DayRangeMarkers.For(without));
        Assert.Equal(2, markers.Count);

        var a = fx.Renderer.Render(without);
        var b = fx.Renderer.Render(with);
        var masked = markers.Select(m => m.Bounds).Append(frame.Layout.Headline).ToArray();
        var (top, bottom) = DifferingRows(a, b, (x, y) => !masked.Any(r => Inside(r, x, y)));

        var band = 3 * frame.Layout.Text.AxisLabelPx;
        Assert.True(top is null || bottom - top <= band,
            $"rows {top}..{bottom} differ outside the headline and the markers (more than the {band:0} px of one " +
            "dotted 24h line and its labels): the price scale moved with the off-scale 24h extreme");
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static void AssertMarker(DayRangeMarker marker, DayExtreme extreme, decimal price)
    {
        Assert.Equal(extreme, marker.Extreme);
        Assert.Equal(price, marker.Price);
        var shown = price.ToString("#,##0.00", CultureInfo.InvariantCulture);
        var expected = extreme == DayExtreme.High ? $"24h high ↑ {shown}" : $"24h low ↓ {shown}";
        Assert.Equal(expected, marker.Text);
    }

    // The recorded frame with its ticker's 24h high and low replaced; nothing else changes.
    private static ChartFrame WithDay(ChartFrame frame, decimal high, decimal low)
    {
        Assert.NotNull(frame.Ticker);
        return frame with { Ticker = frame.Ticker with { High = high, Low = low } };
    }

    private static ChartFrame OffScale(ChartFrame frame)
    {
        var (hi, lo) = VisibleRange(frame);
        var range = hi - lo;
        return WithDay(frame, Round(hi + (3 * range)), Round(lo - (3 * range)));
    }

    // Highest and lowest price of the visible candles and the averages drawn over them.
    private static (decimal Hi, decimal Lo) VisibleRange(ChartFrame frame)
    {
        var candles = frame.Candles;
        var ma20 = MovingAverage.Simple(candles, 20);
        var ma50 = MovingAverage.Simple(candles, 50);
        decimal hi = decimal.MinValue, lo = decimal.MaxValue;
        for (var i = frame.Layout.FirstDrawnIndex; i < candles.Count; i++)
        {
            hi = Math.Max(hi, candles[i].High);
            lo = Math.Min(lo, candles[i].Low);
            foreach (var v in new[] { ma20[i], ma50[i] })
            {
                if (v is { } p)
                {
                    hi = Math.Max(hi, p);
                    lo = Math.Min(lo, p);
                }
            }
        }

        Assert.True(hi > lo && lo - (4 * (hi - lo)) > 0, $"fixture range {lo}..{hi} is unusable for these tests");
        return (hi, lo);
    }

    private static decimal Round(decimal price) => Math.Round(price, 2);

    // A pixel is inside a rectangle when any part of it overlaps the rectangle (antialiased edges included).
    private static bool Inside(RectPx r, int x, int y) =>
        x + 1 > r.X && x < r.Right && y + 1 > r.Y && y < r.Bottom;

    private static (int X, int Y)? FirstDiffering(BgraImage a, BgraImage b, Func<int, int, bool> region)
    {
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                if (region(x, y) && Differs(a, b, x, y))
                {
                    return (x, y);
                }
            }
        }

        return null;
    }

    // The first and last row with a differing pixel inside the region, or (null, null).
    private static (int? Top, int? Bottom) DifferingRows(BgraImage a, BgraImage b, Func<int, int, bool> region)
    {
        int? top = null, bottom = null;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                if (region(x, y) && Differs(a, b, x, y))
                {
                    top ??= y;
                    bottom = y;
                    break;
                }
            }
        }

        return (top, bottom);
    }

    private static bool Differs(BgraImage a, BgraImage b, int x, int y)
    {
        var i = ((y * a.Width) + x) * 4;
        return a.Pixels[i] != b.Pixels[i] || a.Pixels[i + 1] != b.Pixels[i + 1] || a.Pixels[i + 2] != b.Pixels[i + 2];
    }
}
