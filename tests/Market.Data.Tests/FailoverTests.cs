using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Market.Data.Tests.Fakes;

namespace BitcoinWallpaper.Market.Data.Tests;

// Contract #3, criterion: ... after 3 failed cycles (or HTTP 451 at start) it switches to Kraken; and
// (CandleBuffer criterion, client side) the series never mixes sources: a switch clears and reloads.
// Fake clock and fake transport (Sim); a connect cycle = REST history + WebSocket, cycles are told apart by
// their instant on the fake clock (a backoff of at least 1 s separates them).
public class FailoverTests
{
    private static void AssertHoldsExactlyTheKrakenHistory(MarketDataClient client)
    {
        Assert.Equal(MarketSource.Kraken, client.Source);
        Assert.Equal(MarketSource.Kraken, client.Candles.Source);
        Assert.Equal(
            Fixtures.KrakenRestRows(),
            client.Candles.Snapshot().Select(c => (c.OpenTime, c.Open, c.High, c.Low, c.Close, c.Volume)));
    }

    private static void AdvanceUntilKrakenSocket(Sim sim, TimeSpan limit)
    {
        var end = sim.Now + limit;
        while (sim.Transport.OpenSocket(Exchange.Kraken) is null && sim.Now < end)
        {
            sim.Advance(TimeSpan.FromMilliseconds(500));
        }

        Assert.NotNull(sim.Transport.OpenSocket(Exchange.Kraken));
    }

    [Fact]
    public void Starts_on_Binance_with_history_ticker_and_a_live_socket()
    {
        using var sim = new Sim();
        sim.Run();

        Assert.Equal(MarketSource.Binance, sim.Client.Source);
        Assert.Equal(MarketSource.Binance, sim.Client.Candles.Source);
        Assert.Equal(5, sim.Client.Candles.Count);
        Assert.Equal(Fixtures.Utc("2026-10-31T01:45:00Z"), sim.Client.Candles.Snapshot()[0].OpenTime);
        Assert.Equal(77404.54m, sim.Client.Ticker?.Last);
        Assert.NotNull(sim.Transport.OpenSocket(Exchange.Binance));
        Assert.Empty(sim.Transport.CallsTo(Exchange.Kraken));
        Assert.Equal(FeedState.Live, sim.Client.State);
    }

    [Fact]
    public void Http_451_at_start_switches_to_Kraken_at_once()
    {
        using var sim = new Sim();
        sim.Transport.Binance.RestStatus = 451;
        sim.Run();
        sim.Advance(TimeSpan.FromSeconds(10));

        var firstKraken = sim.Transport.CallsTo(Exchange.Kraken).Select(c => c.At).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
        Assert.True(firstKraken <= sim.Start + TimeSpan.FromSeconds(2), $"first Kraken request at {firstKraken:O}");
        Assert.Single(sim.Transport.CycleTimes(Exchange.Binance)); // no Binance retry after a 451
        AssertHoldsExactlyTheKrakenHistory(sim.Client);
    }

    [Fact]
    public void After_a_switch_Binance_data_never_enters_the_series()
    {
        using var sim = new Sim();
        sim.Transport.Binance.RestStatus = 451; // REST blocked, the Binance socket still connects
        sim.Run();
        sim.Advance(TimeSpan.FromSeconds(10));
        AssertHoldsExactlyTheKrakenHistory(sim.Client);

        foreach (var socket in sim.Transport.Sockets.Where(s => s.Exchange == Exchange.Binance && s.IsOpen).ToList())
        {
            socket.Push(Fixtures.BinanceKlineForming);
            socket.Push(Fixtures.BinanceKlineNext);
            sim.Drain();
        }

        AssertHoldsExactlyTheKrakenHistory(sim.Client);
    }

    [Fact]
    public void Three_failed_cycles_switch_to_Kraken_and_reload_the_history()
    {
        using var sim = new Sim();
        sim.Transport.Binance.RestDown = sim.Transport.Binance.SocketDown = true;
        sim.Run();
        sim.Advance(TimeSpan.FromMinutes(2));

        var kraken = sim.Transport.CallsTo(Exchange.Kraken).ToList();
        Assert.NotEmpty(kraken);
        var firstKraken = kraken.Min(c => c.At);
        var binanceCycles = sim.Transport.CycleTimes(Exchange.Binance).Where(t => t <= firstKraken).ToList();
        Assert.Equal(3, binanceCycles.Count);
        Assert.Equal(sim.Start, binanceCycles[0]);
        for (var i = 1; i < binanceCycles.Count; i++)
        {
            Assert.True(binanceCycles[i] - binanceCycles[i - 1] >= TimeSpan.FromSeconds(1),
                $"Binance cycles {i - 1} and {i} only {binanceCycles[i] - binanceCycles[i - 1]} apart");
        }

        AssertHoldsExactlyTheKrakenHistory(sim.Client);
    }

    [Fact]
    public void A_switch_after_live_Binance_data_clears_the_Binance_series()
    {
        using var sim = new Sim();
        sim.Run();
        sim.Push(Exchange.Binance, Fixtures.BinanceKlineForming);
        Assert.Equal(MarketSource.Binance, sim.Client.Candles.Source);

        sim.Transport.Binance.RestDown = sim.Transport.Binance.SocketDown = true;
        sim.Transport.OpenSocket(Exchange.Binance)!.ServerClose();
        sim.Drain();
        sim.Advance(TimeSpan.FromMinutes(2));

        AssertHoldsExactlyTheKrakenHistory(sim.Client);
    }

    [Fact]
    public void Two_failed_cycles_stay_on_Binance_and_the_count_restarts_after_a_success()
    {
        using var sim = new Sim();
        sim.Run();
        sim.Push(Exchange.Binance, Fixtures.BinanceKlineForming);

        for (var round = 0; round < 2; round++)
        {
            sim.AdvanceFeeding(Exchange.Binance, sim.Now + TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1),
                Fixtures.BinanceMiniTicker);
            sim.Transport.Binance.FailNextSockets = 2;
            var droppedAt = sim.Now;
            sim.Transport.OpenSocket(Exchange.Binance)!.ServerClose();
            sim.Drain();

            sim.AdvanceFeeding(Exchange.Binance, sim.Now + TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1),
                Fixtures.BinanceMiniTicker);

            var connects = sim.Transport.CallsTo(Exchange.Binance).Count(c => c.IsSocket && c.At >= droppedAt);
            Assert.Equal(3, connects); // two refused, the third one holds
            Assert.NotNull(sim.Transport.OpenSocket(Exchange.Binance));
        }

        Assert.Empty(sim.Transport.CallsTo(Exchange.Kraken));
        Assert.Equal(MarketSource.Binance, sim.Client.Source);
        Assert.Equal(MarketSource.Binance, sim.Client.Candles.Source);
        Assert.Equal(FeedState.Live, sim.Client.State);
    }

    [Fact]
    public void After_failover_the_Kraken_stream_updates_the_candles()
    {
        using var sim = new Sim();
        sim.Transport.Binance.RestDown = sim.Transport.Binance.SocketDown = true;
        sim.Run();
        AdvanceUntilKrakenSocket(sim, TimeSpan.FromMinutes(2));
        AssertHoldsExactlyTheKrakenHistory(sim.Client);

        sim.Push(Exchange.Kraken, Fixtures.KrakenWsSubscribeAck);
        sim.Push(Exchange.Kraken, Fixtures.KrakenWsSnapshot);
        sim.Push(Exchange.Kraken, Fixtures.KrakenWsUpdate);

        var candles = sim.Client.Candles.Snapshot();
        Assert.Equal(721, candles.Count);
        Assert.Equal(
            new Candle(Fixtures.Utc("2026-10-31T02:05:00Z"), 77218.7m, 77231.0m, 77218.7m, 77229.9m, 0.81234567m, false),
            candles[^1]);
        Assert.Equal(FeedState.Live, sim.Client.State);

        sim.AdvanceFeeding(Exchange.Kraken, Fixtures.Utc("2026-10-31T02:10:01.511Z"), TimeSpan.FromSeconds(5),
            Fixtures.KrakenWsUpdate);
        sim.Push(Exchange.Kraken, Fixtures.KrakenWsUpdateNext);

        candles = sim.Client.Candles.Snapshot();
        Assert.Equal(722, candles.Count);
        Assert.Equal(
            new Candle(Fixtures.Utc("2026-10-31T02:10:00Z"), 77230.1m, 77230.1m, 77226.4m, 77226.4m, 0.0441m, false),
            candles[^1]);
        Assert.Equal(Fixtures.Utc("2026-10-31T02:05:00Z"), candles[^2].OpenTime);
        Assert.Equal(MarketSource.Kraken, sim.Client.Candles.Source);
    }
}
