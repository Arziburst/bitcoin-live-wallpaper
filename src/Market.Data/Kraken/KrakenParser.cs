using System.Text.Json;
using BitcoinWallpaper.Chart.Core.Candles;

namespace BitcoinWallpaper.Market.Data.Kraken;

public static class KrakenParser
{
    private static readonly TimeSpan RestInterval = TimeSpan.FromMinutes(5);

    /// <summary>REST /0/public/OHLC (row: time, open, high, low, close, vwap, volume, count).
    /// IsClosed = open time + 5 min is not after <paramref name="now"/>.</summary>
    public static IReadOnlyList<Candle> ParseOhlc(string json, DateTimeOffset now) => ParseOhlc(json, now, RestInterval);

    /// <summary>REST /0/public/OHLC of the given interval: IsClosed = open time + interval is not after
    /// <paramref name="now"/>.</summary>
    public static IReadOnlyList<Candle> ParseOhlc(string json, DateTimeOffset now, TimeSpan interval)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var errors) && errors.GetArrayLength() > 0)
        {
            throw new FormatException("Kraken error: " + errors.GetRawText());
        }

        var candles = new List<Candle>();
        foreach (var pair in root.GetProperty("result").EnumerateObject())
        {
            // "result" holds the pair's rows plus "last" (the cursor for the next request).
            if (pair.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var row in pair.Value.EnumerateArray())
            {
                var openTime = DateTimeOffset.FromUnixTimeSeconds(JsonValues.Int64(row[0]));
                candles.Add(new Candle(
                    openTime,
                    JsonValues.Decimal(row[1]),
                    JsonValues.Decimal(row[2]),
                    JsonValues.Decimal(row[3]),
                    JsonValues.Decimal(row[4]),
                    JsonValues.Decimal(row[6]),
                    openTime + interval <= now));
            }
        }

        return candles;
    }

    /// <summary>REST /0/public/Ticker: last trade (c), high, low and volume of the last 24 h (h, l, v at
    /// index 1). Kraken's "o" is today's open, so the open 24 h ago is passed in.</summary>
    public static Ticker24h ParseTicker24h(string json, decimal open24h)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var errors) && errors.GetArrayLength() > 0)
        {
            throw new FormatException("Kraken error: " + errors.GetRawText());
        }

        foreach (var pair in root.GetProperty("result").EnumerateObject())
        {
            var t = pair.Value;
            return JsonValues.Ticker(
                JsonValues.Decimal(t.GetProperty("c")[0]),
                open24h,
                JsonValues.Decimal(t.GetProperty("h")[1]),
                JsonValues.Decimal(t.GetProperty("l")[1]),
                JsonValues.Decimal(t.GetProperty("v")[1]));
        }

        throw new FormatException("Kraken ticker without a pair");
    }

    /// <summary>WS v2 message. Channel ohlc (snapshot or update) -> its candles, OpenTime = interval_begin,
    /// IsClosed = interval_begin + interval is not after <paramref name="now"/>; any other message -> empty.</summary>
    public static IReadOnlyList<Candle> ParseWsOhlc(string json, DateTimeOffset now) => ParseWsOhlc(json, now, null);

    /// <summary>As <see cref="ParseWsOhlc(string, DateTimeOffset)"/>, only the items whose interval is
    /// <paramref name="intervalMinutes"/>; items of any other interval are left out.</summary>
    public static IReadOnlyList<Candle> ParseWsOhlc(string json, DateTimeOffset now, int? intervalMinutes)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("channel", out var channel)
            || channel.GetString() != "ohlc"
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var candles = new List<Candle>(data.GetArrayLength());
        foreach (var item in data.EnumerateArray())
        {
            var minutes = JsonValues.Int64(item.GetProperty("interval"));
            if (intervalMinutes is { } only && minutes != only)
            {
                continue;
            }

            var openTime = JsonValues.Rfc3339(item.GetProperty("interval_begin").GetString()!);
            var interval = TimeSpan.FromMinutes(minutes);
            candles.Add(new Candle(
                openTime,
                JsonValues.Decimal(item, "open"),
                JsonValues.Decimal(item, "high"),
                JsonValues.Decimal(item, "low"),
                JsonValues.Decimal(item, "close"),
                JsonValues.Decimal(item, "volume"),
                openTime + interval <= now));
        }

        return candles;
    }
}
