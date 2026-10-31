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
//   public enum CrosshairItemKind { PriceTag, TimeTag, InfoBlock }
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
//       //  InfoBlock - (present ONLY when state.OnCandle, round 3; otherwise no such item; replaces OhlcLine) three lines joined by "\n": the candle's open time in frame.TimeZone
//       //             "d MMM HH:mm"; "O {o}  H {h}  L {l}  C {c}"; "Vol {v}" (two spaces between; prices "#,##0.00", volume
//       //             "#,##0", all invariant). Bounds: always fully inside layout.Plot. For a cursor in the plot: by default
//       //             ABOVE the cursor (Bounds.Bottom <= Y, Y - Bottom <= 3 * AxisLabelPx) and starting a little right of
//       //             state.X (X <= Bounds.X <= X + 4 * AxisLabelPx); when that would leave the plot on the right it is
//       //             flipped to the left of X (Bounds.Right <= X, X - Right <= 4 * AxisLabelPx); when there is not enough room
//       //             above inside the plot it is BELOW the cursor (Bounds.Y >= Y, Bounds.Y - Y <= 3 * AxisLabelPx). For a
//       //             cursor over the volume pane: only "fully inside the plot" is pinned.
//       // Order: PriceTag, TimeTag, InfoBlock. Drawn after everything else (on top of the price tags).
//       public static IReadOnlyList<CrosshairItem> CrosshairItems(ChartFrame frame);
//   }
//
// The vertical line is at state.X = the cursor x (no snapping, round 2). Every candle in state.HighlightedIndices (the ones a
// line crosses, round 3, see CrosshairModelTests) is drawn slightly brighter (higher luminance of its body and wick pixels,
// by no more than 70); every other candle is unchanged.
// Pixels: SOLID thin lines (round 4: colour #8FA3B8 at ~55 % opacity, 1 px, scaled with the DPI scale): a vertical one at
// state.X from the plot's top to the bottom of the volume pane, a horizontal one at state.Y from x = 0 to the screen width (round 5:
// also through the price-axis column, under the price tag, whose fill stays unbroken); none when hidden; over the volume pane
// only the vertical one. Nothing else in the frame changes.

using System.Globalization;
using BitcoinWallpaper.Chart.Core.Candles;
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
    public void A_hidden_crosshair_off_the_screen_or_without_cursor_draws_exactly_the_same_pixels_as_no_crosshair(string file)
    {
        var plain = fx.Frame(file);
        var baseline = fx.Renderer.Render(plain);
        var l = plain.Layout;
        var hidden = new CursorPx?[]
        {
            null,
            new CursorPx(-50, -50),
            new CursorPx(l.WidthPx + 5, 10),
            new CursorPx(10, l.HeightPx),
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

    // Round 6: the crosshair is a whole-monitor one. Wherever the cursor is on the screen both lines run edge to edge; the
    // chart tags show only where they make sense (price tag: y in the plot; time tag: x over a candle slot).
    [Theory]
    [MemberData(nameof(Screens))]
    public void Outside_the_plot_the_lines_still_cross_the_whole_monitor_and_only_the_fitting_tags_show(string file)
    {
        var plain = fx.Frame(file);
        var l = plain.Layout;
        var a = fx.Renderer.Render(plain);
        var slotX = l.CandleCenterX(plain.Candles.Count - 20);
        var axisX = l.PriceAxis.X + (l.PriceAxis.Width / 2);
        var cases = new (CursorPx Cursor, CrosshairItemKind[] Kinds)[]
        {
            (new CursorPx(axisX, l.Plot.Y + (0.6 * l.Plot.Height)), [CrosshairItemKind.PriceTag]),            // price-axis column
            (new CursorPx(slotX, l.TimeAxis.Y + (l.TimeAxis.Height / 2)), [CrosshairItemKind.TimeTag]),       // time axis
            (new CursorPx(slotX, 2), [CrosshairItemKind.TimeTag]),                                            // top margin
            (new CursorPx(axisX, 2), []),                                                                     // margin over the axis column
            (new CursorPx(l.WidthPx - 1, l.HeightPx - 1), []),                                                // the screen's corner
            (new CursorPx(slotX, (l.Plot.Bottom + l.VolumePane.Y) / 2), [CrosshairItemKind.TimeTag]),         // gap above the volume pane
        };

        foreach (var (cursor, kinds) in cases)
        {
            var frame = plain with { Cursor = cursor };
            var s = FrameView.Crosshair(frame);
            Assert.NotNull(s);
            Assert.Equal(cursor.X, s.X, 9);
            Assert.Equal(kinds, FrameView.CrosshairItems(frame).Select(i => i.Kind).ToList());
            Assert.False(s.OnCandle);
            AssertLinesCrossTheScreen(new Probe(frame, a, 0), $"cursor {cursor}");
            var strays = Strays(new Probe(frame, a, 0));
            Assert.True(strays.Count == 0, $"cursor {cursor}: {strays.Count} changed pixels outside the lines and tags, e.g. {string.Join(" ", strays.Take(5))}");
        }
    }

    // Both lines present on >= 90 % of the sampled pixels across the whole screen (round 6), except where a tag, the headline,
    // the info block or a crossed candle covers them.
    private void AssertLinesCrossTheScreen(Probe p, string what)
    {
        var frame = p.Frame;
        var l = frame.Layout;
        var s = FrameView.Crosshair(frame)!;
        var a = p.Plain;
        var b = fx.Renderer.Render(frame);
        var x0 = (int)Math.Floor(s.X);
        var y0 = (int)Math.Floor(s.Y);
        var grow = l.Text.StatsRowPx;
        var boxes = FrameView.CrosshairItems(frame).Select(i => i.Bounds).ToList();

        var rows = Enumerable.Range(0, b.Height)
            .Where(y => Math.Abs(y - y0) > 4 && !Inside(l.Headline, x0, y, grow) && !boxes.Any(r => Inside(r, x0, y, 3))).ToList();
        var vMarked = rows.Count(y => Enumerable.Range(x0 - 1, 3).Any(x => x >= 0 && x < b.Width && Differs(a, b, x, y)));
        Assert.True(vMarked >= 0.9 * rows.Count, $"{what}: the vertical line is on {vMarked} of {rows.Count} rows of the screen");

        var cols = Enumerable.Range(0, b.Width)
            .Where(x => Math.Abs(x - x0) > 4 && !InLitSlot(frame, s, x) && !Inside(l.Headline, x, y0, grow) && !boxes.Any(r => Inside(r, x, y0, 3))).ToList();
        var hMarked = cols.Count(x => Enumerable.Range(y0 - 1, 3).Any(y => y >= 0 && y < b.Height && Differs(a, b, x, y)));
        Assert.True(hMarked >= 0.9 * cols.Count, $"{what}: the horizontal line is on {hMarked} of {cols.Count} columns of the screen");

        // The ends too (a line that stops at the plot or the volume pane would still pass 90 % of the whole).
        var topRows = rows.Where(y => y < l.Plot.Y).ToList();
        if (topRows.Count > 5)
        {
            var n = topRows.Count(y => Enumerable.Range(x0 - 1, 3).Any(x => x >= 0 && x < b.Width && Differs(a, b, x, y)));
            Assert.True(n >= 0.9 * topRows.Count, $"{what}: the vertical line is missing in the top margin ({n} of {topRows.Count} rows)");
        }

        var bottomRows = rows.Where(y => y >= l.VolumePane.Bottom).ToList();
        if (bottomRows.Count > 5)
        {
            var n = bottomRows.Count(y => Enumerable.Range(x0 - 1, 3).Any(x => x >= 0 && x < b.Width && Differs(a, b, x, y)));
            Assert.True(n >= 0.7 * bottomRows.Count, $"{what}: the vertical line is missing on the time axis / bottom margin ({n} of {bottomRows.Count} rows)");
        }

        var axisCols = cols.Where(x => x >= l.Plot.Right).ToList();
        if (axisCols.Count > 5)
        {
            var n = axisCols.Count(x => Enumerable.Range(y0 - 1, 3).Any(y => y >= 0 && y < b.Height && Differs(a, b, x, y)));
            Assert.True(n >= 0.7 * axisCols.Count, $"{what}: the horizontal line is missing in the price-axis column ({n} of {axisCols.Count} columns)");
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
        Assert.Same(frame.Candles[s.CandleIndex!.Value], s.Candle);
        Assert.Equal(frame.Cursor!.Value.X, s.X, 9); // no snapping (round 2)

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

            var local = TimeZoneInfo.ConvertTime(s.Candle!.OpenTime, tz);
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

    // ---- finding candles in the pixels of the frame without the crosshair --------------------------------------

    private static double Lum(BgraImage im, int x, int y)
    {
        var i = ((y * im.Width) + x) * 4;
        return (0.114 * im.Pixels[i]) + (0.587 * im.Pixels[i + 1]) + (0.299 * im.Pixels[i + 2]);
    }

    // A pixel of an up (green) or down (red) candle in the frame without the crosshair.
    private static bool IsCandle(BgraImage im, int x, int y)
    {
        var i = ((y * im.Width) + x) * 4;
        return Math.Abs(im.Pixels[i + 1] - im.Pixels[i + 2]) > 60;
    }

    /// <summary>The longest run of consecutive rows (in the plot; under the headline's bottom in its columns) in which every column is
    /// candle-coloured; (0, -1) when none.</summary>
    private static (int Top, int Bottom) LongestRun(BgraImage a, ChartLayout l, params int[] cols)
    {
        (int Top, int Bottom) best = (0, -1);
        var start = -1;
        var underHeadline = cols[0] >= l.Headline.X - l.CandlePitchPx && cols[0] <= l.Headline.Right + l.CandlePitchPx;
        var from = underHeadline ? (int)Math.Ceiling(l.Headline.Bottom) + 10 : (int)l.Plot.Y + 4;
        for (var y = from; y <= (int)l.Plot.Bottom; y++)
        {
            var on = y < (int)l.Plot.Bottom - 2 && cols.All(x => IsCandle(a, x, y));
            if (on && start < 0)
            {
                start = y;
            }
            else if (!on && start >= 0)
            {
                if (y - 1 - start > best.Bottom - best.Top)
                {
                    best = (start, y - 1);
                }

                start = -1;
            }
        }

        return best;
    }

    private sealed record Probe(ChartFrame Frame, BgraImage Plain, int Index);

    private Probe Make(string file, int index, double x, double y, BgraImage a) =>
        new(fx.Frame(file) with { Cursor = new CursorPx(x, y) }, a, index);

    // The cursor in the middle of a candle's body (the candle with the tallest body, columns centre -2 and +2).
    private Probe OnBody(string file)
    {
        var plain = fx.Frame(file);
        var l = plain.Layout;
        var a = fx.Renderer.Render(plain);
        (int I, int Top, int Bottom) best = (-1, 0, -1);
        for (var i = l.FirstDrawnIndex + 30; i < l.FirstDrawnIndex + 90; i++)
        {
            var cx = (int)Math.Floor(l.CandleCenterX(i));
            var run = LongestRun(a, l, cx - 2, cx + 2);
            if (run.Bottom - run.Top > best.Bottom - best.Top)
            {
                best = (i, run.Top, run.Bottom);
            }
        }

        Assert.True(best.Bottom - best.Top >= 6, "no candle with a body to test on");
        return Make(file, best.I, l.CandleCenterX(best.I), ((best.Top + best.Bottom) / 2.0) + 0.5, a);
    }

    // The cursor on the upper wick of the highest candle, 3 px under its tip: no room above for a block.
    private Probe OnWick(string file)
    {
        var plain = fx.Frame(file);
        var l = plain.Layout;
        var a = fx.Renderer.Render(plain);
        (int I, int Top) best = (-1, int.MaxValue);
        for (var i = l.FirstDrawnIndex + 1; i < plain.Candles.Count - 1; i++)
        {
            var cx = (int)Math.Floor(l.CandleCenterX(i));
            var run = LongestRun(a, l, cx);
            if (run.Bottom - run.Top >= 6 && run.Top < best.Top)
            {
                best = (i, run.Top);
            }
        }

        Assert.True(best.I >= 0);
        return Make(file, best.I, l.CandleCenterX(best.I), best.Top + 3.5, a);
    }

    // The cursor in the middle of the newest (forming) candle, at the right end of the plot.
    private Probe OnNewest(string file)
    {
        var plain = fx.Frame(file);
        var l = plain.Layout;
        var a = fx.Renderer.Render(plain);
        var i = plain.Candles.Count - 1;
        var run = LongestRun(a, l, (int)Math.Floor(l.CandleCenterX(i)));
        Assert.True(run.Bottom - run.Top >= 3, "the newest candle is not found in the pixels");
        return Make(file, i, l.CandleCenterX(i), ((run.Top + run.Bottom) / 2.0) + 0.5, a);
    }

    // At the top edge of the plot (the price there is above every high: the scale has a margin), at the edge of a candle's slot:
    // on no candle, and no candle's range holds the price.
    private Probe AboveAll(string file)
    {
        var on = OnBody(file);
        var l = on.Frame.Layout;
        return Make(file, on.Index, l.CandleCenterX(on.Index) + (l.CandlePitchPx / 2) - 1, l.Plot.Y + 2, on.Plain);
    }

    // A synthetic chart whose single spike candle (index 5, far from the headline) is high above the others, so that the
    // cursor on its upper wick has too little room above the cursor for the info block.
    private Probe OnSpike(string file)
    {
        var real = fx.Frame(file).Layout;
        var t0 = new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);
        var candles = Enumerable.Range(0, 60).Select(i =>
        {
            var open = 100000m + (i * 5);
            var close = open + 30;
            var high = i == 5 ? open + 9000 : close + 20;
            return new Candle(t0.AddHours(4 * i), open, high, open - 20, close, 1000 + i, i < 59);
        }).ToList();
        var plain = ChartFrame.Create(real.WidthPx, real.HeightPx, real.DpiScale, candles, null, t0.AddHours(240), TimeZoneInfo.Utc, interval: TimeSpan.FromHours(4));
        var l = plain.Layout;
        var a = fx.Renderer.Render(plain);
        var cx = (int)Math.Floor(l.CandleCenterX(5));
        var run = LongestRun(a, l, cx);
        Assert.True(run.Bottom - run.Top >= 10, "the spike wick is not found in the pixels");
        return new Probe(plain with { Cursor = new CursorPx(l.CandleCenterX(5), run.Top + 3.5) }, a, 5);
    }

    // At the edge of a body candle's slot (outside body and wick), at the body's height.
    private Probe BesideBody(string file)
    {
        var on = OnBody(file);
        var l = on.Frame.Layout;
        return Make(file, on.Index, l.CandleCenterX(on.Index) + (l.CandlePitchPx / 2) - 1, on.Frame.Cursor!.Value.Y, on.Plain);
    }

    // ---- the info block --------------------------------------------------------------------------------------

    private static string[] InfoLines(ChartFrame frame, Candle c)
    {
        string P(decimal v) => v.ToString("#,##0.00", Inv);
        return
        [
            TimeZoneInfo.ConvertTime(c.OpenTime, frame.TimeZone).ToString("d MMM HH:mm", Inv),
            $"O {P(c.Open)}  H {P(c.High)}  L {P(c.Low)}  C {P(c.Close)}",
            $"Vol {c.Volume.ToString("#,##0", Inv)}",
        ];
    }

    private static RectPx Block(ChartFrame frame) =>
        Assert.Single(FrameView.CrosshairItems(frame), i => i.Kind == CrosshairItemKind.InfoBlock).Bounds;

    private static void AssertInsidePlot(RectPx b, ChartLayout l) =>
        Assert.True(b.X >= l.Plot.X - 0.5 && b.Right <= l.Plot.Right + 0.5 && b.Y >= l.Plot.Y - 0.5 && b.Bottom <= l.Plot.Bottom + 0.5,
            $"info block {b} is not inside the plot {l.Plot}");

    [Theory]
    [MemberData(nameof(Screens))]
    public void On_a_candle_the_state_says_so_and_all_three_items_show(string file)
    {
        foreach (var p in new[] { OnBody(file), OnWick(file), OnNewest(file) })
        {
            var s = FrameView.Crosshair(p.Frame)!;
            Assert.NotNull(s);
            Assert.Equal(p.Index, s.CandleIndex);
            Assert.True(s.OnCandle, $"cursor {p.Frame.Cursor} on candle {p.Index} is not reported OnCandle");
            Assert.Equal(
                [CrosshairItemKind.PriceTag, CrosshairItemKind.TimeTag, CrosshairItemKind.InfoBlock],
                FrameView.CrosshairItems(p.Frame).Select(i => i.Kind).ToList());
        }
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void Off_a_candle_there_is_no_info_block_but_the_price_and_time_tags_stay(string file)
    {
        foreach (var p in new[] { AboveAll(file), BesideBody(file) })
        {
            var s = FrameView.Crosshair(p.Frame);
            Assert.NotNull(s);
            Assert.False(s.OnCandle, $"cursor {p.Frame.Cursor} is off every candle");
            Assert.Equal(
                [CrosshairItemKind.PriceTag, CrosshairItemKind.TimeTag],
                FrameView.CrosshairItems(p.Frame).Select(i => i.Kind).ToList());
        }
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void The_info_block_holds_date_ohlc_and_volume_lines(string file)
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus3", TimeSpan.FromHours(3), "plus3", "plus3");
        var p = OnBody(file);
        foreach (var tz in new[] { TimeZoneInfo.Utc, zone })
        {
            var frame = p.Frame with { TimeZone = tz };
            var c = FrameView.Crosshair(frame)!.Candle;
            var block = Assert.Single(FrameView.CrosshairItems(frame), i => i.Kind == CrosshairItemKind.InfoBlock);
            Assert.Equal(string.Join("\n", InfoLines(frame, c!)), block.Text);
            Assert.DoesNotContain(FrameView.CrosshairItems(frame), i => i.Text.Contains("Vol") && i.Kind != CrosshairItemKind.InfoBlock);
        }
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void The_info_block_sits_above_the_cursor_a_little_right_of_it(string file)
    {
        var frame = OnBody(file).Frame;
        var l = frame.Layout;
        var s = FrameView.Crosshair(frame)!;
        var b = Block(frame);
        AssertInsidePlot(b, l);
        Assert.True(b.Bottom <= s.Y && s.Y - b.Bottom <= 3 * l.Text.AxisLabelPx, $"block {b} is not just above y {s.Y}");
        Assert.True(b.X >= s.X && b.X - s.X <= 4 * l.Text.AxisLabelPx, $"block {b} does not start a little right of x {s.X}");
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void The_info_block_flips_left_of_the_cursor_near_the_right_edge(string file)
    {
        var frame = OnNewest(file).Frame;
        var l = frame.Layout;
        var s = FrameView.Crosshair(frame)!;
        var b = Block(frame);
        AssertInsidePlot(b, l);
        Assert.True(b.Y < s.Y ? s.Y - b.Bottom <= 3 * l.Text.AxisLabelPx && b.Bottom <= s.Y : b.Y >= s.Y, $"block {b} is not above or below y {s.Y}");
        Assert.True(b.Right <= s.X && s.X - b.Right <= 4 * l.Text.AxisLabelPx, $"block {b} is not just left of x {s.X}");
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void The_info_block_goes_below_the_cursor_when_there_is_no_room_above(string file)
    {
        var frame = OnSpike(file).Frame;
        var l = frame.Layout;
        var s = FrameView.Crosshair(frame)!;
        Assert.True(s.OnCandle);
        var b = Block(frame);
        AssertInsidePlot(b, l);
        Assert.True(b.Y >= s.Y && b.Y - s.Y <= 3 * l.Text.AxisLabelPx, $"block {b} is not just below y {s.Y}");
        Assert.True(
            (b.X >= s.X && b.X - s.X <= 4 * l.Text.AxisLabelPx) || (b.Right <= s.X && s.X - b.Right <= 4 * l.Text.AxisLabelPx),
            $"block {b} is not next to x {s.X}");
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void The_info_block_stays_inside_the_plot_on_every_candle(string file)
    {
        var plain = fx.Frame(file);
        var l = plain.Layout;
        var a = fx.Renderer.Render(plain);
        var checkedCandles = 0;
        for (var i = l.FirstDrawnIndex + 1; i < plain.Candles.Count; i += i < plain.Candles.Count - 8 ? 7 : 1)
        {
            var cx = (int)Math.Floor(l.CandleCenterX(i));
            var run = LongestRun(a, l, cx);
            if (run.Bottom - run.Top < 3)
            {
                continue;
            }

            var frame = plain with { Cursor = new CursorPx(l.CandleCenterX(i), ((run.Top + run.Bottom) / 2.0) + 0.5) };
            if (FrameView.Crosshair(frame) is { OnCandle: true })
            {
                AssertInsidePlot(Block(frame), l);
                checkedCandles++;
            }
        }

        Assert.True(checkedCandles >= 10, $"only {checkedCandles} candles checked");
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void Over_the_volume_pane_there_is_a_time_tag_but_no_price_tag_and_no_info_block(string file)
    {
        var frame = With(file, l => new CursorPx(l.Plot.X + (0.4 * l.Plot.Width), l.VolumePane.Y + (l.VolumePane.Height / 2)));
        var s = FrameView.Crosshair(frame);
        Assert.NotNull(s);
        Assert.True(s.OverVolume);
        Assert.False(s.OnCandle);
        Assert.Equal([CrosshairItemKind.TimeTag], FrameView.CrosshairItems(frame).Select(i => i.Kind).ToList());
    }

    // ---- the pixels -----------------------------------------------------------------------------------------

    private static bool InLitSlot(ChartFrame frame, CrosshairState s, int x) =>
        s.HighlightedIndices.Any(i => Math.Abs(x - frame.Layout.CandleCenterX(i)) <= (frame.Layout.CandlePitchPx / 2) + 1);

    // "1 px" lines at scale 1.0 are at most 2 device px wide, scaled with the DPI scale.
    private static int MaxBand(ChartLayout l) => (int)Math.Ceiling(2 * l.DpiScale);

    [Theory]
    [MemberData(nameof(Screens))]
    public void The_lines_are_solid_and_thin_the_horizontal_one_runs_edge_to_edge_under_the_price_tag(string file)
    {
        var p = OnBody(file);
        var frame = p.Frame;
        var l = frame.Layout;
        var s = FrameView.Crosshair(frame)!;
        var items = FrameView.CrosshairItems(frame);
        var a = p.Plain;
        var b = fx.Renderer.Render(frame);
        var x0 = (int)Math.Floor(s.X);
        var y0 = (int)Math.Floor(s.Y);
        var headline = l.Headline;
        var block = Block(frame);
        var grow = l.Text.StatsRowPx;
        var band = MaxBand(l);

        // The cursor's own candle (wick and body, a little more) is lit and wider than the line: no thinness check there.
        var extent = LongestRun(a, l, (int)Math.Floor(l.CandleCenterX(s.CandleIndex!.Value)));

        // Vertical (round 6: the whole screen height, also the margins, the headline and the time axis): rows away from the
        // horizontal line, the headline and the tags / info block.
        var rows = Enumerable.Range(0, b.Height)
            .Where(y => Math.Abs(y - y0) > 4 && !Inside(headline, x0, y, grow) && !items.Any(i => Inside(i.Bounds, x0, y, 3))).ToList();
        Assert.True(rows.Count > 100);
        var marked = rows.Count(y => Enumerable.Range(x0 - 1, 3).Any(x => Differs(a, b, x, y)));
        Assert.True(marked >= 0.9 * rows.Count, $"the vertical line is on {marked} of {rows.Count} rows: not solid");
        foreach (var y in rows.Where(y => y < extent.Top - 4 || y > extent.Bottom + 4))
        {
            var width = Enumerable.Range(x0 - 4, 9).Count(x => Differs(a, b, x, y));
            Assert.True(width <= band, $"the vertical line is {width} px wide at row {y} (at most {band})");
        }

        // Horizontal: columns across the plot away from the vertical line, the lit candles, the headline and the info block.
        var cols = Enumerable.Range((int)l.Plot.X, (int)l.Plot.Width)
            .Where(x => Math.Abs(x - x0) > 4 && !InLitSlot(frame, s, x) && !Inside(headline, x, y0, grow) && !Inside(block, x, y0, grow)).ToList();
        Assert.True(cols.Count > 500);
        var hMarked = cols.Count(x => Enumerable.Range(y0 - 1, 3).Any(y => Differs(a, b, x, y)));
        Assert.True(hMarked >= 0.9 * cols.Count, $"the horizontal line is on {hMarked} of {cols.Count} columns: not solid");
        foreach (var x in cols)
        {
            var height = Enumerable.Range(y0 - 4, 9).Count(y => Differs(a, b, x, y));
            Assert.True(height <= band, $"the horizontal line is {height} px thick at column {x} (at most {band})");
        }

        // Edge to edge (round 5): the line also crosses the price-axis column, on both sides of the tag, up to the screen edge.
        var tag = items.Single(i => i.Kind == CrosshairItemKind.PriceTag).Bounds;
        var axisCols = Enumerable.Range((int)Math.Ceiling(l.Plot.Right), b.Width - (int)Math.Ceiling(l.Plot.Right))
            .Where(x => !Inside(tag, x, y0, 2)).ToList();
        Assert.True(axisCols.Count > 20);
        var axisMarked = axisCols.Count(x => Enumerable.Range(y0 - 1, 3).Any(y => Differs(a, b, x, y)));
        Assert.True(axisMarked >= 0.9 * axisCols.Count, $"the horizontal line is on {axisMarked} of {axisCols.Count} price-axis column pixels outside the tag");
        Assert.True(axisCols.Any(x => x < tag.X) && axisCols.Any(x => x >= tag.Right), "the line must show on both sides of the tag");
        Assert.True(Differs(a, b, b.Width - 1, y0) || Differs(a, b, b.Width - 1, y0 + 1) || Differs(a, b, b.Width - 1, y0 - 1), "the line stops short of the right screen edge");
        Assert.True(Differs(a, b, 0, y0) || Differs(a, b, 0, y0 + 1) || Differs(a, b, 0, y0 - 1), "the line stops short of the left screen edge");

        // The tag is drawn on top of the line: its fill is unbroken (the left padding strip, away from the text, has one colour
        // from the tag's top to its bottom, also on the line's row).
        var size = l.Text.AxisLabelPx;
        var sx = (int)Math.Ceiling(tag.X + (0.1 * size));
        var yTop = (int)Math.Ceiling(tag.Y + (0.3 * size));
        var yBottom = (int)Math.Floor(tag.Bottom - (0.3 * size));
        Assert.InRange(y0, yTop, yBottom);
        for (var y = yTop; y <= yBottom; y++)
        {
            for (var x = sx; x < sx + 3; x++)
            {
                Assert.False(DiffersFrom(b, x, y, sx, yTop), $"the tag fill is broken at ({x}, {y}): the line shows through it");
            }
        }
    }

    private static bool DiffersFrom(BgraImage im, int x, int y, int rx, int ry)
    {
        var i = ((y * im.Width) + x) * 4;
        var j = ((ry * im.Width) + rx) * 4;
        return Math.Abs(im.Pixels[i] - im.Pixels[j]) + Math.Abs(im.Pixels[i + 1] - im.Pixels[j + 1]) + Math.Abs(im.Pixels[i + 2] - im.Pixels[j + 2]) > Diff;
    }

    private List<(int, int)> Strays(Probe p)
    {
        var frame = p.Frame;
        var l = frame.Layout;
        var s = FrameView.Crosshair(frame)!;
        var a = p.Plain;
        var b = fx.Renderer.Render(frame);
        var grow = l.Text.StatsRowPx;
        var boxes = FrameView.CrosshairItems(frame).Select(i => i.Bounds).ToList();
        var strays = new List<(int, int)>();
        for (var y = 0; y < b.Height; y++)
        {
            for (var x = 0; x < b.Width; x++)
            {
                if (!Differs(a, b, x, y))
                {
                    continue;
                }

                var onVertical = Math.Abs(x - s.X) <= 2; // whole screen height (round 6)
                var onHorizontal = Math.Abs(y - s.Y) <= 2; // edge to edge, wherever the cursor is (round 6)
                var lit = InLitSlot(frame, s, x) && y >= l.Plot.Y && y < l.Plot.Bottom; // a crossed candle is brighter
                if (!onVertical && !onHorizontal && !lit && !boxes.Any(r => Inside(r, x, y, grow)))
                {
                    strays.Add((x, y));
                }
            }
        }

        return strays;
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void Nothing_else_in_the_frame_changes(string file)
    {
        foreach (var p in new[] { OnBody(file), OnWick(file), AboveAll(file), BesideBody(file) })
        {
            var strays = Strays(p);
            Assert.True(strays.Count == 0, $"cursor {p.Frame.Cursor}: {strays.Count} changed pixels outside the lines, tags, block and crossed candles, e.g. {string.Join(" ", strays.Take(5))}");
        }
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void Over_the_volume_pane_both_lines_cross_the_screen_solid_and_thin(string file)
    {
        var frame = With(file, l => new CursorPx(l.Plot.X + (0.4 * l.Plot.Width) + 3.3, l.VolumePane.Y + (0.5 * l.VolumePane.Height)));
        var l = frame.Layout;
        var s = FrameView.Crosshair(frame)!;
        var a = fx.Renderer.Render(fx.Frame(file));
        var b = fx.Renderer.Render(frame);
        var y0 = (int)s.Y;
        var x0 = (int)s.X;

        // The horizontal line runs edge to edge over the volume pane too (round 6), solid and thin.
        var cols = Enumerable.Range(0, b.Width).Where(x => Math.Abs(x - x0) > 4).ToList();
        var hMarked = cols.Count(x => Enumerable.Range(y0 - 1, 3).Any(y => Differs(a, b, x, y)));
        Assert.True(hMarked >= 0.9 * cols.Count, $"the horizontal line is on {hMarked} of {cols.Count} columns over the volume pane");
        Assert.All(cols, x => Assert.True(Enumerable.Range(y0 - 4, 9).Count(y => Differs(a, b, x, y)) <= MaxBand(l), $"the line is too thick at column {x}"));

        // The vertical one is there, solid, through the volume pane.
        var rows = Enumerable.Range((int)l.VolumePane.Y, (int)l.VolumePane.Height).ToList();
        var marked = rows.Count(y => Enumerable.Range(x0 - 1, 3).Any(x => Differs(a, b, x, y)));
        Assert.True(marked >= 0.9 * rows.Count, $"the vertical line is on {marked} of {rows.Count} volume rows: not solid");
        foreach (var y in rows.Where(y => Math.Abs(y - y0) > 4))
        {
            Assert.True(Enumerable.Range(x0 - 4, 9).Count(x => Differs(a, b, x, y)) <= MaxBand(l), $"the line is too thick at row {y}");
        }
    }

    // ---- crossed candles are brighter, the others are not ---------------------------------------------------------

    [Theory]
    [MemberData(nameof(Screens))]
    public void Every_crossed_candle_is_brighter_and_the_others_are_unchanged(string file)
    {
        var p = OnBody(file);
        var frame = p.Frame;
        var l = frame.Layout;
        var s = FrameView.Crosshair(frame)!;
        var a = p.Plain;
        var b = fx.Renderer.Render(frame);
        var block = Block(frame);
        var y0 = (int)Math.Floor(s.Y);
        var x0 = (int)Math.Floor(s.X);
        Assert.Contains(s.CandleIndex!.Value, s.HighlightedIndices);
        Assert.True(s.HighlightedIndices.Count >= 3, "the horizontal line should cross several candles on this chart");

        var litChecked = 0;
        foreach (var i in s.HighlightedIndices.Where(i => i > l.FirstDrawnIndex && i < frame.Candles.Count - 1))
        {
            var cx = (int)Math.Floor(l.CandleCenterX(i));
            var run = LongestRun(a, l, cx - 2, cx + 2);
            var rowsChecked = 0;
            for (var y = run.Top + 2; y <= run.Bottom - 2; y++)
            {
                if (Math.Abs(y - y0) <= 2 || Inside(block, cx, y, 3) || Math.Abs(cx - x0) <= 4)
                {
                    continue;
                }

                foreach (var x in new[] { cx - 2, cx + 2 })
                {
                    var d = Lum(b, x, y) - Lum(a, x, y);
                    Assert.True(d > 0.5, $"candle {i} pixel ({x}, {y}) is not brighter: luminance change {d:0.##}");
                    Assert.True(d <= 70, $"candle {i} pixel ({x}, {y}) is lit too much ({d:0.##}): the highlight should be slight");
                }

                rowsChecked++;
            }

            if (rowsChecked > 0)
            {
                litChecked++;
            }
        }

        Assert.True(litChecked >= 3, $"only {litChecked} crossed candles could be checked");

        // Every candle that is not crossed is exactly as without the crosshair (rows away from the lines and the block).
        var unchanged = 0;
        for (var i = l.FirstDrawnIndex + 1; i < frame.Candles.Count - 1; i++)
        {
            if (s.HighlightedIndices.Contains(i))
            {
                continue;
            }

            var cx = (int)Math.Floor(l.CandleCenterX(i));
            for (var y = (int)l.Plot.Y; y < (int)l.Plot.Bottom; y++)
            {
                if (Math.Abs(y - y0) <= 2 || Inside(block, cx, y, 3))
                {
                    continue;
                }

                for (var x = cx - 4; x <= cx + 4; x++)
                {
                    Assert.False(Differs(a, b, x, y), $"candle {i}, which no line crosses, changed at ({x}, {y})");
                }
            }

            unchanged++;
        }

        Assert.True(unchanged >= 20, $"only {unchanged} uncrossed candles checked");
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void A_cursor_that_crosses_no_candle_lights_none(string file)
    {
        var p = AboveAll(file);
        var s = FrameView.Crosshair(p.Frame)!;
        Assert.Empty(s.HighlightedIndices);
        Assert.Empty(Strays(p)); // nothing but the lines and the tags changed
    }
}
