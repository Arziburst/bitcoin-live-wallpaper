using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Market.Data.Binance;
using BitcoinWallpaper.Market.Data.Tests.Fakes;

namespace BitcoinWallpaper.Market.Data.Tests;

// Contract #3, criterion: Binance WebSocket kline_5m and miniTicker messages update the forming candle and
// the ticker; a kline with x=true closes the candle and starts the next.
// Messages: tests/fixtures/binance/ws_* (raw stream payloads continuing the recorded REST data).
public class BinanceStreamTests
{
    private static readonly DateTimeOffset Forming = Fixtures.Utc("2026-10-31T02:05:00Z");
    private static readonly DateTimeOffset Next = Fixtures.Utc("2026-10-31T02:10:00Z");

    private static readonly Candle FormingUpdate =
        new(Forming, 77385.25m, 77412.50m, 77385.24m, 77410.00m, 2.10413m, false);

    private static readonly Candle FormingClosed =
        new(Forming, 77385.25m, 77420.00m, 77380.10m, 77398.76m, 6.20831m, true);

    private static readonly Candle NextForming =
        new(Next, 77398.77m, 77401.00m, 77398.76m, 77400.12m, 0.31544m, false);

    private static Candle ParseCandle(string message) =>
        Assert.IsType<CandleUpdate>(BinanceParser.ParseStreamMessage(message)).Candle;

    [Fact]
    public void Kline_message_parses_to_the_forming_candle()
    {
        Assert.Equal(FormingUpdate, ParseCandle(Fixtures.BinanceKlineForming));
    }

    [Fact]
    public void Kline_message_with_x_true_parses_to_a_closed_candle()
    {
        Assert.Equal(FormingClosed, ParseCandle(Fixtures.BinanceKlineClosed));
        Assert.Equal(NextForming, ParseCandle(Fixtures.BinanceKlineNext));
    }

    [Fact]
    public void MiniTicker_message_parses_to_the_ticker_with_derived_change()
    {
        var ticker = Assert.IsType<TickerUpdate>(BinanceParser.ParseStreamMessage(Fixtures.BinanceMiniTicker)).Ticker;

        Assert.Equal(77410.00m, ticker.Last);
        Assert.Equal(76559.39m, ticker.Open);
        Assert.Equal(78804.01m, ticker.High);
        Assert.Equal(76325.98m, ticker.Low);
        Assert.Equal(18988.9121m, ticker.Volume);
        Assert.Equal(850.61m, ticker.ChangeAbs);
        var expectedPercent = 850.61m / 76559.39m * 100m; // 1.11104...
        Assert.True(Math.Abs(ticker.ChangePercent - expectedPercent) < 0.0001m,
            $"change % {ticker.ChangePercent}, expected {expectedPercent}");
    }

    [Fact]
    public void Subscribe_reply_is_not_market_data()
    {
        Assert.Null(BinanceParser.ParseStreamMessage(Fixtures.BinanceSubscribeResult));
    }

    [Fact]
    public void Stream_updates_the_forming_candle_in_place_then_closes_it_and_starts_the_next()
    {
        var history = BinanceParser.ParseKlines(Fixtures.BinanceKlines, Fixtures.RecordedAt);
        var buffer = new CandleBuffer();
        buffer.Reset(MarketSource.Binance, history);

        buffer.Upsert(MarketSource.Binance, ParseCandle(Fixtures.BinanceKlineForming));
        var afterUpdate = buffer.Snapshot();
        Assert.Equal(5, afterUpdate.Count);
        Assert.Equal(FormingUpdate, afterUpdate[^1]);
        Assert.Equal(history.Take(4), afterUpdate.Take(4));

        buffer.Upsert(MarketSource.Binance, ParseCandle(Fixtures.BinanceKlineClosed));
        Assert.Equal(FormingClosed, buffer.Snapshot().Single(c => c.OpenTime == Forming));

        buffer.Upsert(MarketSource.Binance, ParseCandle(Fixtures.BinanceKlineNext));
        var afterNext = buffer.Snapshot();
        Assert.Equal(6, afterNext.Count);
        Assert.Equal(FormingClosed, afterNext[4]);
        Assert.Equal(NextForming, afterNext[5]);
    }

    [Fact]
    public void Client_applies_kline_and_miniTicker_messages_to_its_candles_and_ticker()
    {
        using var sim = new Sim();
        sim.Run();

        Assert.Equal(MarketSource.Binance, sim.Client.Source);
        Assert.Equal(5, sim.Client.Candles.Count);
        Assert.Equal(77404.54m, sim.Client.Ticker?.Last);

        sim.Push(Exchange.Binance, Fixtures.BinanceKlineForming);
        sim.Push(Exchange.Binance, Fixtures.BinanceMiniTicker);

        var candles = sim.Client.Candles.Snapshot();
        Assert.Equal(5, candles.Count);
        Assert.Equal(FormingUpdate, candles[^1]);
        Assert.Equal(77410.00m, sim.Client.Ticker?.Last);
        Assert.Equal(850.61m, sim.Client.Ticker?.ChangeAbs);

        // The live stream keeps pushing (miniTicker every second) until the 02:05 candle closes.
        sim.AdvanceFeeding(Exchange.Binance, Fixtures.Utc("2026-10-31T02:10:00.011Z"), TimeSpan.FromSeconds(2),
            Fixtures.BinanceMiniTicker);
        sim.Push(Exchange.Binance, Fixtures.BinanceKlineClosed);
        sim.AdvanceTo(Fixtures.Utc("2026-10-31T02:10:02.009Z"));
        sim.Push(Exchange.Binance, Fixtures.BinanceKlineNext);

        candles = sim.Client.Candles.Snapshot();
        Assert.Equal(6, candles.Count);
        Assert.Equal(FormingClosed, candles[4]);
        Assert.Equal(NextForming, candles[5]);
        Assert.Equal(MarketSource.Binance, sim.Client.Candles.Source);
    }
}
