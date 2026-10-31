using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Market.Data.Tests.Fakes;

namespace BitcoinWallpaper.Market.Data.Tests;

// Contract #13, criterion 2: when the WebSocket of the current source fails 3 times in a row but its REST
// works, the client polls REST (klines limit 2 + the 24h ticker; Kraken OHLC + its ticker) every 15 s, keeps
// State Live while data flows, and goes back to the WebSocket when a reconnect try (every 2 min) succeeds.
//
// API: no new public member is needed; everything is observed through the existing MarketDataClient
// (Source, Candles, Ticker, State) and the fake transport's call log. (A FeedMode/Mode property for
// tools/DataProbe is the worker's choice, additive, and not pinned here.) Access points the tests rely on:
//   - a Binance poll = GET .../api/v3/klines with query limit=2 plus GET .../api/v3/ticker/24hr, at one
//     instant; the connect cycle keeps its limit=1000 history request, so limit=2 marks polls only;
//   - a Kraken poll = GET .../0/public/OHLC plus GET .../0/public/Ticker, at one instant; the Kraken connect
//     cycle does not request the Ticker (it derives the 24 h ticker from candles, as today), so Ticker
//     requests mark Kraken polls;
//   - polls repeat every 15 s (+-1 s), a reconnect try is a socket connect every 2 min (+-10 s);
//   - "3 times in a row": 3 consecutive connect cycles whose REST answered and whose socket was refused (a
//     server close of a working socket is not one); the first poll follows within 30 s, 2 refusals do not
//     start polling, and there is no switch to the other source while the current source's REST answers.
// Fake clock and fake transport (Sim), single-threaded and deterministic.
public class RestPollingFallbackTests
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollTolerance = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryEvery = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RetryTolerance = TimeSpan.FromSeconds(10);

    /// <summary>The first poll comes at most this long after the 3rd refused socket (one poll period plus a
    /// reconnect backoff).</summary>
    private static readonly TimeSpan FirstPollWithin = TimeSpan.FromSeconds(30);

    private static bool PathHas(TransportCall call, string part) =>
        call.Url.AbsolutePath.Contains(part, StringComparison.OrdinalIgnoreCase);

    private static string? QueryValue(Uri url, string name) =>
        url.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2 && string.Equals(p[0], name, StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1])
            .FirstOrDefault();

    /// <summary>Instants of Binance polls (klines limit=2).</summary>
    private static List<DateTimeOffset> BinancePolls(Sim sim) =>
        sim.Transport.CallsTo(Exchange.Binance)
            .Where(c => !c.IsSocket && PathHas(c, "klines") && QueryValue(c.Url, "limit") == "2")
            .Select(c => c.At)
            .Distinct()
            .OrderBy(t => t)
            .ToList();

    /// <summary>Instants of Kraken polls (Ticker requests).</summary>
    private static List<DateTimeOffset> KrakenPolls(Sim sim) =>
        sim.Transport.CallsTo(Exchange.Kraken)
            .Where(c => !c.IsSocket && PathHas(c, "ticker"))
            .Select(c => c.At)
            .Distinct()
            .OrderBy(t => t)
            .ToList();

    private static List<DateTimeOffset> SocketConnects(Sim sim, Exchange exchange) =>
        sim.Transport.CallsTo(exchange).Where(c => c.IsSocket).Select(c => c.At).OrderBy(t => t).ToList();

    private static void AssertEvery15s(IReadOnlyList<DateTimeOffset> polls, string what)
    {
        for (var i = 1; i < polls.Count; i++)
        {
            var gap = polls[i] - polls[i - 1];
            Assert.True(gap >= PollEvery - PollTolerance && gap <= PollEvery + PollTolerance,
                $"{what} {i} -> {i + 1}: {gap} apart ({polls[i - 1]:O} -> {polls[i]:O}), expected 15 s");
        }
    }

    /// <summary>Binance REST up, its socket refused from the start: 3 refused cycles, then polling.
    /// Returns the instant of the 3rd refused socket.</summary>
    private static DateTimeOffset StartWithBinanceSocketDown(Sim sim, TimeSpan run)
    {
        sim.Transport.Binance.SocketDown = true;
        sim.Run();
        sim.Advance(run);
        var connects = SocketConnects(sim, Exchange.Binance);
        Assert.True(connects.Count >= 3, $"only {connects.Count} Binance socket connects in {run}");
        return connects[2];
    }

    [Fact]
    public void Binance_socket_refused_3_times_with_REST_up_polls_klines_limit_2_and_the_ticker_every_15_s()
    {
        using var sim = new Sim();
        var thirdFailure = StartWithBinanceSocketDown(sim, TimeSpan.FromMinutes(5));

        var polls = BinancePolls(sim);
        Assert.True(polls.Count > 0, "no Binance REST poll (klines limit=2) in 5 min with the socket refused");

        // Polling starts with the 3rd refused socket in a row, not earlier, and within one poll period.
        Assert.Equal(3, SocketConnects(sim, Exchange.Binance).Count(t => t <= polls[0]));
        Assert.True(polls[0] >= thirdFailure && polls[0] - thirdFailure <= FirstPollWithin,
            $"first poll at {polls[0]:O}, the 3rd refused socket at {thirdFailure:O}");

        AssertEvery15s(polls, "Binance poll");
        Assert.True(polls.Count >= 18, $"{polls.Count} polls in ~5 min, expected one every 15 s");
        foreach (var at in polls)
        {
            Assert.Contains(sim.Transport.CallsTo(Exchange.Binance),
                c => !c.IsSocket && c.At == at && PathHas(c, "ticker/24hr"));
        }

        // The current source's REST works: no failover.
        Assert.Empty(sim.Transport.CallsTo(Exchange.Kraken));
        Assert.Equal(MarketSource.Binance, sim.Client.Source);
        Assert.Equal(MarketSource.Binance, sim.Client.Candles.Source);
    }

    [Fact]
    public void Two_refused_sockets_do_not_start_polling()
    {
        using var sim = new Sim();
        sim.Run();
        sim.Push(Exchange.Binance, Fixtures.BinanceKlineForming);
        sim.Transport.Binance.FailNextSockets = 2;
        var droppedAt = sim.Now;
        sim.Transport.OpenSocket(Exchange.Binance)!.ServerClose();
        sim.Drain();

        sim.AdvanceFeeding(Exchange.Binance, droppedAt + TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(1),
            Fixtures.BinanceMiniTicker);

        Assert.Equal(3, SocketConnects(sim, Exchange.Binance).Count(t => t > droppedAt));
        Assert.Empty(BinancePolls(sim));
        Assert.Empty(sim.Transport.CallsTo(Exchange.Kraken));

        // ...whereas a third refused one does (same feed, one more refusal).
        sim.Transport.Binance.FailNextSockets = 3;
        var droppedAgainAt = sim.Now;
        sim.Transport.OpenSocket(Exchange.Binance)!.ServerClose();
        sim.Drain();
        sim.Advance(TimeSpan.FromSeconds(40));

        Assert.Contains(BinancePolls(sim), t => t > droppedAgainAt);
        Assert.Empty(sim.Transport.CallsTo(Exchange.Kraken));
    }

    [Fact]
    public void While_polling_the_feed_stays_Live_and_the_polled_candles_reach_the_buffer()
    {
        using var sim = new Sim();
        StartWithBinanceSocketDown(sim, TimeSpan.FromSeconds(30));
        var polls = BinancePolls(sim);
        Assert.True(polls.Count > 0, "no Binance REST poll within 30 s of start with the socket refused");

        // Without a socket, data older than 30 s would read Stale: the polls keep it Live.
        var end = Fixtures.Utc("2026-10-31T02:16:00Z");
        while (sim.Now < end)
        {
            sim.Advance(TimeSpan.FromSeconds(5));
            Assert.True(sim.Client.State == FeedState.Live, $"State {sim.Client.State} at {sim.Now:O} while polling");
        }

        Assert.Equal(MarketSource.Binance, sim.Client.Source);
        Assert.Equal(MarketSource.Binance, sim.Client.Candles.Source);
        Assert.Equal(5, sim.Client.Candles.Count);
        Assert.Equal(77404.54m, sim.Client.Ticker?.Last);

        // The 02:05 candle was forming at start (closes 02:09:59.999); a poll after that delivers it closed.
        var last = sim.Client.Candles.Snapshot()[^1];
        Assert.Equal(Fixtures.Utc("2026-10-31T02:05:00Z"), last.OpenTime);
        Assert.True(last.IsClosed, "the polled klines did not reach the buffer (02:05 candle still forming at 02:16)");
        Assert.Empty(sim.Transport.CallsTo(Exchange.Kraken));
    }

    [Fact]
    public void Polling_without_answers_does_not_fake_a_Live_feed()
    {
        using var sim = new Sim();
        sim.Transport.Kraken.RestDown = sim.Transport.Kraken.SocketDown = true; // nowhere else to go
        StartWithBinanceSocketDown(sim, TimeSpan.FromMinutes(1));
        Assert.True(BinancePolls(sim).Count > 0, "no Binance REST poll (klines limit=2) after 3 refused sockets");
        Assert.Equal(FeedState.Live, sim.Client.State);

        sim.Transport.Binance.RestDown = true;
        sim.Advance(TimeSpan.FromSeconds(45));

        Assert.Equal(FeedState.Stale, sim.Client.State);
        Assert.Equal(MarketSource.Binance, sim.Client.Candles.Source);
    }

    [Fact]
    public void While_polling_the_socket_is_retried_every_2_minutes()
    {
        using var sim = new Sim();
        var thirdFailure = StartWithBinanceSocketDown(sim, TimeSpan.FromMinutes(9));
        Assert.True(BinancePolls(sim).Count > 0, "no Binance REST poll (klines limit=2) after 3 refused sockets");

        var retries = SocketConnects(sim, Exchange.Binance).Where(t => t > thirdFailure).ToList();
        Assert.True(retries.Count >= 3, $"{retries.Count} socket retries in ~9 min of polling, expected one every 2 min");
        var first = retries[0] - thirdFailure;
        Assert.True(first >= RetryEvery - RetryTolerance && first <= RetryEvery + FirstPollWithin + RetryTolerance,
            $"first socket retry {first} after the 3rd refused socket, expected about 2 min");
        for (var i = 1; i < retries.Count; i++)
        {
            var gap = retries[i] - retries[i - 1];
            Assert.True(gap >= RetryEvery - RetryTolerance && gap <= RetryEvery + RetryTolerance,
                $"socket retry {i} -> {i + 1}: {gap} apart, expected 2 min");
        }

        AssertEvery15s(BinancePolls(sim), "Binance poll"); // a failed retry does not interrupt the polling
        Assert.Empty(sim.Transport.CallsTo(Exchange.Kraken));
    }

    [Fact]
    public void A_successful_socket_retry_ends_the_polling()
    {
        using var sim = new Sim();
        StartWithBinanceSocketDown(sim, TimeSpan.FromMinutes(3));
        Assert.True(BinancePolls(sim).Count > 0, "no Binance REST poll (klines limit=2) after 3 refused sockets");

        sim.Transport.Binance.SocketDown = false;
        var healedAt = sim.Now;
        var end = healedAt + RetryEvery + RetryTolerance + PollEvery;
        while (sim.Transport.OpenSocket(Exchange.Binance) is null && sim.Now < end)
        {
            sim.Advance(TimeSpan.FromSeconds(1));
        }

        var socket = sim.Transport.OpenSocket(Exchange.Binance);
        Assert.True(socket is not null, $"no Binance socket within {end - healedAt} of the socket coming back");
        var reconnectedAt = socket!.OpenedAt;

        sim.AdvanceFeeding(Exchange.Binance, reconnectedAt + TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(5),
            Fixtures.BinanceMiniTicker);

        Assert.DoesNotContain(BinancePolls(sim), t => t > reconnectedAt);
        Assert.Same(socket, sim.Transport.OpenSocket(Exchange.Binance));
        Assert.Equal(FeedState.Live, sim.Client.State);

        sim.Push(Exchange.Binance, Fixtures.BinanceKlineForming);
        Assert.Equal(
            new Candle(Fixtures.Utc("2026-10-31T02:05:00Z"), 77385.25m, 77412.50m, 77385.24m, 77410.00m, 2.10413m, false),
            sim.Client.Candles.Snapshot()[^1]);
        Assert.Equal(MarketSource.Binance, sim.Client.Candles.Source);
        Assert.Empty(sim.Transport.CallsTo(Exchange.Kraken));
    }

    [Fact]
    public void On_Kraken_a_socket_refused_3_times_with_REST_up_polls_OHLC_and_the_ticker_every_15_s()
    {
        using var sim = new Sim();
        sim.Transport.Binance.RestDown = sim.Transport.Binance.SocketDown = true; // failover to Kraken
        sim.Transport.Kraken.SocketDown = true;
        sim.Run();
        sim.Advance(TimeSpan.FromMinutes(6));

        var krakenConnects = SocketConnects(sim, Exchange.Kraken);
        Assert.True(krakenConnects.Count >= 3, $"only {krakenConnects.Count} Kraken socket connects");
        var thirdFailure = krakenConnects[2];

        var polls = KrakenPolls(sim);
        Assert.True(polls.Count > 0, "no Kraken REST poll (OHLC + Ticker) in 6 min with its socket refused");
        Assert.Equal(3, krakenConnects.Count(t => t <= polls[0]));
        Assert.True(polls[0] >= thirdFailure && polls[0] - thirdFailure <= FirstPollWithin,
            $"first Kraken poll at {polls[0]:O}, the 3rd refused socket at {thirdFailure:O}");
        AssertEvery15s(polls, "Kraken poll");
        Assert.True(polls.Count >= 20, $"{polls.Count} Kraken polls in ~5.5 min, expected one every 15 s");
        foreach (var at in polls)
        {
            Assert.Contains(sim.Transport.CallsTo(Exchange.Kraken), c => !c.IsSocket && c.At == at && PathHas(c, "ohlc"));
        }

        // Stays on Kraken, Live, with the polled Kraken candles (02:05 closed by now).
        Assert.Equal(MarketSource.Kraken, sim.Client.Source);
        Assert.Equal(MarketSource.Kraken, sim.Client.Candles.Source);
        Assert.Equal(FeedState.Live, sim.Client.State);
        Assert.NotNull(sim.Client.Ticker);
        var rows = Fixtures.KrakenRestRows();
        var candles = sim.Client.Candles.Snapshot();
        Assert.Equal(rows.Select(r => r.OpenTime), candles.Select(c => c.OpenTime));
        Assert.True(candles[^1].IsClosed, $"the polled OHLC did not reach the buffer (last candle still forming at {sim.Now:O})");
    }
}
