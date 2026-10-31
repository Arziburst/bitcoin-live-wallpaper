using System.Globalization;

namespace BitcoinWallpaper.Chart.Core.Candles;

public static class CandleInterval
{
    /// <summary>The interval before D-13, and of the existing feeds' default.</summary>
    public static TimeSpan FiveMinutes { get; } = TimeSpan.FromMinutes(5);

    /// <summary>The short name in Binance notation: 5m, 1h, 4h, 1d.</summary>
    public static string Code(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        var minutes = interval.TotalMinutes;
        return minutes % 1440 == 0 ? Format(minutes / 1440, "d")
            : minutes % 60 == 0 ? Format(minutes / 60, "h")
            : Format(minutes, "m");
    }

    private static string Format(double value, string unit) => value.ToString("0.##", CultureInfo.InvariantCulture) + unit;
}
