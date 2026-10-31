// Contract #18, owner 2026-10-31 (round 7, with a screenshot): the price-axis block - the price labels and the price
// tags (last price, 24h high / low) - is centred horizontally in the strip between the newest candle (Plot.Right,
// right offset 0) and the right screen edge; nothing hugs the candles with empty space to its right.
//
// Observed from the pixels only (no new API): everything right of the plot is the price-axis column (grid, lines,
// candles and volume are clipped to the plot and the volume pane), so whatever differs there from the background
// is an axis label or tag. Background = the pixel at the screen's last column in the same row (the content keeps a
// padding from the edge, so that pixel is background).
// The per-item check (PriceAxisItemsTests) pins each label and tag through FrameView.PriceAxisItems.

using BitcoinWallpaper.Chart.Render.D2D;

namespace BitcoinWallpaper.Chart.Render.Tests;

[Collection(nameof(RendererCollection))]
public class PriceAxisCentringTests(RendererFixture fx)
{
    private const string Desk = "btc-5120x1440.png";
    private const string Tv = "btc-3840x2160.png";

    /// <summary>A pixel is content when it differs from the row's background by more than this (sum over B, G, R).</summary>
    private const int Threshold = 30;

    private sealed record Run(int Top, int Bottom, int MinX, int MaxX)
    {
        public double Centre => (MinX + MaxX + 1) / 2.0;
    }

    /// <summary>Runs of consecutive rows with content right of the plot, each with its horizontal extent.</summary>
    private static List<Run> Runs(BgraImage image, ChartFrame frame)
    {
        var l = frame.Layout;
        var from = (int)Math.Ceiling(l.Plot.Right);
        var runs = new List<Run>();
        Run? open = null;
        for (var y = (int)Math.Ceiling(l.Plot.Y); y < (int)l.Plot.Bottom; y++)
        {
            var bg = ((y * image.Width) + image.Width - 1) * 4;
            int min = int.MaxValue, max = -1;
            for (var x = from; x < image.Width - 1; x++)
            {
                var i = ((y * image.Width) + x) * 4;
                var d = Math.Abs(image.Pixels[i] - image.Pixels[bg]) + Math.Abs(image.Pixels[i + 1] - image.Pixels[bg + 1]) +
                        Math.Abs(image.Pixels[i + 2] - image.Pixels[bg + 2]);
                if (d > Threshold)
                {
                    min = Math.Min(min, x);
                    max = Math.Max(max, x);
                }
            }

            if (max < 0)
            {
                if (open is not null)
                {
                    runs.Add(open);
                    open = null;
                }

                continue;
            }

            open = open is null ? new Run(y, y, min, max) : open with { Bottom = y, MinX = Math.Min(open.MinX, min), MaxX = Math.Max(open.MaxX, max) };
        }

        if (open is not null)
        {
            runs.Add(open);
        }

        return runs;
    }

    public static TheoryData<string, bool> Frames => new()
    {
        { Desk, false },
        { Tv, false },
        { Desk, true },  // 24h high / low on the scale: their tags are drawn too
        { Tv, true },
    };

    private ChartFrame FrameFor(string fileName, bool dayTags)
    {
        var frame = fx.Frame(fileName);
        if (!dayTags)
        {
            return frame;
        }

        // The 24h extremes inside the visible range, far from the last price: day tags in the axis.
        var visible = frame.Candles.Skip(frame.Layout.FirstDrawnIndex).ToArray();
        var hi = visible.Max(c => c.High);
        var lo = visible.Min(c => c.Low);
        var range = hi - lo;
        return frame with { Ticker = frame.Ticker! with { High = Math.Round(hi - (0.05m * range), 2), Low = Math.Round(lo + (0.05m * range), 2) } };
    }

    [Theory]
    [MemberData(nameof(Frames))]
    public void The_price_axis_block_is_centred_between_the_newest_candle_and_the_screen_edge(string fileName, bool dayTags)
    {
        var frame = FrameFor(fileName, dayTags);
        var l = frame.Layout;
        var image = fx.Renderer.Render(frame);
        var runs = Runs(image, frame);
        Assert.True(runs.Count >= 3, $"{runs.Count} labels / tags found right of the plot");

        var stripLeft = l.Plot.Right;
        var stripRight = (double)l.WidthPx;
        var stripCentre = (stripLeft + stripRight) / 2;
        var size = l.Text.AxisLabelPx;

        // The block as a whole (its widest item, a tag): centred within 2 px, with padding on both sides.
        var minX = runs.Min(r => r.MinX);
        var maxX = runs.Max(r => r.MaxX);
        var blockCentre = (minX + maxX + 1) / 2.0;
        Assert.True(Math.Abs(blockCentre - stripCentre) <= 2,
            $"the price-axis block spans x {minX}..{maxX} (centre {blockCentre:0.#}); the strip {stripLeft:0.#}..{stripRight} has its centre at {stripCentre:0.#}");
        Assert.True(minX - stripLeft >= 0.2 * size && stripRight - (maxX + 1) >= 0.2 * size,
            $"the price-axis block x {minX}..{maxX} has under {0.2 * size:0.#} px padding in the strip {stripLeft:0.#}..{stripRight}");

        // Every label and tag on its own: centred (glyph side bearings allowed: 0.15 em + 1 px).
        foreach (var run in runs)
        {
            Assert.True(Math.Abs(run.Centre - stripCentre) <= (0.15 * size) + 1,
                $"the label / tag in rows {run.Top}..{run.Bottom} spans x {run.MinX}..{run.MaxX} (centre {run.Centre:0.#}), " +
                $"the strip centre is {stripCentre:0.#}");
        }
    }
}
