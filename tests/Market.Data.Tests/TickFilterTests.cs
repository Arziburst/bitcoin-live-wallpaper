using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Market.Data.Binance;
using BitcoinWallpaper.Market.Data.Kraken;
using BitcoinWallpaper.Market.Data.Tests.Fakes;

namespace BitcoinWallpaper.Market.Data.Tests;

// Contract #32 (D-27), criteria 1 and 2: the pure tick filter. API SKETCH (#237), src/Market.Data/TickFilter.cs,
// public static class TickFilter (namespace BitcoinWallpaper.Market.Data):
//   public static string? CheckCandle(Candle candle, decimal? referenceClose);
//       // null = passes, else a non-empty reason naming the RULE (never the values: same rule, same text; other rule,
//       // other text). Rules: a price <= 0; high < low; open or close outside [low, high]; volume < 0 (0 is fine); with a
//       // reference: |close - ref| / ref > 0.15 STRICTLY (exactly 15 % passes). Only the close is jump-checked.
//   public static string? CheckTicker(Ticker24h ticker);
//       // null = passes. Dropped only for a price <= 0, low > high, last outside [low, high]; open is not range-checked.
//   public static IReadOnlyList<Candle> FilterHistory(IReadOnlyList<Candle> candles, Action<string>? onDropped = null);
//       // each candle against the last ACCEPTED one before it (the first: structural rules only); a dropped candle is never
//       // the reference; returns the accepted ones in order; onDropped is called once per dropped candle with its reason.
public class TickFilterTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 31, 2, 0, 0, TimeSpan.Zero);

    private static Candle C(decimal o, decimal h, decimal l, decimal c, decimal v = 1m, int n = 0) =>
        new(T.AddMinutes(5 * n), o, h, l, c, v, true);

    private static Candle Ok(decimal close = 100m, int n = 0) => C(close, close + 1, close - 1, close, 1m, n);

    private static Ticker24h K(decimal last = 100, decimal open = 95, decimal high = 110, decimal low = 90) =>
        new(last, open, high, low, last - open, 1m, 5m);

    public static TheoryData<string, Candle> StructurallyBad => new()
    {
        { "zero open", C(0m, 101m, 99m, 100m) },
        { "zero low", C(100m, 101m, 0m, 100m) },
        { "zero close", C(100m, 101m, 99m, 0m) },
        { "negative close", C(100m, 101m, -5m, -1m) },
        { "high below low", C(100m, 98m, 99m, 100m) },
        { "open above high", C(102m, 101m, 99m, 100m) },
        { "open below low", C(98m, 101m, 99m, 100m) },
        { "close above high", C(100m, 101m, 99m, 102m) },
        { "negative volume", C(100m, 101m, 99m, 100m, -0.001m) },
    };

    [Theory]
    [MemberData(nameof(StructurallyBad))]
    public void A_structurally_impossible_candle_is_dropped_with_a_reason(string name, Candle bad)
    {
        Assert.False(string.IsNullOrWhiteSpace(TickFilter.CheckCandle(bad, null)), name + " without a reference");
        Assert.False(string.IsNullOrWhiteSpace(TickFilter.CheckCandle(bad, 100m)), name + " with a reference");
    }

    [Fact]
    public void Edge_values_that_are_possible_pass()
    {
        Assert.Null(TickFilter.CheckCandle(C(100m, 100m, 100m, 100m, 0m), null)); // flat, no volume
        Assert.Null(TickFilter.CheckCandle(C(99m, 101m, 99m, 101m), 100m)); // open on low, close on high
        Assert.Null(TickFilter.CheckCandle(C(100m, 300m, 1m, 100m), 100m)); // a huge wick is real: only the close is jump-checked
    }

    [Theory]
    [InlineData(115.0, true)] // exactly +15 %
    [InlineData(85.0, true)] // exactly -15 %
    [InlineData(115.01, false)]
    [InlineData(84.99, false)]
    [InlineData(100.0, true)]
    [InlineData(1000.0, false)]
    public void The_close_may_be_at_most_15_percent_from_the_reference(double close, bool passes)
    {
        var c = (decimal)close;
        var candle = C(c, c + 1m, c - 1m, c);
        Assert.Equal(passes, TickFilter.CheckCandle(candle, 100m) is null);
        Assert.Null(TickFilter.CheckCandle(candle, null)); // no reference: structural rules only
    }

    [Fact]
    public void Reasons_name_the_rule_not_the_values()
    {
        var jumpA = TickFilter.CheckCandle(Ok(200m), 100m);
        var jumpB = TickFilter.CheckCandle(Ok(40m), 100m);
        var zeroA = TickFilter.CheckCandle(C(0m, 101m, 99m, 100m), null);
        var zeroB = TickFilter.CheckCandle(C(100m, 101m, 99m, 0m), null);
        var order = TickFilter.CheckCandle(C(100m, 98m, 99m, 100m), null);
        Assert.NotNull(jumpA);
        Assert.Equal(jumpA, jumpB);
        Assert.Equal(zeroA, zeroB);
        Assert.NotEqual(jumpA, zeroA);
        Assert.NotEqual(order, zeroA);
        Assert.NotEqual(order, jumpA);
    }

    [Fact]
    public void A_dropped_candle_never_becomes_the_reference_in_a_history()
    {
        // 100 .. spike 200 (dropped) .. spike 200 again (dropped: the first one was not accepted) .. 110 (kept).
        var history = new[] { Ok(100m, 0), Ok(200m, 1), Ok(200m, 2), Ok(110m, 3), Ok(120m, 4), Ok(300m, 5), Ok(125m, 6) };
        var drops = new List<string>();
        var kept = TickFilter.FilterHistory(history, drops.Add);
        Assert.Equal(new[] { 0, 3, 4, 6 }, kept.Select(c => (int)((c.OpenTime - T).TotalMinutes / 5)));
        Assert.Equal(3, drops.Count);
        Assert.All(drops, d => Assert.False(string.IsNullOrWhiteSpace(d)));
    }

    [Fact]
    public void The_first_candle_is_checked_by_the_structural_rules_only()
    {
        // The first is structurally fine at any price (accepted); the next is then compared with it.
        var kept = TickFilter.FilterHistory([Ok(1000m, 0), Ok(100m, 1)]);
        Assert.Equal([T], kept.Select(c => c.OpenTime));

        // A structurally bad first candle is dropped and the second has no reference either.
        var after = TickFilter.FilterHistory([C(0m, 1m, 0m, 0m, 1m, 0), Ok(5000m, 1)]);
        Assert.Equal([T.AddMinutes(5)], after.Select(c => c.OpenTime));
    }

    public static TheoryData<string, Ticker24h> BadTickers => new()
    {
        { "zero last", K(last: 0m) },
        { "negative last", K(last: -1m, low: -2m) },
        { "zero open", K(open: 0m) },
        { "zero high", K(high: 0m, low: 0m, last: 0m) },
        { "zero low", K(low: 0m) },
        { "low above high", K(high: 90m, low: 110m) },
        { "last above high", K(last: 111m) },
        { "last below low", K(last: 89m) },
    };

    [Theory]
    [MemberData(nameof(BadTickers))]
    public void An_impossible_ticker_is_dropped_with_a_reason(string name, Ticker24h bad) =>
        Assert.False(string.IsNullOrWhiteSpace(TickFilter.CheckTicker(bad)), name);

    [Fact]
    public void A_ticker_open_outside_the_range_is_fine_and_a_ticker_is_never_jump_checked()
    {
        Assert.Null(TickFilter.CheckTicker(K(open: 500m))); // Kraken's derived open can be older than the rolling range
        Assert.Null(TickFilter.CheckTicker(K(open: 5m)));
        Assert.Null(TickFilter.CheckTicker(K(last: 100000m, open: 95m, high: 100001m, low: 90m)));
        Assert.Null(TickFilter.CheckTicker(K(last: 90m, low: 90m, high: 110m))); // on the bounds
        Assert.Null(TickFilter.CheckTicker(K(last: 110m)));
    }

    [Fact]
    public void Recorded_exchange_data_passes_unchanged()
    {
        var now = Fixtures.RecordedAt;
        var binance = BinanceParser.ParseKlines(Fixtures.BinanceKlines, now);
        var drops = new List<string>();
        Assert.Equal(binance, TickFilter.FilterHistory(binance, drops.Add));
        var kraken = KrakenParser.ParseOhlc(Fixtures.KrakenOhlc, now);
        Assert.Equal(kraken, TickFilter.FilterHistory(kraken, drops.Add));
        Assert.Empty(drops);

        var kline = ((CandleUpdate)BinanceParser.ParseStreamMessage(Fixtures.BinanceKlineForming)!).Candle;
        Assert.Null(TickFilter.CheckCandle(kline, binance[^1].Close));
        foreach (var message in new[] { Fixtures.KrakenWsSnapshot, Fixtures.KrakenWsUpdate, Fixtures.KrakenWsUpdateNext })
        {
            foreach (var candle in KrakenParser.ParseWsOhlc(message, now))
            {
                Assert.Null(TickFilter.CheckCandle(candle, kraken[^1].Close));
            }
        }

        Assert.Null(TickFilter.CheckTicker(BinanceParser.ParseTicker24h(Fixtures.BinanceTicker)));
        Assert.Null(TickFilter.CheckTicker(((TickerUpdate)BinanceParser.ParseStreamMessage(Fixtures.BinanceMiniTicker)!).Ticker));
        Assert.Null(TickFilter.CheckTicker(KrakenParser.ParseTicker24h(Fixtures.KrakenTicker, kraken[0].Open)));
    }
}
