using System.Text.Json;
using BitcoinWallpaper.Chart.Core.Candles;

namespace BitcoinWallpaper.Market.Data.Binance;

public static class BinanceParser
{
    /// <summary>REST /api/v3/klines. IsClosed = the kline's close time is before <paramref name="now"/>.</summary>
    public static IReadOnlyList<Candle> ParseKlines(string json, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        var rows = doc.RootElement;
        var candles = new List<Candle>(rows.GetArrayLength());

        // Row: open time, open, high, low, close, volume, close time, quote volume, trades, ...
        foreach (var row in rows.EnumerateArray())
        {
            var closeTime = DateTimeOffset.FromUnixTimeMilliseconds(JsonValues.Int64(row[6]));
            candles.Add(new Candle(
                DateTimeOffset.FromUnixTimeMilliseconds(JsonValues.Int64(row[0])),
                JsonValues.Decimal(row[1]),
                JsonValues.Decimal(row[2]),
                JsonValues.Decimal(row[3]),
                JsonValues.Decimal(row[4]),
                JsonValues.Decimal(row[5]),
                closeTime < now));
        }

        return candles;
    }

    /// <summary>REST /api/v3/ticker/24hr.</summary>
    public static Ticker24h ParseTicker24h(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var t = doc.RootElement;
        return new Ticker24h(
            JsonValues.Decimal(t, "lastPrice"),
            JsonValues.Decimal(t, "openPrice"),
            JsonValues.Decimal(t, "highPrice"),
            JsonValues.Decimal(t, "lowPrice"),
            JsonValues.Decimal(t, "priceChange"),
            JsonValues.Decimal(t, "priceChangePercent"),
            JsonValues.Decimal(t, "volume"));
    }

    /// <summary>A raw stream payload: kline -> CandleUpdate (IsClosed = k.x), 24hrMiniTicker -> TickerUpdate;
    /// anything else (subscribe replies, other events) -> null.</summary>
    public static MarketEvent? ParseStreamMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("e", out var type))
        {
            return null;
        }

        switch (type.GetString())
        {
            case "kline":
                var k = root.GetProperty("k");
                return new CandleUpdate(new Candle(
                    DateTimeOffset.FromUnixTimeMilliseconds(JsonValues.Int64(k.GetProperty("t"))),
                    JsonValues.Decimal(k, "o"),
                    JsonValues.Decimal(k, "h"),
                    JsonValues.Decimal(k, "l"),
                    JsonValues.Decimal(k, "c"),
                    JsonValues.Decimal(k, "v"),
                    k.GetProperty("x").GetBoolean()));

            case "24hrMiniTicker":
                return new TickerUpdate(JsonValues.Ticker(
                    JsonValues.Decimal(root, "c"),
                    JsonValues.Decimal(root, "o"),
                    JsonValues.Decimal(root, "h"),
                    JsonValues.Decimal(root, "l"),
                    JsonValues.Decimal(root, "v")));

            default:
                return null;
        }
    }
}
