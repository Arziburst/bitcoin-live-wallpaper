using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Layout;

namespace BitcoinWallpaper.Chart.Render.D2D;

/// <summary>Price to y within the plot: Max at the plot's top edge, Min at its bottom edge.</summary>
internal readonly record struct PriceScale(double Top, double Height, double Max, double Min)
{
    public double Y(double price) => Top + ((Max - price) / (Max - Min) * Height);

    public double Y(decimal price) => Y((double)price);

    /// <summary>The visible candles (and the averages drawn over them) with a margin above and below; candles under
    /// the headline stay below it, so the headline's scrim never covers a candle.</summary>
    public static PriceScale For(
        ChartLayout layout,
        IReadOnlyList<Candle> candles,
        IReadOnlyList<IReadOnlyList<decimal?>> overlays)
    {
        var plot = layout.Plot;
        double hi = double.MinValue, lo = double.MaxValue;
        for (var i = layout.FirstDrawnIndex; i < candles.Count; i++)
        {
            hi = Math.Max(hi, (double)candles[i].High);
            lo = Math.Min(lo, (double)candles[i].Low);
            foreach (var overlay in overlays)
            {
                if (overlay[i] is { } v)
                {
                    hi = Math.Max(hi, (double)v);
                    lo = Math.Min(lo, (double)v);
                }
            }
        }

        if (hi < lo)
        {
            return new PriceScale(plot.Y, plot.Height, 1, 0);
        }

        // The 24h high / low never stretch the scale (D-11): off it, they become edge markers.
        var range = Math.Max(hi - lo, Math.Max(hi * 0.001, 1e-6));
        var max = hi + (0.07 * range);
        var min = lo - (0.07 * range);

        // Under the headline: the candle's high must map below the headline's bottom edge plus a gap.
        var headline = layout.Headline;
        var clear = Math.Min(0.5, (headline.Bottom + (0.02 * layout.HeightPx) - plot.Y) / plot.Height);
        var underHigh = double.MinValue;
        for (var i = layout.FirstDrawnIndex; i < candles.Count; i++)
        {
            var x = layout.CandleCenterX(i);
            if (x >= headline.X - layout.CandlePitchPx && x <= headline.Right + layout.CandlePitchPx)
            {
                underHigh = Math.Max(underHigh, (double)candles[i].High);
                foreach (var overlay in overlays)
                {
                    if (overlay[i] is { } v)
                    {
                        underHigh = Math.Max(underHigh, (double)v);
                    }
                }
            }
        }

        if (underHigh > double.MinValue && clear > 0)
        {
            max = Math.Max(max, (underHigh - (clear * min)) / (1 - clear));
        }

        return new PriceScale(plot.Y, plot.Height, max, min);
    }
}

internal static class Ticks
{
    /// <summary>Round price levels about <paramref name="spacingPx"/> apart: steps of 1, 2, 2.5 or 5 x 10^k.</summary>
    public static (double Step, IReadOnlyList<double> Levels) Prices(PriceScale scale, double spacingPx)
    {
        var count = Math.Max(2, scale.Height / spacingPx);
        var raw = (scale.Max - scale.Min) / count;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * magnitude).First(s => s >= raw);
        var levels = new List<double>();
        for (var p = Math.Ceiling(scale.Min / step) * step; p <= scale.Max; p += step)
        {
            levels.Add(Math.Round(p / step) * step);
        }

        return (step, levels);
    }

    /// <summary>The smallest of 30, 60, 120, ... minutes whose labels are at least <paramref name="minSpacingPx"/>
    /// apart at the candle pitch of <paramref name="candleMinutes"/>-minute candles.</summary>
    public static int TimeIntervalMinutes(double pitchPx, double candleMinutes, double minSpacingPx)
    {
        foreach (var minutes in new[] { 30, 60, 120, 180, 240, 360, 720, 1440 })
        {
            if (minutes / candleMinutes * pitchPx >= minSpacingPx)
            {
                return minutes;
            }
        }

        return 1440;
    }
}
