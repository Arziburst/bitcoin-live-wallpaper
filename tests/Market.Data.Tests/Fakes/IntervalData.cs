using System.Globalization;
using System.Text;
using BitcoinWallpaper.Chart.Core.Candles;

namespace BitcoinWallpaper.Market.Data.Tests.Fakes;

/// <summary>Contract #18: deterministic synthetic market data for any candle interval, written in the exchanges'
/// formats (Binance REST klines and WS kline, Kraken REST OHLC and WS v2 ohlc), plus an independent oracle for
/// the expected candles. A candle's values depend only on its open time and interval, so every request and every
/// expectation agree without a recording.</summary>
internal static class IntervalData
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Binance interval codes of the IntervalPolicy ladder (and 5m).</summary>
    public static string BinanceCode(TimeSpan interval) => interval.TotalMinutes switch
    {
        5 => "5m",
        60 => "1h",
        120 => "2h",
        240 => "4h",
        360 => "6h",
        480 => "8h",
        720 => "12h",
        1440 => "1d",
        _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, "no Binance code in the test table"),
    };

    public static TimeSpan FromBinanceCode(string code) => code switch
    {
        "1m" => TimeSpan.FromMinutes(1),
        "3m" => TimeSpan.FromMinutes(3),
        "5m" => TimeSpan.FromMinutes(5),
        "15m" => TimeSpan.FromMinutes(15),
        "30m" => TimeSpan.FromMinutes(30),
        "1h" => TimeSpan.FromHours(1),
        "2h" => TimeSpan.FromHours(2),
        "4h" => TimeSpan.FromHours(4),
        "6h" => TimeSpan.FromHours(6),
        "8h" => TimeSpan.FromHours(8),
        "12h" => TimeSpan.FromHours(12),
        "1d" => TimeSpan.FromDays(1),
        _ => TimeSpan.Zero,
    };

    /// <summary>The OHLC intervals Kraken offers, in minutes (REST /0/public/OHLC and WS v2 ohlc). 360 is not one.</summary>
    public static readonly int[] KrakenNativeMinutes = [1, 5, 15, 30, 60, 240, 1440, 10080, 21600];

    /// <summary>ceil(30 days / interval).</summary>
    public static int ThirtyDayCount(TimeSpan interval) =>
        (int)Math.Ceiling(TimeSpan.FromDays(30).Ticks / (double)interval.Ticks);

    public static DateTimeOffset Floor(DateTimeOffset t, TimeSpan interval) =>
        new(t.UtcTicks - (t.UtcTicks % interval.Ticks), TimeSpan.Zero);

    public static bool Aligned(DateTimeOffset t, TimeSpan interval) =>
        t.Offset == TimeSpan.Zero && t.UtcTicks % interval.Ticks == 0;

    /// <summary>The synthetic values of the candle opening at <paramref name="open"/>: one decimal place (Kraken's
    /// precision), so the same numbers fit every format.</summary>
    public static (decimal Open, decimal High, decimal Low, decimal Close, decimal Volume) Values(DateTimeOffset open, TimeSpan interval)
    {
        var k = open.ToUnixTimeSeconds() / (long)interval.TotalSeconds;
        var m = (int)(interval.TotalMinutes % 997);
        var o = 70000m + (((k * 7919) + m) % 4000) / 10m;
        var c = o + ((((k * 104729) + m) % 4001) - 2000) / 10m;
        var h = Math.Max(o, c) + (k % 7) / 10m + 1m;
        var l = Math.Min(o, c) - (k % 5) / 10m - 1m;
        var v = 1m + (k % 11) * 0.5m + 0.1234m;
        return (o, h, l, c, v);
    }

    /// <summary>The newest <paramref name="count"/> candles of the interval up to and including the one that
    /// contains <paramref name="now"/>, oldest first, as (open time, values).</summary>
    public static IReadOnlyList<DateTimeOffset> OpenTimes(TimeSpan interval, DateTimeOffset now, int count)
    {
        var newest = Floor(now, interval);
        return Enumerable.Range(0, count).Select(i => newest - ((count - 1 - i) * interval)).ToList();
    }

    /// <summary>What a Binance parse of the served rows must give: IsClosed = close time (open + interval - 1 ms)
    /// is before now.</summary>
    public static Candle BinanceCandle(DateTimeOffset open, TimeSpan interval, DateTimeOffset now)
    {
        var v = Values(open, interval);
        return new Candle(open, v.Open, v.High, v.Low, v.Close, v.Volume, open + interval - TimeSpan.FromMilliseconds(1) < now);
    }

    public static string BinanceKlinesJson(TimeSpan interval, DateTimeOffset now, int count)
    {
        var sb = new StringBuilder("[");
        var first = true;
        foreach (var open in OpenTimes(interval, now, count))
        {
            var v = Values(open, interval);
            var openMs = open.ToUnixTimeMilliseconds();
            var closeMs = openMs + (long)interval.TotalMilliseconds - 1;
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            sb.Append(Inv, $"[{openMs},\"{v.Open:F8}\",\"{v.High:F8}\",\"{v.Low:F8}\",\"{v.Close:F8}\",\"{v.Volume:F8}\",{closeMs},\"0\",100,\"0\",\"0\",\"0\"]");
        }

        return sb.Append(']').ToString();
    }

    public static string BinanceKlineMessage(string code, DateTimeOffset open, TimeSpan interval, decimal o, decimal h, decimal l, decimal c, decimal v, bool closed)
    {
        var openMs = open.ToUnixTimeMilliseconds();
        var closeMs = openMs + (long)interval.TotalMilliseconds - 1;
        return string.Create(Inv,
            $"{{\"e\":\"kline\",\"E\":{openMs + 1000},\"s\":\"BTCUSDT\",\"k\":{{\"t\":{openMs},\"T\":{closeMs},\"s\":\"BTCUSDT\",\"i\":\"{code}\",\"f\":1,\"L\":2,\"o\":\"{o:F8}\",\"c\":\"{c:F8}\",\"h\":\"{h:F8}\",\"l\":\"{l:F8}\",\"v\":\"{v:F8}\",\"n\":10,\"x\":{(closed ? "true" : "false")},\"q\":\"0\",\"V\":\"0\",\"Q\":\"0\",\"B\":\"0\"}}}}");
    }

    /// <summary>Kraken REST OHLC: the newest <paramref name="count"/> rows of the interval (Kraken serves at most 720),
    /// those with time >= since when given.</summary>
    public static string KrakenOhlcJson(TimeSpan interval, DateTimeOffset now, int count, long? since)
    {
        var sb = new StringBuilder("{\"error\":[],\"result\":{\"XXBTZUSD\":[");
        var first = true;
        long last = 0;
        foreach (var open in OpenTimes(interval, now, count))
        {
            var t = open.ToUnixTimeSeconds();
            if (since is { } s && t < s)
            {
                continue;
            }

            var v = Values(open, interval);
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            last = t;
            sb.Append(Inv, $"[{t},\"{v.Open:F1}\",\"{v.High:F1}\",\"{v.Low:F1}\",\"{v.Close:F1}\",\"{v.Close:F1}\",\"{v.Volume:F8}\",50]");
        }

        return sb.Append(Inv, $"],\"last\":{last}}}}}").ToString();
    }

    public static string KrakenWsOhlc(int intervalMinutes, DateTimeOffset begin, decimal o, decimal h, decimal l, decimal c, decimal v, DateTimeOffset now) =>
        string.Create(Inv,
            $"{{\"channel\":\"ohlc\",\"type\":\"update\",\"timestamp\":\"{now:yyyy-MM-ddTHH:mm:ss.fffffff}Z\",\"data\":[{{\"symbol\":\"BTC/USD\",\"open\":{o},\"high\":{h},\"low\":{l},\"close\":{c},\"trades\":7,\"volume\":{v},\"vwap\":{c},\"interval_begin\":\"{begin:yyyy-MM-ddTHH:mm:ss}.000000000Z\",\"interval\":{intervalMinutes},\"timestamp\":\"{begin.AddMinutes(intervalMinutes):yyyy-MM-ddTHH:mm:ss}.000000Z\"}}]}}");

    public const string KrakenHeartbeat = "{\"channel\":\"heartbeat\"}";

    /// <summary>The oracle: source candles (ascending) grouped into UTC-aligned buckets of <paramref name="interval"/>;
    /// a leading bucket whose first source candle is not at the bucket start is incomplete and dropped; O = first
    /// open, H = max, L = min, C = last close, V = sum; IsClosed = bucket start + interval &lt;= now.</summary>
    public static IReadOnlyList<Candle> Aggregate(IReadOnlyList<Candle> source, TimeSpan interval, DateTimeOffset now)
    {
        var result = new List<Candle>();
        foreach (var group in source.GroupBy(c => Floor(c.OpenTime, interval)))
        {
            var items = group.OrderBy(c => c.OpenTime).ToList();
            if (result.Count == 0 && items[0].OpenTime != group.Key)
            {
                continue;
            }

            result.Add(new Candle(
                group.Key,
                items[0].Open,
                items.Max(c => c.High),
                items.Min(c => c.Low),
                items[^1].Close,
                items.Sum(c => c.Volume),
                group.Key + interval <= now));
        }

        return result;
    }

    /// <summary>Synthetic source candles (values as Kraken would serve them) at the given open times.</summary>
    public static Candle Source(DateTimeOffset open, TimeSpan interval)
    {
        var v = Values(open, interval);
        return new Candle(open, v.Open, v.High, v.Low, v.Close, v.Volume, IsClosed: true);
    }

    public static (DateTimeOffset, decimal, decimal, decimal, decimal, decimal) Ohlcv(Candle c) =>
        (c.OpenTime, c.Open, c.High, c.Low, c.Close, c.Volume);
}
