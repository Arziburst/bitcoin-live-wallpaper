using BitcoinWallpaper.Chart.Core.Candles;

namespace BitcoinWallpaper.Chart.Core.Indicators;

public readonly record struct HighLow(decimal High, decimal Low);

/// <summary>A price change; Percent 1.5 means 1.5 %.</summary>
public readonly record struct PriceChange(decimal Absolute, decimal Percent);

/// <summary>24 h statistics for the headline and the 24h high/low lines.</summary>
public static class DayStats
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    public static HighLow HighLowFromTicker(Ticker24h ticker)
    {
        ArgumentNullException.ThrowIfNull(ticker);
        return new HighLow(ticker.High, ticker.Low);
    }

    public static PriceChange ChangeFromTicker(Ticker24h ticker)
    {
        ArgumentNullException.ThrowIfNull(ticker);
        return new PriceChange(ticker.ChangeAbs, ticker.ChangePercent);
    }

    /// <summary>Max High / min Low over the candles whose interval end (OpenTime + 5 min) is after now - 24 h,
    /// the forming candle included. No such candle: (0, 0).</summary>
    public static HighLow HighLowFromCandles(IReadOnlyList<Candle> candles, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candles);
        var since = now - Day;
        decimal? high = null, low = null;
        foreach (var candle in candles)
        {
            if (candle.OpenTime + Interval <= since)
            {
                continue;
            }

            high = high is { } h ? Math.Max(h, candle.High) : candle.High;
            low = low is { } l ? Math.Min(l, candle.Low) : candle.Low;
        }

        return new HighLow(high ?? 0, low ?? 0);
    }
}
