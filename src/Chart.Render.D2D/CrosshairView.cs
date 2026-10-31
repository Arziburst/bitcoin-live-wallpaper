using System.Globalization;
using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Crosshair;
using BitcoinWallpaper.Chart.Core.Layout;

namespace BitcoinWallpaper.Chart.Render.D2D;

public enum CrosshairItemKind
{
    PriceTag,
    TimeTag,
    InfoBlock,
}

/// <summary>One thing drawn for the crosshair (D-35). Bounds in physical px: a tag's filled box, the info block's box.</summary>
public sealed record CrosshairItem(CrosshairItemKind Kind, string Text, RectPx Bounds);

/// <summary>What a frame draws for its crosshair (D-35), computed from the frame alone; empty when hidden. Each part is
/// its own member, so another part (a column band, say) is one more member and one more draw step.</summary>
internal sealed class CrosshairView
{
    /// <summary>The info block's padding and its gap to the cursor, in em of the axis label size.</summary>
    public const double BlockPadding = 0.6;

    public const double BlockGap = 0.8;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public CrosshairView(Scene scene, CrosshairState? state)
    {
        State = state;
        if (state is null)
        {
            return;
        }

        var items = new List<CrosshairItem>();
        if (state.Price is { } price)
        {
            items.Add(PriceTag(scene.Layout, price, state.Y));
        }

        items.Add(TimeTag(scene, state));
        if (state.OnCandle)
        {
            items.Add(InfoBlock(scene, state));
        }

        Items = items;
    }

    public CrosshairState? State { get; }

    /// <summary>Order: price tag, time tag, info block (only with the cursor on a candle); drawn in this order after
    /// everything else.</summary>
    public IReadOnlyList<CrosshairItem> Items { get; } = [];

    /// <summary>The candles a crosshair line crosses (the model's), drawn slightly brighter.</summary>
    public IReadOnlyList<int> LitCandles => State?.HighlightedIndices ?? [];

    /// <summary>The candle's open time in the frame's time zone, as the time tag and the info block show it.</summary>
    public static string Time(Candle candle, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(candle.OpenTime, zone).ToString("d MMM HH:mm", Invariant);

    /// <summary>The info block's three lines: time; O/H/L/C; volume.</summary>
    public static string[] InfoLines(Candle c, TimeZoneInfo zone) =>
    [
        Time(c, zone),
        $"O {ChartText.Price(c.Open)}  H {ChartText.Price(c.High)}  L {ChartText.Price(c.Low)}  C {ChartText.Price(c.Close)}",
        $"Vol {c.Volume.ToString("#,##0", Invariant)}",
    ];

    // Like the D-32 tags: centred in the price-axis column, the same height and padding.
    private static CrosshairItem PriceTag(ChartLayout l, double price, double y)
    {
        var size = l.Text.AxisLabelPx;
        var text = ChartText.Price((decimal)price);
        var height = Scene.TagHeight * size;
        var width = Math.Ceiling((text.Length * Scene.MonoAdvance * size) + (2 * Scene.TagPadding * size));
        var centre = l.PriceAxis.X + (l.PriceAxis.Width / 2);
        return new CrosshairItem(CrosshairItemKind.PriceTag, text, new RectPx(centre - (width / 2), y - (height / 2), width, height));
    }

    // On the time axis, centred on the line; kept on the screen at its edges.
    private static CrosshairItem TimeTag(Scene s, CrosshairState state)
    {
        var l = s.Layout;
        var size = l.Text.AxisLabelPx;
        var text = Time(state.Candle, s.Frame.TimeZone);
        var height = Scene.TagHeight * size;
        var width = Math.Ceiling((text.Length * Scene.MonoAdvance * size) + (2 * Scene.TagPadding * size));
        var x = Math.Clamp(state.X - (width / 2), 0, Math.Max(0, l.WidthPx - width));
        var y = l.TimeAxis.Y + ((l.TimeAxis.Height - height) / 2);
        return new CrosshairItem(CrosshairItemKind.TimeTag, text, new RectPx(x, y, width, height));
    }

    // Next to the cursor: above and right of it by default; left of it when the right side leaves the plot; below it
    // when there is no room above (then on the left first, away from the arrow of the mouse pointer). Always inside the
    // plot.
    private static CrosshairItem InfoBlock(Scene s, CrosshairState state)
    {
        var l = s.Layout;
        var plot = l.Plot;
        var size = l.Text.AxisLabelPx;
        var lines = InfoLines(state.Candle, s.Frame.TimeZone);
        var pad = BlockPadding * size;
        var gap = BlockGap * size;
        var width = Math.Ceiling((lines.Max(line => line.Length) * Scene.MonoAdvance * size) + (2 * pad));
        var height = Math.Ceiling((lines.Length * Scene.MonoLineHeight * size) + (2 * pad));

        var (x, y) = (state.X, state.Y);
        var above = y - gap - height >= plot.Y;
        var right = x + gap + width <= plot.Right;
        var left = x - gap - width >= plot.X;
        var toRight = above ? right : !left && right;
        var bx = toRight ? x + gap : x - gap - width;
        var by = above ? y - gap - height : y + gap;
        bx = Math.Clamp(bx, plot.X, Math.Max(plot.X, plot.Right - width));
        by = Math.Clamp(by, plot.Y, Math.Max(plot.Y, plot.Bottom - height));
        return new CrosshairItem(CrosshairItemKind.InfoBlock, string.Join("\n", lines), new RectPx(bx, by, width, height));
    }
}
