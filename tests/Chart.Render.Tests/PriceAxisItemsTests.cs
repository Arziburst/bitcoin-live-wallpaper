// Contract #18, owner 2026-10-31 (round 7): every price-axis label and price tag is centred in the price-axis column
// (which spans Plot.Right .. the screen width): its centre within +-1 px of the column centre, the widest tag still
// inside the column with padding, and the tag text inside the tag with padding.
//
// API SKETCH (written before the code, #237), in src/Chart.Render.D2D/, next to FrameView:
//
//   public enum PriceAxisItemKind { Level, LastPrice, DayHigh, DayLow }
//
//   // One thing DrawPriceAxis draws. Bounds in physical px: for a Level label its text box (JetBrains Mono:
//   // width = Text.Length * 0.6 * AxisLabelPx, height = 1.32 * AxisLabelPx); for a tag its filled box.
//   public sealed record PriceAxisItem(PriceAxisItemKind Kind, string Text, RectPx Bounds);
//
//   public static class FrameView
//   {
//       // Exactly what the renderer draws in the price-axis column, top to bottom: the tick labels shown and the
//       // tags (last price; 24h high / low when on the scale and not hidden by the last-price tag).
//       public static IReadOnlyList<PriceAxisItem> PriceAxisItems(ChartFrame frame);
//   }

using System.Globalization;
using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Layout;
using BitcoinWallpaper.Chart.Render.D2D;

namespace BitcoinWallpaper.Chart.Render.Tests;

[Collection(nameof(RendererCollection))]
public class PriceAxisItemsTests(RendererFixture fx)
{
    private const string Desk = "btc-5120x1440.png";
    private const string Tv = "btc-3840x2160.png";

    public static TheoryData<string, bool> Frames => new()
    {
        { Desk, false },
        { Tv, false },
        { Desk, true },
        { Tv, true },
    };

    private ChartFrame FrameFor(string fileName, bool dayTags)
    {
        var frame = fx.Frame(fileName);
        if (!dayTags)
        {
            return frame;
        }

        var visible = frame.Candles.Skip(frame.Layout.FirstDrawnIndex).ToArray();
        var hi = visible.Max(c => c.High);
        var lo = visible.Min(c => c.Low);
        var range = hi - lo;
        return frame with { Ticker = frame.Ticker! with { High = Math.Round(hi - (0.05m * range), 2), Low = Math.Round(lo + (0.05m * range), 2) } };
    }

    private static double CentreX(RectPx r) => r.X + (r.Width / 2);

    [Theory]
    [MemberData(nameof(Frames))]
    public void Every_label_and_tag_is_centred_in_the_price_axis_column(string fileName, bool dayTags)
    {
        var frame = FrameFor(fileName, dayTags);
        var l = frame.Layout;
        var column = l.PriceAxis;
        var size = l.Text.AxisLabelPx;
        var items = FrameView.PriceAxisItems(frame);

        Assert.True(items.Count(i => i.Kind == PriceAxisItemKind.Level) >= 3, "fewer than 3 price labels");
        var last = Assert.Single(items, i => i.Kind == PriceAxisItemKind.LastPrice);
        Assert.Equal(frame.Candles[^1].Close.ToString("#,##0.00", CultureInfo.InvariantCulture), last.Text);
        if (dayTags)
        {
            Assert.Contains(items, i => i.Kind is PriceAxisItemKind.DayHigh or PriceAxisItemKind.DayLow);
        }

        foreach (var item in items)
        {
            var b = item.Bounds;
            Assert.True(Math.Abs(CentreX(b) - CentreX(column)) <= 1,
                $"{item.Kind} '{item.Text}' {b} has its centre at {CentreX(b):0.#}, the column {column} at {CentreX(column):0.#}");
            Assert.True(b.X - column.X >= 0.2 * size && column.Right - b.Right >= 0.2 * size,
                $"{item.Kind} '{item.Text}' {b} is not inside the column {column} with {0.2 * size:0.#} px padding");
        }
    }

    [Theory]
    [MemberData(nameof(Frames))]
    public void A_tag_holds_its_text_with_inner_padding(string fileName, bool dayTags)
    {
        var frame = FrameFor(fileName, dayTags);
        var size = frame.Layout.Text.AxisLabelPx;
        foreach (var tag in FrameView.PriceAxisItems(frame).Where(i => i.Kind != PriceAxisItemKind.Level))
        {
            var text = tag.Text.Length * 0.6 * size;
            Assert.True(tag.Bounds.Width >= text + (0.5 * size),
                $"{tag.Kind} tag '{tag.Text}' is {tag.Bounds.Width:0.#} px wide for {text:0.#} px of text: under {0.25 * size:0.#} px padding per side");
        }
    }

    [Fact]
    public void The_widest_possible_tag_still_fits_centred()
    {
        // A six-figure price ("123,456.78", 10 characters) on the desk and the TV.
        foreach (var (w, h, s) in new[] { (5120, 1440, 1.75), (3840, 2160, 1.0) })
        {
            var open = new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);
            var candles = Enumerable.Range(0, 60)
                .Select(i => new Candle(open.AddHours(4 * (i - 59)), 123000m + i, 123500m + i, 122900m + i, 123456.78m + i - 59, 10m, i < 59))
                .ToArray();
            var frame = ChartFrame.Create(w, h, s, candles, null, open.AddMinutes(30), TimeZoneInfo.Utc, interval: TimeSpan.FromHours(4));
            var column = frame.Layout.PriceAxis;
            var size = frame.Layout.Text.AxisLabelPx;

            var last = Assert.Single(FrameView.PriceAxisItems(frame), i => i.Kind == PriceAxisItemKind.LastPrice);
            Assert.Equal("123,456.78", last.Text);
            Assert.True(Math.Abs(CentreX(last.Bounds) - CentreX(column)) <= 1, $"{w}x{h}: last-price tag {last.Bounds} not centred in {column}");
            Assert.True(last.Bounds.X - column.X >= 0.2 * size && column.Right - last.Bounds.Right >= 0.2 * size,
                $"{w}x{h}: the 10-character tag {last.Bounds} does not fit the column {column} with padding");
            Assert.True(last.Bounds.Width >= (10 * 0.6 * size) + (0.5 * size), $"{w}x{h}: the tag {last.Bounds} is cut short");
        }
    }
}
