using BitcoinWallpaper.Chart.Core.Candles;

namespace BitcoinWallpaper.Market.Data;

/// <summary>Drops impossible exchange data before it reaches the chart (D-27); nothing is corrected or invented.
/// A reason names the rule, never the values, so the same rule always gives the same text.</summary>
public static class TickFilter
{
    /// <summary>The largest move of a close from the last accepted close of the same feed (15 %, inclusive).</summary>
    public const decimal MaxCloseJump = 0.15m;

    /// <summary>Null when the candle is possible, else the rule it breaks. Only the close is compared with
    /// <paramref name="referenceClose"/> (the last accepted close; null: structural rules only): real wicks pass.</summary>
    public static string? CheckCandle(Candle candle, decimal? referenceClose)
    {
        ArgumentNullException.ThrowIfNull(candle);
        if (candle.Open <= 0m || candle.High <= 0m || candle.Low <= 0m || candle.Close <= 0m)
        {
            return "candle: a price is zero or negative";
        }

        if (candle.High < candle.Low)
        {
            return "candle: high below low";
        }

        if (candle.Open < candle.Low || candle.Open > candle.High || candle.Close < candle.Low || candle.Close > candle.High)
        {
            return "candle: open or close outside the high-low range";
        }

        if (candle.Volume < 0m)
        {
            return "candle: negative volume";
        }

        if (referenceClose is { } reference && reference > 0m && Math.Abs(candle.Close - reference) > reference * MaxCloseJump)
        {
            return "candle: close more than 15 % from the last accepted close";
        }

        return null;
    }

    /// <summary>Null when the ticker is possible, else the rule it breaks. The open is not range-checked (Kraken's
    /// derived open can be older than the rolling 24 h range) and a ticker is never compared with an earlier one.</summary>
    public static string? CheckTicker(Ticker24h ticker)
    {
        ArgumentNullException.ThrowIfNull(ticker);
        if (ticker.Last <= 0m || ticker.Open <= 0m || ticker.High <= 0m || ticker.Low <= 0m)
        {
            return "ticker: a price is zero or negative";
        }

        if (ticker.Low > ticker.High)
        {
            return "ticker: low above high";
        }

        return ticker.Last < ticker.Low || ticker.Last > ticker.High ? "ticker: last outside the high-low range" : null;
    }

    /// <summary>The possible candles of a history, in order: each checked against the last accepted one before it
    /// (the first by the structural rules only); <paramref name="onDropped"/> gets the reason of each dropped one.</summary>
    public static IReadOnlyList<Candle> FilterHistory(IReadOnlyList<Candle> candles, Action<string>? onDropped = null)
    {
        ArgumentNullException.ThrowIfNull(candles);
        var kept = new List<Candle>(candles.Count);
        decimal? reference = null;
        foreach (var candle in candles)
        {
            if (CheckCandle(candle, reference) is { } reason)
            {
                onDropped?.Invoke(reason);
                continue;
            }

            kept.Add(candle);
            reference = candle.Close;
        }

        return kept;
    }
}
