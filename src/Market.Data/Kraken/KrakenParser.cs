using System.Text.Json;
using BitcoinWallpaper.Chart.Core.Candles;

namespace BitcoinWallpaper.Market.Data.Kraken;

public static class KrakenParser
{
    private static readonly TimeSpan RestInterval = TimeSpan.FromMinutes(5);

    /// <summary>REST /0/public/OHLC (row: time, open, high, low, close, vwap, volume, count).
    /// IsClosed = open time + 5 min is not after <paramref name="now"/>.</summary>
    public static IReadOnlyList<Candle> ParseOhlc(string json, DateTimeOffset now)
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
                    openTime + RestInterval <= now));
            }
        }

        return candles;
    }

    /// <summary>WS v2 message. Channel ohlc (snapshot or update) -> its candles, OpenTime = interval_begin,
    /// IsClosed = interval_begin + interval is not after <paramref name="now"/>; any other message -> empty.</summary>
    public static IReadOnlyList<Candle> ParseWsOhlc(string json, DateTimeOffset now)
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
            var openTime = JsonValues.Rfc3339(item.GetProperty("interval_begin").GetString()!);
            var interval = TimeSpan.FromMinutes(JsonValues.Int64(item.GetProperty("interval")));
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
