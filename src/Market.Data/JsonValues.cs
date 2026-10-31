using System.Globalization;
using System.Text.Json;
using BitcoinWallpaper.Chart.Core.Candles;

namespace BitcoinWallpaper.Market.Data;

/// <summary>Culture-invariant, decimal-exact readers for exchange JSON (numbers come as strings or numbers).</summary>
internal static class JsonValues
{
    public static decimal Decimal(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => decimal.Parse(element.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture),
        JsonValueKind.Number => element.GetDecimal(),
        _ => throw new FormatException($"expected a number, got {element.ValueKind}"),
    };

    public static decimal Decimal(JsonElement obj, string name) => Decimal(obj.GetProperty(name));

    public static long Int64(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => long.Parse(element.GetString()!, NumberStyles.Integer, CultureInfo.InvariantCulture),
        JsonValueKind.Number => element.GetInt64(),
        _ => throw new FormatException($"expected an integer, got {element.ValueKind}"),
    };

    /// <summary>RFC 3339 UTC time with up to nanosecond precision (Kraken WS v2), truncated to 100 ns.</summary>
    public static DateTimeOffset Rfc3339(string text)
    {
        var dot = text.IndexOf('.');
        if (dot >= 0)
        {
            var end = dot + 1;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
            {
                end++;
            }

            var digits = end - dot - 1;
            if (digits > 7)
            {
                text = string.Concat(text.AsSpan(0, dot + 8), text.AsSpan(end));
            }
        }

        return DateTimeOffset.Parse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    /// <summary>A ticker with the change fields derived: ChangeAbs = last - open, ChangePercent = ChangeAbs / open * 100.</summary>
    public static Ticker24h Ticker(decimal last, decimal open, decimal high, decimal low, decimal volume)
    {
        var change = last - open;
        var percent = open == 0m ? 0m : change / open * 100m;
        return new Ticker24h(last, open, high, low, change, percent, volume);
    }
}
