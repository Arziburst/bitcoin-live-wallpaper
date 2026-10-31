// Contract #41 (D-35), criterion 2: the renderer draws the crosshair on top.
//
// API SKETCH (written before the code, #237). Chart.Core has CursorPx / CrosshairState / CrosshairModel (see
// tests/Chart.Core.Tests/CrosshairModelTests.cs). In src/Chart.Render.D2D/:
//
//   public sealed record ChartFrame(...)
//   {
//       // The cursor in screen px, or null: no crosshair (and a frame pixel-identical to one before this feature).
//       // The host passes null while paused. The renderer asks CrosshairModel.Compute with the frame's own layout,
//       // candles and price scale (paused: false); a hidden answer draws nothing.
//       public CursorPx? Cursor { get; init; }
//   }
//
//   public enum CrosshairItemKind { PriceTag, TimeTag, OhlcLine }
//
//   // One thing drawn for the crosshair; Bounds in physical px (a tag: its filled box; the line: its text box).
//   public sealed record CrosshairItem(CrosshairItemKind Kind, string Text, RectPx Bounds);
//
//   public static class FrameView
//   {
//       // The model's answer for the frame (null: hidden / no cursor).
//       public static CrosshairState? Crosshair(ChartFrame frame);
//       // Exactly what is drawn for the crosshair, empty when hidden:
//       //  PriceTag - only when the state has a Price: text = the price as the axis tags show it ("#,##0.00",
//       //             invariant), a box like the D-32 tags (height Scene.TagHeight * AxisLabelPx, mono text with the same
//       //             padding), centred in the price-axis column, vertically centred on state.Y.
//       //  TimeTag  - the candle's open time in frame.TimeZone, "d MMM HH:mm" (invariant, e.g. "31 Oct 08:25"), a box
//       //             on the time axis (inside layout.TimeAxis vertically) centred on state.X.
//       //  OhlcLine - "O {o}  H {h}  L {l}  C {c}  Vol {v}" (two spaces between; prices "#,##0.00", volume "#,##0", all
//       //             invariant), directly under the headline (Bounds.Y >= Headline.Bottom - 1), inside the plot.
//       // Order: PriceTag, TimeTag, OhlcLine. Drawn after everything else (on top of the price tags).
//       public static IReadOnlyList<CrosshairItem> CrosshairItems(ChartFrame frame);
//   }
//
// Pixels: dashed thin lines in a muted (grid-like) colour: a vertical one at state.X from the plot's top to the bottom of
// the volume pane, a horizontal one at state.Y across the plot only (not into the price-axis column), none when
// hidden; over the volume pane only the vertical one. Nothing else in the frame changes.

using System.Globalization;
using BitcoinWallpaper.Chart.Core.Crosshair;
using BitcoinWallpaper.Chart.Core.Layout;
using BitcoinWallpaper.Chart.Render.D2D;

namespace BitcoinWallpaper.Chart.Render.Tests;

[Collection(nameof(RendererCollection))]
public class CrosshairRenderTests(RendererFixture fx)
{
    private const string Desk = "btc-5120x1440.png";
    private const string Tv = "btc-3840x2160.png";
    private const int Diff = 6; // sum over B, G, R: a pixel "differs" above this

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static CursorPx InPlot(ChartLayout l, double xShare = 0.3, double yShare = 0.6) =>
        new(l.Plot.X + (xShare * l.Plot.Width) + 3.3, l.Plot.Y + (yShare * l.Plot.Height));

    private ChartFrame With(string file, Func<ChartLayout, CursorPx?> cursor)
    {
        var f = fx.Frame(file);
        return f with { Cursor = cursor(f.Layout) };
    }

    private static bool Differs(BgraImage a, BgraImage b, int x, int y)
    {
        var i = ((y * a.Width) + x) * 4;
        return Math.Abs(a.Pixels[i] - b.Pixels[i]) + Math.Abs(a.Pixels[i + 1] - b.Pixels[i + 1]) +
               Math.Abs(a.Pixels[i + 2] - b.Pixels[i + 2]) > Diff;
    }

    private static bool Inside(RectPx r, int x, int y, double grow = 0) =>
        x >= r.X - grow && x < r.Right + grow && y >= r.Y - grow && y < r.Bottom + grow;

    public static TheoryData<string> Screens => new() { Desk, Tv };

    // ---- hidden: nothing changes ----------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Screens))]
    public void A_hidden_crosshair_draws_exactly_the_same_pixels_as_no_crosshair(string file)
    {
        var plain = fx.Frame(file);
        var baseline = fx.Renderer.Render(plain);
        var l = plain.Layout;
        var hidden = new CursorPx?[]
        {
            null,
            new CursorPx(l.PriceAxis.X + (l.PriceAxis.Width / 2), l.Plot.Y + (l.Plot.Height / 2)), // price-axis column
            new CursorPx(l.Plot.X + (l.Plot.Width / 2), l.TimeAxis.Y + (l.TimeAxis.Height / 2)),    // time axis
            new CursorPx(l.Plot.X + (l.Plot.Width / 2), 2),                                          // top margin
            new CursorPx(-50, -50),
        };

        foreach (var cursor in hidden)
        {
            var frame = plain with { Cursor = cursor };
            Assert.Null(FrameView.Crosshair(frame));
            Assert.Empty(FrameView.CrosshairItems(frame));
            var image = fx.Renderer.Render(frame);
            Assert.True(baseline.Pixels.AsSpan().SequenceEqual(image.Pixels), $"cursor {cursor}: the frame differs from the one without crosshair");
        }
    }

    // ---- the values -----------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Screens))]
    public void The_price_is_the_inverse_of_the_drawn_price_scale(string file)
    {
        var frame = With(file, l => InPlot(l));
        var l = frame.Layout;
        var s = FrameView.Crosshair(frame);
        Assert.NotNull(s);
        Assert.Equal(frame.Cursor!.Value.Y, s.Y, 6);
        Assert.Same(frame.Candles[s.CandleIndex], s.Candle);
        Assert.Equal(l.CandleCenterX(s.CandleIndex), s.X, 6);

        // Independent check against the axis: two tick labels give two (y, price) points of the scale.
        var levels = FrameView.PriceAxisItems(frame).Where(i => i.Kind == PriceAxisItemKind.Level).ToList();
        Assert.True(levels.Count >= 3);
        var a = levels[0];
        var b = levels[^1];
        double Price(PriceAxisItem i) => double.Parse(i.Text, NumberStyles.Number, Inv);
        double Cy(PriceAxisItem i) => i.Bounds.Y + (i.Bounds.Height / 2);
        var expected = Price(a) + ((s.Y - Cy(a)) * (Price(b) - Price(a)) / (Cy(b) - Cy(a)));
        var tolerance = Math.Abs((Price(b) - Price(a)) / (Cy(b) - Cy(a))) * 1.5; // the labels' centres are rounded to px
        Assert.InRange(s.Price!.Value, expected - tolerance, expected + tolerance);
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void The_price_tag_sits_in_the_axis_column_like_the_D32_tags_on_top_of_the_others(string file)
    {
        var plain = fx.Frame(file);
        var frame = With(file, l => InPlot(l));
        var l = frame.Layout;
        var size = l.Text.AxisLabelPx;
        var s = FrameView.Crosshair(frame)!;
        var tag = Assert.Single(FrameView.CrosshairItems(frame), i => i.Kind == CrosshairItemKind.PriceTag);

        Assert.Equal(((decimal)s.Price!.Value).ToString("#,##0.00", Inv), tag.Text);
        var column = l.PriceAxis;
        Assert.True(Math.Abs((tag.Bounds.X + (tag.Bounds.Width / 2)) - (column.X + (column.Width / 2))) <= 1, $"tag {tag.Bounds} not centred in {column}");
        Assert.True(tag.Bounds.X - column.X >= 0.2 * size && column.Right - tag.Bounds.Right >= 0.2 * size);
        Assert.True(Math.Abs((tag.Bounds.Y + (tag.Bounds.Height / 2)) - s.Y) <= 1, $"tag {tag.Bounds} not centred on y {s.Y}");
        Assert.True(tag.Bounds.Width >= (tag.Text.Length * 0.6 * size) + (0.5 * size), "no inner padding");

        var last = Assert.Single(FrameView.PriceAxisItems(plain), i => i.Kind == PriceAxisItemKind.LastPrice);
        Assert.True(Math.Abs(tag.Bounds.Height - last.Bounds.Height) <= 1, "not the height of the D-32 tags");

        // The axis items themselves are what they were: the crosshair adds a tag, it does not move the others.
        Assert.Equal(FrameView.PriceAxisItems(plain), FrameView.PriceAxisItems(frame));
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void The_time_tag_shows_the_candles_local_open_time_centred_on_the_line(string file)
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus3", TimeSpan.FromHours(3), "plus3", "plus3");
        foreach (var tz in new[] { TimeZoneInfo.Utc, zone })
        {
            var frame = With(file, l => InPlot(l)) with { TimeZone = tz };
            var l = frame.Layout;
            var s = FrameView.Crosshair(frame)!;
            var tag = Assert.Single(FrameView.CrosshairItems(frame), i => i.Kind == CrosshairItemKind.TimeTag);

            var local = TimeZoneInfo.ConvertTime(s.Candle.OpenTime, tz);
            Assert.Equal(local.ToString("d MMM HH:mm", Inv), tag.Text);
            Assert.True(Math.Abs((tag.Bounds.X + (tag.Bounds.Width / 2)) - s.X) <= 1, $"tag {tag.Bounds} not centred on x {s.X}");
            Assert.True(tag.Bounds.Y >= l.TimeAxis.Y - 1 && tag.Bounds.Bottom <= l.TimeAxis.Bottom + 1, $"tag {tag.Bounds} not on the time axis {l.TimeAxis}");
            Assert.True(tag.Bounds.Width >= tag.Text.Length * 0.6 * l.Text.AxisLabelPx);
        }

        var utc = With(file, l => InPlot(l));
        var shifted = utc with { TimeZone = zone };
        Assert.NotEqual(
            Assert.Single(FrameView.CrosshairItems(utc), i => i.Kind == CrosshairItemKind.TimeTag).Text,
            Assert.Single(FrameView.CrosshairItems(shifted), i => i.Kind == CrosshairItemKind.TimeTag).Text);
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void The_ohlc_line_sits_under_the_headline_with_the_candles_values(string file)
    {
        var frame = With(file, l => InPlot(l));
        var l = frame.Layout;
        var c = FrameView.Crosshair(frame)!.Candle;
        var line = Assert.Single(FrameView.CrosshairItems(frame), i => i.Kind == CrosshairItemKind.OhlcLine);

        string P(decimal v) => v.ToString("#,##0.00", Inv);
        Assert.Equal($"O {P(c.Open)}  H {P(c.High)}  L {P(c.Low)}  C {P(c.Close)}  Vol {c.Volume.ToString("#,##0", Inv)}", line.Text);
        Assert.True(line.Bounds.Y >= l.Headline.Bottom - 1, $"line {line.Bounds} is not under the headline {l.Headline}");
        Assert.True(line.Bounds.Y - l.Headline.Bottom <= 0.05 * l.HeightPx, "not directly under the headline");
        Assert.True(line.Bounds.X >= l.Plot.X && line.Bounds.Right <= l.Plot.Right, "outside the plot");
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void Over_the_volume_pane_there_is_a_time_tag_and_the_values_but_no_price_tag(string file)
    {
        var frame = With(file, l => new CursorPx(l.Plot.X + (0.4 * l.Plot.Width), l.VolumePane.Y + (l.VolumePane.Height / 2)));
        var s = FrameView.Crosshair(frame);
        Assert.NotNull(s);
        Assert.True(s.OverVolume);
        var kinds = FrameView.CrosshairItems(frame).Select(i => i.Kind).ToList();
        Assert.Equal([CrosshairItemKind.TimeTag, CrosshairItemKind.OhlcLine], kinds);
    }

    // ---- the pixels -----------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Screens))]
    public void The_lines_are_dashed_inside_the_plot_and_do_not_reach_the_price_axis_column(string file)
    {
        var plain = fx.Frame(file);
        var frame = With(file, l => InPlot(l));
        var l = frame.Layout;
        var s = FrameView.Crosshair(frame)!;
        var items = FrameView.CrosshairItems(frame);
        var a = fx.Renderer.Render(plain);
        var b = fx.Renderer.Render(frame);
        var x0 = (int)Math.Floor(s.X);
        var y0 = (int)Math.Floor(s.Y);
        var headline = l.Headline;
        var ohlc = items.Single(i => i.Kind == CrosshairItemKind.OhlcLine).Bounds;
        var grow = l.Text.StatsRowPx;

        // Vertical: rows below the headline block and its line, away from the horizontal line.
        var top = (int)Math.Ceiling(Math.Max(headline.Bottom, ohlc.Bottom) + grow);
        var rows = Enumerable.Range(top, (int)l.Plot.Bottom - top).Where(y => Math.Abs(y - y0) > 4).ToList();
        Assert.True(rows.Count > 100);
        var marked = rows.Select(y => Enumerable.Range(x0 - 1, 3).Any(x => Differs(a, b, x, y))).ToList();
        var share = marked.Count(m => m) / (double)marked.Count;
        Assert.InRange(share, 0.25, 0.85); // a dashed line: marks and gaps
        var changes = marked.Zip(marked.Skip(1), (p, q) => p != q).Count(c => c);
        Assert.True(changes >= 10, $"only {changes} dash edges on the vertical line");

        // Horizontal: columns across the plot, away from the vertical line, the headline block and the line text.
        var cols = Enumerable.Range((int)l.Plot.X, (int)l.Plot.Width)
            .Where(x => Math.Abs(x - x0) > 4 && !Inside(headline, x, y0, grow) && !Inside(ohlc, x, y0, grow)).ToList();
        Assert.True(cols.Count > 500);
        var hMarked = cols.Select(x => Enumerable.Range(y0 - 1, 3).Any(y => Differs(a, b, x, y))).ToList();
        var hShare = hMarked.Count(m => m) / (double)hMarked.Count;
        Assert.InRange(hShare, 0.25, 0.85);
        Assert.True(hMarked.Zip(hMarked.Skip(1), (p, q) => p != q).Count(c => c) >= 10, "the horizontal line is not dashed");

        // Not in the price-axis column: on the line's rows, outside the price tag, the column is untouched.
        var tag = items.Single(i => i.Kind == CrosshairItemKind.PriceTag).Bounds;
        for (var y = y0 - 1; y <= y0 + 1; y++)
        {
            for (var x = (int)Math.Ceiling(l.Plot.Right); x < b.Width; x++)
            {
                if (!Inside(tag, x, y, 2))
                {
                    Assert.False(Differs(a, b, x, y), $"the horizontal line reaches the price-axis column at ({x}, {y})");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void Nothing_else_in_the_frame_changes(string file)
    {
        var frame = With(file, l => InPlot(l));
        var l = frame.Layout;
        var s = FrameView.Crosshair(frame)!;
        var items = FrameView.CrosshairItems(frame);
        var a = fx.Renderer.Render(fx.Frame(file));
        var b = fx.Renderer.Render(frame);
        var grow = l.Text.StatsRowPx;
        var boxes = items.Select(i => i.Bounds).ToList();

        var strays = new List<(int, int)>();
        for (var y = 0; y < b.Height; y++)
        {
            for (var x = 0; x < b.Width; x++)
            {
                if (!Differs(a, b, x, y))
                {
                    continue;
                }

                var onVertical = Math.Abs(x - s.X) <= 2 && y >= l.Plot.Y - 1 && y < l.VolumePane.Bottom + 1;
                var onHorizontal = Math.Abs(y - s.Y) <= 2 && x >= l.Plot.X && x < l.Plot.Right;
                if (!onVertical && !onHorizontal && !boxes.Any(r => Inside(r, x, y, grow)))
                {
                    strays.Add((x, y));
                }
            }
        }

        Assert.True(strays.Count == 0, $"{strays.Count} changed pixels outside the lines and tags, e.g. {string.Join(" ", strays.Take(5))}");
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void Over_the_volume_pane_only_the_vertical_line_is_drawn(string file)
    {
        var frame = With(file, l => new CursorPx(l.Plot.X + (0.4 * l.Plot.Width) + 3.3, l.VolumePane.Y + (0.5 * l.VolumePane.Height)));
        var l = frame.Layout;
        var s = FrameView.Crosshair(frame)!;
        var a = fx.Renderer.Render(fx.Frame(file));
        var b = fx.Renderer.Render(frame);
        var y0 = (int)s.Y;
        var x0 = (int)s.X;

        // No horizontal line: on the cursor's row, away from the vertical line, nothing changes.
        for (var x = (int)l.Plot.X; x < b.Width; x++)
        {
            if (Math.Abs(x - x0) > 4)
            {
                Assert.False(Differs(a, b, x, y0), $"a horizontal line over the volume pane at ({x}, {y0})");
            }
        }

        // The vertical one is there, dashed, through the volume pane.
        var rows = Enumerable.Range((int)l.VolumePane.Y, (int)l.VolumePane.Height).ToList();
        var marked = rows.Count(y => Enumerable.Range(x0 - 1, 3).Any(x => Differs(a, b, x, y)));
        Assert.InRange(marked / (double)rows.Count, 0.25, 0.85);
    }
}
