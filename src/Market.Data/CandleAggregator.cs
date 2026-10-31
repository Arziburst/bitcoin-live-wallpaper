using BitcoinWallpaper.Chart.Core.Candles;

namespace BitcoinWallpaper.Market.Data;

/// <summary>Candles of an interval an exchange does not offer (Kraken has no 6h), made from a finer one.</summary>
public static class CandleAggregator
{
    /// <summary>Groups the source candles (ascending, one finer interval that divides <paramref name="interval"/>)
    /// into buckets aligned to multiples of the interval since the Unix epoch (6h: 00/06/12/18 UTC): O = the first
    /// open, H = max, L = min, C = the last close, V = the sum; IsClosed = bucket start + interval &lt;= now. A
    /// leading bucket whose first source candle is not at the bucket start is incomplete and dropped; later buckets
    /// keep what they have.</summary>
    public static IReadOnlyList<Candle> Aggregate(IReadOnlyList<Candle> candles, TimeSpan interval, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        var result = new List<Candle>();
        var i = 0;
        while (i < candles.Count)
        {
            var start = BucketStart(candles[i].OpenTime, interval);
            var end = i;
            while (end < candles.Count && BucketStart(candles[end].OpenTime, interval) == start)
            {
                end++;
            }

            if (result.Count > 0 || candles[i].OpenTime == start)
            {
                result.Add(Bucket(candles, i, end, start, interval, now));
            }

            i = end;
        }

        return result;
    }

    /// <summary>The start of the bucket holding <paramref name="time"/>, in UTC.</summary>
    public static DateTimeOffset BucketStart(DateTimeOffset time, TimeSpan interval) =>
        new(time.UtcTicks - (time.UtcTicks % interval.Ticks), TimeSpan.Zero);

    /// <summary>One bucket from the source candles [from, to) that all lie in it.</summary>
    internal static Candle Bucket(IReadOnlyList<Candle> candles, int from, int to, DateTimeOffset start, TimeSpan interval, DateTimeOffset now)
    {
        var high = candles[from].High;
        var low = candles[from].Low;
        var volume = 0m;
        for (var k = from; k < to; k++)
        {
            high = Math.Max(high, candles[k].High);
            low = Math.Min(low, candles[k].Low);
            volume += candles[k].Volume;
        }

        return new Candle(start, candles[from].Open, high, low, candles[to - 1].Close, volume, start + interval <= now);
    }
}
