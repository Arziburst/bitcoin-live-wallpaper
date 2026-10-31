using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Layout;

namespace BitcoinWallpaper.Chart.Core.Crosshair;

/// <summary>A cursor position in physical px, origin top-left of the wallpaper's monitor (like <see cref="ChartLayout"/>).</summary>
public readonly record struct CursorPx(double X, double Y);

/// <summary>A shown crosshair (D-35): a whole-monitor one, both lines edge to edge through the cursor; the chart's values
/// are null / false where they do not apply. Hidden is null.</summary>
/// <param name="X">The cursor's x, exactly: the vertical line follows the user's own cursor.</param>
/// <param name="Y">The cursor's y, exactly.</param>
/// <param name="Price">The price at <paramref name="Y"/> when it is in the plot's rows (any x); otherwise null.</param>
/// <param name="CandleIndex">The candle whose slot holds <paramref name="X"/> (any y), an index into the candles
/// (0 = oldest); null when x is over no candle.</param>
/// <param name="Candle">candles[CandleIndex]; null exactly when CandleIndex is.</param>
/// <param name="OverVolume">Y is in the volume pane's rows.</param>
public sealed record CrosshairState(double X, double Y, double? Price, int? CandleIndex, Candle? Candle, bool OverVolume)
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
    /// The crosshair for <paramref name="cursor"/>, or null (hidden) when paused or the cursor is not a finite point on the
    /// screen (x in [0, WidthPx), y in [0, HeightPx)). Anywhere on the screen it is shown; the price is there for y in the
    /// plot's rows, the candle for x over a candle's half-open slot [centre - pitch/2, centre + pitch/2) (the candle cut by
    /// the plot's left edge counts).
    /// </summary>
    /// <param name="priceMax">The price at the plot's top edge.</param>
    /// <param name="priceMin">The price at the plot's bottom edge.</param>
    public static CrosshairState? Compute(
        CursorPx cursor, ChartLayout layout, IReadOnlyList<Candle> candles, double priceMax, double priceMin, bool paused)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(candles);
        var (x, y) = (cursor.X, cursor.Y);
        if (paused || !double.IsFinite(x) || !double.IsFinite(y) || x < 0 || x >= layout.WidthPx || y < 0 || y >= layout.HeightPx)
        {
            return null;
        }

        var plot = layout.Plot;
        var volume = layout.VolumePane;
        var overPlotRows = y >= plot.Y && y < plot.Bottom;
        var overVolume = y >= volume.Y && y < volume.Bottom;
        var price = overPlotRows ? PriceAt(y, plot, priceMax, priceMin) : (double?)null;

        var first = Math.Max(0, layout.FirstDrawnIndex - 1);
        var count = Math.Min(candles.Count, layout.CandleCount);
        int? index = null;
        if (count > 0 && x >= plot.X && x < plot.Right && SlotAt(layout, x) is var slot && slot >= first && slot < count)
        {
            index = slot;
        }

        var candle = index is { } at ? candles[at] : null;
        var offset = index is { } i0 ? Math.Abs(x - layout.CandleCenterX(i0)) : double.PositiveInfinity;

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
        if (price is not null && candle is not null)
        {
            double YOf(decimal value) => plot.Y + ((priceMax - (double)value) / (priceMax - priceMin) * plot.Height);
            var inBody = offset <= layout.CandleBodyWidthPx / 2 && Between(y, YOf(candle.Open), YOf(candle.Close));
            var onWick = offset <= (layout.WickWidthPx / 2) + 2 && Between(y, YOf(candle.High), YOf(candle.Low));
            onCandle = inBody || onWick;
        }

        return new CrosshairState(x, y, price, index, candle, overVolume) { HighlightedIndices = lit, OnCandle = onCandle };
    }

    /// <summary>The price at <paramref name="y"/>: linear, <paramref name="priceMax"/> at plot.Y, <paramref name="priceMin"/>
    /// at plot.Bottom.</summary>
    public static double PriceAt(double y, RectPx plot, double priceMax, double priceMin) =>
        priceMax - ((y - plot.Y) / plot.Height * (priceMax - priceMin));

    private static bool Between(double v, double a, double b) => v >= Math.Min(a, b) && v <= Math.Max(a, b);

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
