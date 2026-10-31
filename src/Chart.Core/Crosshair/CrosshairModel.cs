using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Layout;

namespace BitcoinWallpaper.Chart.Core.Crosshair;

/// <summary>A cursor position in physical px, origin top-left of the wallpaper's monitor (like <see cref="ChartLayout"/>).</summary>
public readonly record struct CursorPx(double X, double Y);

/// <summary>A shown crosshair (D-35); hidden is null.</summary>
/// <param name="X">The cursor's x, exactly: the vertical line follows the user's own cursor.</param>
/// <param name="Y">The cursor's y, exactly.</param>
/// <param name="Price">The price at <paramref name="Y"/>; null over the volume pane (no horizontal line there).</param>
/// <param name="CandleIndex">The candle whose slot holds <paramref name="X"/>, an index into the candles (0 = oldest).</param>
/// <param name="Candle">candles[CandleIndex].</param>
/// <param name="OverVolume">The cursor is over the volume pane.</param>
public sealed record CrosshairState(double X, double Y, double? Price, int CandleIndex, Candle Candle, bool OverVolume)
{
    /// <summary>The candles a crosshair line crosses, ascending, drawn slightly brighter: the horizontal line crosses each
    /// candle whose low..high holds the price; the vertical line the slot's candle when it passes through its body or
    /// wick columns.</summary>
    public IReadOnlyList<int> HighlightedIndices { get; init; } = [];

    /// <summary>The cursor is on the slot's candle (its body or its wick): the candle's values are shown next to it.</summary>
    public bool OnCandle { get; init; }

    // Value equality also over the highlighted indices (a list compares by reference).
    public bool Equals(CrosshairState? other) =>
        other is not null
        && X.Equals(other.X) && Y.Equals(other.Y) && Price.Equals(other.Price) && CandleIndex == other.CandleIndex
        && Equals(Candle, other.Candle) && OverVolume == other.OverVolume && OnCandle == other.OnCandle
        && HighlightedIndices.SequenceEqual(other.HighlightedIndices);

    public override int GetHashCode() => HashCode.Combine(X, Y, Price, CandleIndex, Candle, OverVolume, OnCandle, HighlightedIndices.Count);
}

/// <summary>Where the crosshair is and what it shows, from the cursor, the layout and the price scale (D-35). Pure.</summary>
public static class CrosshairModel
{
    /// <summary>
    /// The crosshair for <paramref name="cursor"/>, or null (hidden) when paused, the cursor is not a finite point, there
    /// are no candles, the cursor is neither over the plot nor over the volume pane (the price-axis column, the time
    /// axis, the gap between the panes and the margins hide it), or no candle exists in the slot under it. Slots are
    /// half-open, [centre - pitch/2, centre + pitch/2); the candle cut by the plot's left edge counts.
    /// </summary>
    /// <param name="priceMax">The price at the plot's top edge.</param>
    /// <param name="priceMin">The price at the plot's bottom edge.</param>
    public static CrosshairState? Compute(
        CursorPx cursor, ChartLayout layout, IReadOnlyList<Candle> candles, double priceMax, double priceMin, bool paused)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(candles);
        var (x, y) = (cursor.X, cursor.Y);
        if (paused || !double.IsFinite(x) || !double.IsFinite(y) || candles.Count == 0)
        {
            return null;
        }

        var plot = layout.Plot;
        var volume = layout.VolumePane;
        if (x < plot.X || x >= plot.Right)
        {
            return null;
        }

        var overPlot = y >= plot.Y && y < plot.Bottom;
        var overVolume = !overPlot && y >= volume.Y && y < volume.Bottom;
        if (!overPlot && !overVolume)
        {
            return null;
        }

        var first = Math.Max(0, layout.FirstDrawnIndex - 1);
        var count = Math.Min(candles.Count, layout.CandleCount);
        var index = SlotAt(layout, x);
        if (index < first || index >= count)
        {
            return null;
        }

        var price = overPlot ? PriceAt(y, plot, priceMax, priceMin) : (double?)null;
        var candle = candles[index];
        var offset = Math.Abs(x - layout.CandleCenterX(index));

        // The vertical line crosses the slot's candle on its body's (or wick's) columns, with a px of antialiasing.
        var crossedByVertical = offset <= (layout.CandleBodyWidthPx / 2) + 1;
        var lit = new List<int>();
        for (var i = first; i < count; i++)
        {
            var c = candles[i];
            if ((price is { } p && (double)c.Low <= p && p <= (double)c.High) || (i == index && crossedByVertical))
            {
                lit.Add(i);
            }
        }

        var onCandle = false;
        if (price is not null)
        {
            double YOf(decimal value) => plot.Y + ((priceMax - (double)value) / (priceMax - priceMin) * plot.Height);
            var inBody = offset <= layout.CandleBodyWidthPx / 2 && Between(y, YOf(candle.Open), YOf(candle.Close));
            var onWick = offset <= (layout.WickWidthPx / 2) + 2 && Between(y, YOf(candle.High), YOf(candle.Low));
            onCandle = inBody || onWick;
        }

        return new CrosshairState(x, y, price, index, candle, overVolume) { HighlightedIndices = lit, OnCandle = onCandle };
    }

    private static bool Between(double v, double a, double b) => v >= Math.Min(a, b) && v <= Math.Max(a, b);

    /// <summary>The price at <paramref name="y"/>: linear, <paramref name="priceMax"/> at plot.Y, <paramref name="priceMin"/>
    /// at plot.Bottom.</summary>
    public static double PriceAt(double y, RectPx plot, double priceMax, double priceMin) =>
        priceMax - ((y - plot.Y) / plot.Height * (priceMax - priceMin));

    // The slot holding x: the index whose [centre - pitch/2, centre + pitch/2) contains it. The layout's centres are
    // exact (whole px and half pitches), so the boundaries are too; the two checks only guard rounding.
    private static int SlotAt(ChartLayout layout, double x)
    {
        var pitch = layout.CandlePitchPx;
        var index = (int)Math.Floor((x - (layout.CandleCenterX(0) - (pitch / 2))) / pitch);
        if (x < layout.CandleCenterX(index) - (pitch / 2))
        {
            index--;
        }
        else if (x >= layout.CandleCenterX(index) + (pitch / 2))
        {
            index++;
        }

        return index;
    }
}
