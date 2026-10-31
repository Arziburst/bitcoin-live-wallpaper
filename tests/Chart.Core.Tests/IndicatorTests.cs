// Contract #4, criterion 3: MA20/MA50, 24h high/low and the "closes in mm:ss" text.
//
// API these tests need (the worker implements it as written; files in src/Chart.Core/Indicators/):
//
//   namespace BitcoinWallpaper.Chart.Core;
//
//   // One kline. Binance style: CloseTime = OpenTime + interval - 1 ms.
//   public sealed record Candle(DateTimeOffset OpenTime, DateTimeOffset CloseTime,
//                               decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);
//
//   // Rolling 24h ticker (Binance /ticker/24hr or miniTicker).
//   public sealed record Ticker24h(decimal LastPrice, decimal OpenPrice, decimal HighPrice,
//                                  decimal LowPrice, decimal Volume);
//
//   namespace BitcoinWallpaper.Chart.Core.Indicators;
//
//   public static class MovingAverage
//   {
//       // Simple moving average of Close. Result has one entry per candle; entry i is null while
//       // i < period - 1, else the mean of Close over candles i-period+1 .. i.
//       // period <= 0 -> ArgumentOutOfRangeException.
//       public static IReadOnlyList<decimal?> Simple(IReadOnlyList<Candle> candles, int period);
//   }
//
//   public readonly record struct HighLow(decimal High, decimal Low);
//   public readonly record struct PriceChange(decimal Absolute, decimal Percent);  // Percent: 1.5 means 1.5 %
//
//   public static class DayStats
//   {
//       public static HighLow HighLowFromTicker(Ticker24h ticker);              // ticker High / Low
//       public static PriceChange ChangeFromTicker(Ticker24h ticker);           // Last - Open, (Last - Open) / Open * 100
//       // Max High / min Low over candles with CloseTime > now - 24h (the forming candle included).
//       public static HighLow HighLowFromCandles(IReadOnlyList<Candle> candles, DateTimeOffset now);
//   }
//
//   public static class CloseCountdown
//   {
//       // "closes in mm:ss": time from now to closeTime, rounded UP to whole seconds, never negative,
//       // two-digit minutes and seconds, culture-invariant.
//       public static string Text(DateTimeOffset closeTime, DateTimeOffset now);
//   }

using BitcoinWallpaper.Chart.Core.Indicators;

namespace BitcoinWallpaper.Chart.Core.Tests;

public class IndicatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Five = TimeSpan.FromMinutes(5);

    // 5m candles from T0. Open = previous close, High/Low = close +/- 1, so a mean over anything but
    // Close gives a different number.
    private static List<Candle> Series(IEnumerable<decimal> closes)
    {
        var list = new List<Candle>();
        var prev = 0m;
        var i = 0;
        foreach (var c in closes)
        {
            var open = T0 + i * Five;
            list.Add(new Candle(open, open + Five - TimeSpan.FromMilliseconds(1), prev, c + 1, c - 1, c, 1m));
            prev = c;
            i++;
        }
        return list;
    }

    private static List<Candle> OneToSixty() => Series(Enumerable.Range(1, 60).Select(x => (decimal)x));

    // ---- MA20 / MA50 --------------------------------------------------------------------------

    [Fact]
    public void Ma20_is_the_simple_mean_of_the_last_20_closes()
    {
        var ma = MovingAverage.Simple(OneToSixty(), 20);
        Assert.Equal(60, ma.Count);
        for (var i = 0; i < 19; i++)
            Assert.Null(ma[i]);
        Assert.Equal(10.5m, ma[19]);   // mean(1..20)
        Assert.Equal(21.5m, ma[30]);   // mean(12..31)
        Assert.Equal(50.5m, ma[59]);   // mean(41..60)
    }

    [Fact]
    public void Ma50_is_the_simple_mean_of_the_last_50_closes()
    {
        var ma = MovingAverage.Simple(OneToSixty(), 50);
        Assert.Equal(60, ma.Count);
        Assert.Null(ma[48]);
        Assert.Equal(25.5m, ma[49]);   // mean(1..50)
        Assert.Equal(35.5m, ma[59]);   // mean(11..60)
    }

    [Fact]
    public void Ma20_is_simple_not_exponential()
    {
        // 20 closes of 100, then a spike to 300, then 100 again.
        var closes = Enumerable.Repeat(100m, 20).Append(300m).Append(100m);
        var ma = MovingAverage.Simple(Series(closes), 20);
        Assert.Equal(100m, ma[19]);
        Assert.Equal(110m, ma[20]);    // (19 * 100 + 300) / 20
        Assert.Equal(110m, ma[21]);    // the spike is still inside the window
    }

    [Fact]
    public void Ma_with_fewer_candles_than_the_period_is_all_empty()
    {
        var ma = MovingAverage.Simple(Series(Enumerable.Range(1, 10).Select(x => (decimal)x)), 20);
        Assert.Equal(10, ma.Count);
        Assert.All(ma, Assert.Null);
        Assert.Empty(MovingAverage.Simple(new List<Candle>(), 50));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-20)]
    public void Ma_refuses_a_non_positive_period(int period)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MovingAverage.Simple(OneToSixty(), period));
    }

    // ---- 24h high / low and change ------------------------------------------------------------

    [Fact]
    public void High_low_come_from_the_ticker()
    {
        var t = new Ticker24h(LastPrice: 77490.96m, OpenPrice: 76000m, HighPrice: 78123.45m, LowPrice: 75012.5m, Volume: 12345.678m);
        Assert.Equal(new HighLow(78123.45m, 75012.5m), DayStats.HighLowFromTicker(t));
    }

    [Theory]
    [InlineData(77490.96, 76000, 1490.96, 1.9618)]
    [InlineData(75000, 76000, -1000, -1.3158)]
    [InlineData(76000, 76000, 0, 0)]
    public void Change_24h_comes_from_the_ticker(double last, double open, double abs, double pct)
    {
        var t = new Ticker24h((decimal)last, (decimal)open, 80000m, 70000m, 1m);
        var c = DayStats.ChangeFromTicker(t);
        Assert.Equal((decimal)abs, c.Absolute);
        Assert.Equal((decimal)pct, Math.Round(c.Percent, 4));
    }

    [Fact]
    public void High_low_from_candles_cover_the_last_24h_only_including_the_forming_candle()
    {
        // 300 five-minute candles (25 h); now is 3 minutes into the last (forming) one.
        var candles = new List<Candle>();
        for (var i = 0; i < 300; i++)
        {
            var open = T0 + i * Five;
            var high = 80000m;
            var low = 70000m;
            if (i == 2) high = 99999m;       // 25 h ago: outside the window
            if (i == 3) low = 1m;            // outside the window
            if (i == 150) high = 90000m;     // inside
            if (i == 299) low = 50000m;      // the forming candle
            candles.Add(new Candle(open, open + Five - TimeSpan.FromMilliseconds(1), 75000m, high, low, 75000m, 1m));
        }
        var now = T0 + 299 * Five + TimeSpan.FromMinutes(3);

        Assert.Equal(new HighLow(90000m, 50000m), DayStats.HighLowFromCandles(candles, now));
    }

    // ---- "closes in mm:ss" --------------------------------------------------------------------

    private static readonly DateTimeOffset Open1200 = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Close1205 = Open1200 + Five - TimeSpan.FromMilliseconds(1); // 12:04:59.999

    [Theory]
    [InlineData(0, 0, 0, "closes in 05:00")]       // 12:00:00.000
    [InlineData(1, 52, 0, "closes in 03:08")]      // 12:01:52.000
    [InlineData(4, 0, 0, "closes in 01:00")]       // 12:04:00.000
    [InlineData(4, 59, 0, "closes in 00:01")]      // 12:04:59.000
    [InlineData(4, 59, 999, "closes in 00:00")]    // 12:04:59.999
    [InlineData(6, 10, 0, "closes in 00:00")]      // after close: never negative
    public void Countdown_text_rounds_up_to_whole_seconds(int min, int sec, int ms, string expected)
    {
        var now = Open1200 + new TimeSpan(0, 0, min, sec, ms);
        Assert.Equal(expected, CloseCountdown.Text(Close1205, now));
    }
}
