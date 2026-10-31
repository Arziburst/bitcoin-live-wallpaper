using System.Globalization;
using System.Text.Json;
using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Market.Data.Tests.Fakes;

namespace BitcoinWallpaper.Market.Data.Tests;

// Contract #13, criterion 1: while on Kraken after a failover, the client tries Binance again every 30 min
// (jittered +-10 %); on success it switches back with a full history reload (the buffer is cleared, never
// mixed); a failed try stays on Kraken and does not count as a Kraken failure.
//
// API: no new public member is needed; everything is observed through the existing MarketDataClient
// (Source, Candles, Ticker, State) and the fake transport's call log. Access points the tests rely on:
//   - a "try" is one Binance connect cycle (REST history + WebSocket, as today); all requests of one try
//     happen at one instant of the fake clock, so tries are the distinct instants of Binance calls;
//   - the +-10 % is drawn from the constructor's jitter function (the client's only source of randomness,
//     per its existing remarks), mapped linearly onto [27 min, 33 min];
//   - the interval is counted from the switch to Kraken (the decision after the last failed Binance cycle,
//     or the first Kraken request: either), then from the previous try;
//   - a successful try = Binance REST history (klines limit=1000) + ticker + socket all succeed.
// Fake clock and fake transport (Sim), single-threaded and deterministic.
public class ReturnToPrimaryTests
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(27);
    private static readonly TimeSpan MaxInterval = TimeSpan.FromMinutes(33);
    private static readonly TimeSpan FeedEvery = TimeSpan.FromSeconds(5);

    /// <summary>Binance fully down from the start: 3 failed cycles, then Kraken. Returns the instant of the
    /// switch (the first Kraken request); the Kraken socket is open and has sent its snapshot.</summary>
    private static DateTimeOffset GoToKraken(Sim sim)
    {
        sim.Transport.Binance.RestDown = sim.Transport.Binance.SocketDown = true;
        sim.Run();
        var end = sim.Now + TimeSpan.FromMinutes(2);
        while (sim.Transport.OpenSocket(Exchange.Kraken) is null && sim.Now < end)
        {
            sim.Advance(TimeSpan.FromMilliseconds(500));
        }

        Assert.NotNull(sim.Transport.OpenSocket(Exchange.Kraken));
        Assert.Equal(MarketSource.Kraken, sim.Client.Source);
        sim.Push(Exchange.Kraken, Fixtures.KrakenWsSubscribeAck);
        sim.Push(Exchange.Kraken, Fixtures.KrakenWsSnapshot);
        sim.Push(Exchange.Kraken, Fixtures.KrakenWsUpdate);
        Assert.Equal(721, sim.Client.Candles.Count);
        return sim.Transport.CallsTo(Exchange.Kraken).Min(c => c.At);
    }

    /// <summary>Advances to <paramref name="target"/> in 5 s steps like two live exchanges: after each step the
    /// open Kraken socket gets an OHLC update and the open Binance socket a miniTicker, if there is one.</summary>
    private static void Feed(Sim sim, DateTimeOffset target)
    {
        while (sim.Now < target)
        {
            var next = sim.Now + FeedEvery;
            sim.AdvanceTo(next < target ? next : target);
            sim.Transport.OpenSocket(Exchange.Kraken)?.Push(Fixtures.KrakenWsUpdate);
            sim.Drain();
            sim.Transport.OpenSocket(Exchange.Binance)?.Push(Fixtures.BinanceMiniTicker);
            sim.Drain();
        }
    }

    /// <summary>Binance tries after the switch: distinct instants of Binance calls.</summary>
    private static List<DateTimeOffset> TriesAfter(Sim sim, DateTimeOffset switchedAt) =>
        sim.Transport.CycleTimes(Exchange.Binance).Where(t => t > switchedAt).ToList();

    /// <summary>The first try counts from the switch to Kraken: no earlier than 27 min after the last failed
    /// Binance cycle (the switch decision) and no later than 33 min after the first Kraken request.</summary>
    private static void AssertFirstTry(Sim sim, DateTimeOffset switchedAt, DateTimeOffset firstTry, string what)
    {
        var decidedAt = sim.Transport.CycleTimes(Exchange.Binance).Where(t => t <= switchedAt).Max();
        Assert.True(firstTry - decidedAt >= MinInterval && firstTry - switchedAt <= MaxInterval,
            $"{what}: {firstTry - decidedAt} after the last failed Binance cycle, {firstTry - switchedAt} after the " +
            $"switch to Kraken; expected 30 min +-10 % ({MinInterval}..{MaxInterval})");
    }

    private static void AssertInterval(TimeSpan actual, string what) =>
        Assert.True(actual >= MinInterval && actual <= MaxInterval,
            $"{what}: {actual}, expected 30 min +-10 % ({MinInterval}..{MaxInterval})");

    /// <summary>Independent reading of the Binance REST klines rows (open time, O, H, L, C, volume).</summary>
    private static List<(DateTimeOffset OpenTime, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume)> BinanceRestRows()
    {
        using var doc = JsonDocument.Parse(Fixtures.BinanceKlines);
        var list = new List<(DateTimeOffset, decimal, decimal, decimal, decimal, decimal)>();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            list.Add((
                DateTimeOffset.FromUnixTimeMilliseconds(row[0].GetInt64()),
                decimal.Parse(row[1].GetString()!, CultureInfo.InvariantCulture),
                decimal.Parse(row[2].GetString()!, CultureInfo.InvariantCulture),
                decimal.Parse(row[3].GetString()!, CultureInfo.InvariantCulture),
                decimal.Parse(row[4].GetString()!, CultureInfo.InvariantCulture),
                decimal.Parse(row[5].GetString()!, CultureInfo.InvariantCulture)));
        }

        return list;
    }

    private static string? QueryValue(Uri url, string name) =>
        url.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2 && string.Equals(p[0], name, StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1])
            .FirstOrDefault();

    private static void AssertStillOnKraken(Sim sim, FakeSocket krakenSocket)
    {
        Assert.Equal(MarketSource.Kraken, sim.Client.Source);
        Assert.Equal(MarketSource.Kraken, sim.Client.Candles.Source);
        Assert.Equal(721, sim.Client.Candles.Count);
        Assert.Equal(Fixtures.Utc("2026-10-31T02:05:00Z"), sim.Client.Candles.Snapshot()[^1].OpenTime);
        Assert.Equal(77229.9m, sim.Client.Candles.Snapshot()[^1].Close); // the live Kraken update, not reloaded
        Assert.True(krakenSocket.IsOpen, "the Kraken socket was dropped by a failed Binance try");
        Assert.Single(sim.Transport.Sockets, s => s.Exchange == Exchange.Kraken);
        Assert.Single(sim.Transport.CallsTo(Exchange.Kraken), c => !c.IsSocket); // no Kraken history reload
        Assert.Equal(FeedState.Live, sim.Client.State);
    }

    [Fact]
    public void While_on_Kraken_Binance_is_tried_every_30_minutes()
    {
        using var sim = new Sim();
        var switchedAt = GoToKraken(sim);

        Feed(sim, switchedAt + TimeSpan.FromMinutes(100));

        var tries = TriesAfter(sim, switchedAt);
        Assert.True(tries.Count == 3, $"expected 3 Binance tries in 100 min on Kraken, got {tries.Count}");
        AssertFirstTry(sim, switchedAt, tries[0], "switch to Kraken -> first Binance try");
        for (var i = 1; i < tries.Count; i++)
        {
            AssertInterval(tries[i] - tries[i - 1], $"Binance try {i} -> try {i + 1}");
        }

        Assert.Equal(MarketSource.Kraken, sim.Client.Source);
        Assert.Equal(MarketSource.Kraken, sim.Client.Candles.Source);
    }

    [Fact]
    public void The_30_minute_interval_is_jittered_by_up_to_10_percent()
    {
        static TimeSpan FirstTryAfterSwitch(double draw)
        {
            using var sim = new Sim(jitter: () => draw);
            var switchedAt = GoToKraken(sim);
            Feed(sim, switchedAt + TimeSpan.FromMinutes(40));
            var tries = TriesAfter(sim, switchedAt);
            Assert.True(tries.Count > 0, $"no Binance try within 40 min on Kraken (jitter draw {draw})");
            AssertFirstTry(sim, switchedAt, tries[0], $"first try, jitter draw {draw}");
            return tries[0] - switchedAt;
        }

        var low = FirstTryAfterSwitch(0.0);
        var high = FirstTryAfterSwitch(0.999);

        // Uniform over +-10 % (27..33 min): the extreme draws land about 6 min apart.
        Assert.True(high - low >= TimeSpan.FromMinutes(5),
            $"the jitter barely moves the try: draw 0.0 -> {low}, draw 0.999 -> {high}");
    }

    [Fact]
    public void A_successful_try_switches_back_to_Binance_with_a_full_reload_and_no_Kraken_candle()
    {
        using var sim = new Sim();
        var switchedAt = GoToKraken(sim);
        Feed(sim, switchedAt + TimeSpan.FromMinutes(10));
        sim.Transport.Binance.Heal();

        Feed(sim, switchedAt + TimeSpan.FromMinutes(34));

        var tries = TriesAfter(sim, switchedAt);
        Assert.True(tries.Count > 0, "no Binance try within 34 min on Kraken");
        var returnedAt = tries[0];
        AssertFirstTry(sim, switchedAt, returnedAt, "switch to Kraken -> successful Binance try");

        Assert.Equal(MarketSource.Binance, sim.Client.Source);
        Assert.Equal(MarketSource.Binance, sim.Client.Candles.Source);

        // Full history reload: the whole klines request, and the buffer holds exactly the Binance history.
        var klines = sim.Transport.CallsTo(Exchange.Binance)
            .Where(c => !c.IsSocket && c.At == returnedAt && c.Url.AbsolutePath.Contains("klines", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Single(klines);
        Assert.Equal("1000", QueryValue(klines[0].Url, "limit"));
        Assert.Equal(
            BinanceRestRows(),
            sim.Client.Candles.Snapshot().Select(c => (c.OpenTime, c.Open, c.High, c.Low, c.Close, c.Volume)));
        Assert.Contains(sim.Client.Ticker?.Last, new decimal?[] { 77404.54m, 77410.00m }); // Binance REST / miniTicker
        Assert.Equal(FeedState.Live, sim.Client.State);

        // The Kraken feed is gone; a late Kraken message (if any socket survived) never enters the series.
        foreach (var socket in sim.Transport.Sockets.Where(s => s.Exchange == Exchange.Kraken && s.IsOpen).ToList())
        {
            socket.Push(Fixtures.KrakenWsUpdateNext);
            sim.Drain();
        }

        Assert.DoesNotContain(sim.Transport.Sockets, s => s.Exchange == Exchange.Kraken && s.IsOpen);
        Assert.Equal(
            BinanceRestRows(),
            sim.Client.Candles.Snapshot().Select(c => (c.OpenTime, c.Open, c.High, c.Low, c.Close, c.Volume)));

        // Back on the primary: the Binance stream drives the series, and nothing probes Kraken any more.
        sim.Push(Exchange.Binance, Fixtures.BinanceKlineForming);
        var last = sim.Client.Candles.Snapshot()[^1];
        Assert.Equal(new Candle(Fixtures.Utc("2026-10-31T02:05:00Z"), 77385.25m, 77412.50m, 77385.24m, 77410.00m, 2.10413m, false), last);
        Assert.Equal(MarketSource.Binance, sim.Client.Candles.Source);

        Feed(sim, returnedAt + TimeSpan.FromMinutes(70));
        Assert.DoesNotContain(sim.Transport.Calls, c => c.Exchange == Exchange.Kraken && c.At > returnedAt);
        Assert.Equal(MarketSource.Binance, sim.Client.Source);
        Assert.Equal(FeedState.Live, sim.Client.State);
    }

    [Fact]
    public void A_failed_try_stays_on_Kraken_without_touching_the_Kraken_feed()
    {
        using var sim = new Sim();
        var switchedAt = GoToKraken(sim);
        var krakenSocket = sim.Transport.OpenSocket(Exchange.Kraken)!;

        Feed(sim, switchedAt + TimeSpan.FromMinutes(35)); // Binance still down

        Assert.True(TriesAfter(sim, switchedAt).Count == 1, "expected exactly one (failed) Binance try in 35 min");
        AssertStillOnKraken(sim, krakenSocket);
    }

    [Fact]
    public void A_try_whose_Binance_socket_is_refused_fails_and_leaves_the_Kraken_series_alone()
    {
        using var sim = new Sim();
        var switchedAt = GoToKraken(sim);
        var krakenSocket = sim.Transport.OpenSocket(Exchange.Kraken)!;
        sim.Transport.Binance.RestDown = false; // REST answers, the socket is still refused

        Feed(sim, switchedAt + TimeSpan.FromMinutes(35));

        var tries = TriesAfter(sim, switchedAt);
        Assert.True(tries.Count == 1, $"expected exactly one Binance try in 35 min, got {tries.Count}");
        Assert.Contains(sim.Transport.CallsTo(Exchange.Binance), c => c.IsSocket && c.At == tries[0]);
        AssertStillOnKraken(sim, krakenSocket); // the Binance REST history of the failed try is not committed
    }

    [Fact]
    public void Failed_tries_do_not_count_as_Kraken_failures()
    {
        using var sim = new Sim();
        var switchedAt = GoToKraken(sim);
        Feed(sim, switchedAt + TimeSpan.FromMinutes(65)); // Binance still down: two failed tries
        Assert.True(TriesAfter(sim, switchedAt).Count == 2, "expected two failed Binance tries in 65 min");

        // Two failed Kraken cycles: below the 3 that switch sources, unless the Binance tries were counted.
        sim.Transport.Kraken.FailNextSockets = 2;
        var droppedAt = sim.Now;
        sim.Transport.OpenSocket(Exchange.Kraken)!.ServerClose();
        sim.Drain();
        Feed(sim, droppedAt + TimeSpan.FromSeconds(60));

        var krakenConnects = sim.Transport.CallsTo(Exchange.Kraken).Count(c => c.IsSocket && c.At > droppedAt);
        Assert.Equal(3, krakenConnects); // two refused, the third one holds
        Assert.NotNull(sim.Transport.OpenSocket(Exchange.Kraken));
        Assert.DoesNotContain(sim.Transport.Calls, c => c.Exchange == Exchange.Binance && c.At > droppedAt);
        Assert.Equal(MarketSource.Kraken, sim.Client.Source);
        Assert.Equal(MarketSource.Kraken, sim.Client.Candles.Source);
    }
}
