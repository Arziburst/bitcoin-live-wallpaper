using BitcoinWallpaper.Chart.Core.Candles;

namespace BitcoinWallpaper.Chart.Core.Indicators;

public static class MovingAverage
{
    /// <summary>Simple moving average of Close, one entry per candle: null while i &lt; period - 1, else the mean
    /// of Close over candles i-period+1 .. i.</summary>
    public static IReadOnlyList<decimal?> Simple(IReadOnlyList<Candle> candles, int period)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);

        var result = new decimal?[candles.Count];
        var sum = 0m;
        for (var i = 0; i < candles.Count; i++)
        {
            sum += candles[i].Close;
            if (i >= period)
            {
                sum -= candles[i - period].Close;
            }

            if (i >= period - 1)
            {
                result[i] = sum / period;
            }
        }

        return result;
    }
}
