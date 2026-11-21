using System.Globalization;
using System.Runtime.ExceptionServices;
using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Market.Data.Tests.Fakes;

namespace BitcoinWallpaper.Market.Data.Tests;

// Contract #30, criteria 2 and 3 (D-25) at the data client, on the fake clock and the fake transport.
//
// API SKETCH (written before the code, #237). Everything else is the existing client.
//   MarketDataClient (src/Market.Data/MarketDataClient.cs), new public members:
//     public ExchangeClock Clock { get; }              // the client's estimator, fed by its time requests; see
//                                                      // ExchangeClockTests for ExchangeClock's own API
//     public DateTimeOffset ExchangeNow { get; }       // Clock.Now(<the client's TimeProvider>.GetUtcNow())
//   Time requests (only these, nothing else of the client changes its timing):
//     - Binance: GET https://data-api.binance.vision/api/v3/time, answer {"serverTime": <ms>};
//       Kraken (when it is the source): GET https://api.kraken.com/0/public/Time,
//       answer {"error":[],"result":{"unixtime": <whole seconds>}} (so +-1 s of error is expected).
//     - the first one right after a connect cycle's history (klines + ticker, or OHLC) was fetched, then one every
//       10 min while the feed is live; a request is a sample (local send, local receive, server time) for Clock.
//     - the status of a time answer never sets notBefore (Retry-After ignored), never counts as a failed cycle,
//       never triggers the 451 switch; an exception, a bad status or a bad body only means "no sample this time".
//   The corrected clock (ExchangeNow) is the "now" of BinanceParser.ParseKlines, KrakenParser.ParseOhlc / ParseWsOhlc
//   and CandleAggregator.Aggregate / Bucket calls the client makes; every wait, the silence limit, the stale check
//   (State), the 23 h renewal, backoff and the primary tries keep the client's own TimeProvider.
//   Test-only edits of existing code: FakeTransport's ExchangeScript.TimeSkew (see the commit message).
public class ExchangeTimeClientTests
{
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    private static readonly Uri BinanceTime = new("https://data-api.binance.vision/api/v3/time");
    private static readonly Uri KrakenTime = new("https://api.kraken.com/0/public/Time");

    private static bool PathHas(TransportCall c, string part) =>
        c.Url.AbsolutePath.Contains(part, StringComparison.OrdinalIgnoreCase);

    private static List<TransportCall> Rest(FakeTransport t, Exchange e, string part) =>
        t.CallsTo(e).Where(c => !c.IsSocket && PathHas(c, part)).ToList();

    private static void AssertNear(TimeSpan expected, TimeSpan actual, TimeSpan tolerance, string what) =>
        Assert.True((actual - expected).Duration() <= tolerance, $"{what}: expected {expected} +-{tolerance}, got {actual}");

    /// <summary>Binance fully down until the client has switched to Kraken; the Kraken socket is open with a snapshot.</summary>
    private static void GoToKraken(Sim sim, bool snapshot = true)
    {
        sim.Transport.Binance.RestDown = sim.Transport.Binance.SocketDown = true;
        sim.Run();
        var end = sim.Now + TimeSpan.FromMinutes(2);
        while (sim.Transport.OpenSocket(Exchange.Kraken) is null && sim.Now < end)
        {
            sim.Advance(TimeSpan.FromMilliseconds(500));
        }

        Assert.NotNull(sim.Transport.OpenSocket(Exchange.Kraken));
        sim.Push(Exchange.Kraken, Fixtures.KrakenWsSubscribeAck);
        if (snapshot)
        {
            sim.Push(Exchange.Kraken, Fixtures.KrakenWsSnapshot);
        }
    }

    private static string BinanceKline(decimal close) =>
        "{\"e\":\"kline\",\"E\":1793412330000,\"s\":\"BTCUSDT\",\"k\":{\"t\":1793412300000,\"T\":1793412599999,\"s\":\"BTCUSDT\"," +
        "\"i\":\"5m\",\"o\":\"77385.25000000\",\"c\":\"" + close.ToString(CultureInfo.InvariantCulture) + "\",\"h\":\"77999.00000000\"," +
        "\"l\":\"60000.00000000\",\"v\":\"2.10413000\",\"x\":false}}";

    private static string KrakenUpdate(decimal close) =>
        "{\"channel\":\"ohlc\",\"type\":\"update\",\"timestamp\":\"2026-10-31T02:05:31.009871233Z\",\"data\":[{\"symbol\":\"BTC/USD\"," +
        "\"open\":77218.7,\"high\":77999.0,\"low\":60000.0,\"close\":" + close.ToString(CultureInfo.InvariantCulture) +
        ",\"trades\":45,\"volume\":0.81234567,\"vwap\":77224.1,\"interval_begin\":\"2026-10-31T02:05:00.000000000Z\"," +
        "\"interval\":5,\"timestamp\":\"2026-10-31T02:10:00.000000Z\"}]}";

    // ---- criterion 2: when the time is asked -----------------------------------------------------------------------

    [Fact]
    public void Binance_time_is_asked_after_the_history_of_the_connect_cycle()
    {
        using var sim = new Sim();
        sim.Run();
        sim.AdvanceFeeding(Exchange.Binance, sim.Start + TimeSpan.FromSeconds(30), Tick, Fixtures.BinanceMiniTicker);

        var rest = sim.Transport.CallsTo(Exchange.Binance).Where(c => !c.IsSocket).ToList();
        var time = rest.FindIndex(c => PathHas(c, "time"));
        Assert.True(time >= 0, "no Binance time request in the first 30 s");
        Assert.True(time > rest.FindIndex(c => PathHas(c, "klines")), "the time was asked before the klines history");
        Assert.True(time > rest.FindIndex(c => PathHas(c, "ticker")), "the time was asked before the ticker");
        Assert.Equal(BinanceTime.AbsoluteUri, rest[time].Url.AbsoluteUri);
        Assert.Equal(sim.Start, rest[time].At);   // right after the history, not after some delay
        Assert.Single(Rest(sim.Transport, Exchange.Binance, "klines")); // asking the time reloads nothing
    }

    [Fact]
    public void Binance_time_is_asked_every_10_minutes_while_live()
    {
        using var sim = new Sim();
        sim.Run();
        sim.AdvanceFeeding(Exchange.Binance, sim.Start + TimeSpan.FromMinutes(35), Tick, Fixtures.BinanceMiniTicker);

        var asked = Rest(sim.Transport, Exchange.Binance, "time").Select(c => c.At).ToList();
        Assert.Equal(4, asked.Count);   // at the start, +10, +20, +30 min
        for (var i = 1; i < asked.Count; i++)
        {
            AssertNear(TimeSpan.FromMinutes(10), asked[i] - asked[i - 1], TimeSpan.FromSeconds(10), $"time request {i}");
        }

        Assert.Single(Rest(sim.Transport, Exchange.Binance, "klines")); // a healthy feed is never reloaded for it
        Assert.Single(sim.Transport.Sockets, s => s.Exchange == Exchange.Binance);
    }

    [Fact]
    public void Kraken_time_is_asked_after_the_history_every_10_minutes_when_Kraken_is_the_source()
    {
        using var sim = new Sim();
        GoToKraken(sim);
        sim.AdvanceFeeding(Exchange.Kraken, sim.Now + TimeSpan.FromMinutes(25), Tick, Fixtures.KrakenWsUpdate);

        var rest = sim.Transport.CallsTo(Exchange.Kraken).Where(c => !c.IsSocket).ToList();
        var time = rest.FindIndex(c => PathHas(c, "time"));
        Assert.True(time >= 0, "no Kraken time request");
        Assert.True(time > rest.FindIndex(c => PathHas(c, "ohlc")), "the Kraken time was asked before its history");
        Assert.Equal(KrakenTime.AbsoluteUri, rest[time].Url.AbsoluteUri);

        var asked = Rest(sim.Transport, Exchange.Kraken, "time").Select(c => c.At).ToList();
        Assert.Equal(3, asked.Count);   // after the history, +10, +20 min
        for (var i = 1; i < asked.Count; i++)
        {
            AssertNear(TimeSpan.FromMinutes(10), asked[i] - asked[i - 1], TimeSpan.FromSeconds(10), $"Kraken time request {i}");
        }

        Assert.Empty(Rest(sim.Transport, Exchange.Binance, "time")); // Binance never got as far as a history
    }

    [Theory]
    [InlineData(90)]
    [InlineData(-90)]
    [InlineData(0)]
    public void The_first_Binance_sample_applies_at_once(int skewSeconds)
    {
        using var sim = new Sim();
        sim.Transport.Binance.TimeSkew = TimeSpan.FromSeconds(skewSeconds);
        sim.Run();
        sim.AdvanceFeeding(Exchange.Binance, sim.Start + TimeSpan.FromSeconds(30), Tick, Fixtures.BinanceMiniTicker);

        Assert.True(sim.Client.Clock.HasOffset);
        AssertNear(TimeSpan.FromSeconds(skewSeconds), sim.Client.Clock.Offset, TimeSpan.FromMilliseconds(1), "offset");
        AssertNear(sim.Now + TimeSpan.FromSeconds(skewSeconds) - sim.Now, sim.Client.ExchangeNow - sim.Now, TimeSpan.FromMilliseconds(1), "ExchangeNow - now");
    }

    [Theory]
    [InlineData(90)]
    [InlineData(-90)]
    public void The_Kraken_sample_applies_within_its_whole_second_precision(int skewSeconds)
    {
        using var sim = new Sim();
        sim.Transport.Kraken.TimeSkew = TimeSpan.FromSeconds(skewSeconds);
        GoToKraken(sim);
        sim.AdvanceFeeding(Exchange.Kraken, sim.Now + TimeSpan.FromSeconds(30), Tick, Fixtures.KrakenWsUpdate);

        Assert.True(sim.Client.Clock.HasOffset);
        AssertNear(TimeSpan.FromSeconds(skewSeconds), sim.Client.ExchangeNow - sim.Now, TimeSpan.FromSeconds(1), "ExchangeNow - now");
    }

    // ---- criterion 3: where the corrected now is used --------------------------------------------------------------

    /// <summary>The exchange clock is 90 s off; the history is fetched again after the offset is known (the socket
    /// drops): the candle's IsClosed follows the exchange, not the PC.</summary>
    private static void AssertIsClosedFollowsTheExchange(
        Exchange exchange, TimeSpan? interval, string localStart, int skewSeconds, bool closedByExchange)
    {
        using var sim = new Sim(Fixtures.Utc(localStart));
        sim.Transport.Script(exchange).TimeSkew = TimeSpan.FromSeconds(skewSeconds);
        if (interval is { } wanted)
        {
            sim.Client.ChangeInterval(wanted);
        }

        if (exchange == Exchange.Kraken)
        {
            GoToKraken(sim, snapshot: false); // a WS snapshot is parsed after the first sample: it would already be corrected
        }
        else
        {
            sim.Run();
        }

        sim.AdvanceFeeding(exchange, sim.Now + TimeSpan.FromSeconds(10), Tick, exchange == Exchange.Binance ? Fixtures.BinanceMiniTicker : Fixtures.KrakenWsHeartbeat);
        Assert.True(sim.Client.Clock.HasOffset, "no sample yet");
        Assert.Equal(source(exchange), sim.Client.Candles.Source);
        var before = sim.Client.Candles.Snapshot()[^1];
        Assert.Equal(!closedByExchange, before.IsClosed); // the first history was parsed before any sample: the PC's clock

        sim.Transport.OpenSocket(exchange)!.ServerClose();
        sim.Drain();
        sim.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(2, Rest(sim.Transport, exchange, exchange == Exchange.Binance ? "klines" : "ohlc").Count); // reloaded

        var after = sim.Client.Candles.Snapshot()[^1];
        Assert.Equal(before.OpenTime, after.OpenTime);
        Assert.Equal(closedByExchange, after.IsClosed);

        static MarketSource source(Exchange e) => e == Exchange.Binance ? MarketSource.Binance : MarketSource.Kraken;
    }

    // Local 02:08:30 (the exchange 90 s ahead: 02:10:00, the 02:05 candle is closed) and local 02:10:30 (the exchange
    // 90 s behind: 02:09:00, still forming).
    [Theory]
    [InlineData("Binance", "2026-10-31T02:08:30Z", 90, true)]
    [InlineData("Binance", "2026-10-31T02:10:30Z", -90, false)]
    [InlineData("Kraken", "2026-10-31T02:08:30Z", 90, true)]
    [InlineData("Kraken", "2026-10-31T02:10:30Z", -90, false)]
    public void The_history_parsers_take_the_exchange_time_for_IsClosed(string exchange, string localStart, int skewSeconds, bool closed) =>
        AssertIsClosedFollowsTheExchange(Enum.Parse<Exchange>(exchange), null, localStart, skewSeconds, closed);

    // Kraken's WS candle: IsClosed = interval_begin + 5 min <= the exchange's now (the update's forming candle is the
    // 02:05 one; it closes at 02:10:00 exchange time).
    [Theory]
    [InlineData("2026-10-31T02:08:30Z", 90, true)]
    [InlineData("2026-10-31T02:10:30Z", -90, false)]
    public void The_Kraken_stream_parser_takes_the_exchange_time_for_IsClosed(string localStart, int skewSeconds, bool closed)
    {
        using var sim = new Sim(Fixtures.Utc(localStart));
        sim.Transport.Kraken.TimeSkew = TimeSpan.FromSeconds(skewSeconds);
        GoToKraken(sim, snapshot: false);
        Assert.True(sim.Client.Clock.HasOffset);

        sim.Push(Exchange.Kraken, Fixtures.KrakenWsUpdate);

        Assert.Equal(closed, sim.Client.Candles.Snapshot()[^1].IsClosed);
    }

    // The 6 h candles of Kraken are aggregated from 60 min ones: the bucket 00:00-06:00 closes at 06:00 exchange time.
    [Theory]
    [InlineData("2026-10-31T05:58:30Z", 90, true)]
    [InlineData("2026-10-31T06:00:30Z", -90, false)]
    public void The_aggregator_takes_the_exchange_time_for_IsClosed(string localStart, int skewSeconds, bool closed) =>
        AssertIsClosedFollowsTheExchange(Exchange.Kraken, TimeSpan.FromHours(6), localStart, skewSeconds, closed);

    /// <summary>The exchange clock steps by +90 s then -90 s (three samples of each flip the median) while the PC
    /// clock runs on: the socket is never dropped, State never goes stale, the forming candle keeps following the
    /// stream.</summary>
    [Theory]
    [InlineData("Binance")]
    [InlineData("Kraken")]
    public void An_offset_step_of_90_seconds_neither_drops_the_socket_nor_freezes_the_forming_candle(string exchangeName)
    {
        var exchange = Enum.Parse<Exchange>(exchangeName);
        using var sim = new Sim();
        if (exchange == Exchange.Kraken)
        {
            GoToKraken(sim);
        }
        else
        {
            sim.Run();
        }

        var socket = sim.Transport.OpenSocket(exchange)!;
        var script = sim.Transport.Script(exchange);
        var price = 70000m;
        var step = 0;

        void FeedUntil(TimeSpan minutes)
        {
            var end = sim.Start + minutes;
            while (sim.Now < end)
            {
                sim.AdvanceTo(sim.Now + Tick);
                price += 1;
                sim.Push(exchange, exchange == Exchange.Binance ? BinanceKline(price) : KrakenUpdate(price));
                step++;
                Assert.True(sim.Client.State == FeedState.Live, $"step {step} at {sim.Now:T}: {sim.Client.State} (a false Stale)");
                Assert.True(socket.IsOpen, $"step {step} at {sim.Now:T}: the healthy socket was dropped");
                Assert.Equal(price, sim.Client.Candles.Snapshot()[^1].Close); // the forming candle follows the stream
            }
        }

        FeedUntil(TimeSpan.FromMinutes(25));                       // samples at 0, 10, 20 min: offset 0
        AssertNear(TimeSpan.Zero, sim.Client.ExchangeNow - sim.Now, TimeSpan.FromSeconds(1), "before the steps");

        script.TimeSkew = Skew;
        FeedUntil(TimeSpan.FromMinutes(55));                       // samples at 30, 40, 50 min: median 90 s
        AssertNear(Skew, sim.Client.ExchangeNow - sim.Now, TimeSpan.FromSeconds(1), "after the +90 s step");

        script.TimeSkew = -Skew;
        FeedUntil(TimeSpan.FromMinutes(85));                       // samples at 60, 70, 80 min: median -90 s
        AssertNear(-Skew, sim.Client.ExchangeNow - sim.Now, TimeSpan.FromSeconds(1), "after the -90 s step");

        Assert.Single(sim.Transport.Sockets, s => s.Exchange == exchange);       // never reconnected
        Assert.Single(Rest(sim.Transport, exchange, exchange == Exchange.Binance ? "klines" : "ohlc")); // never reloaded
    }

    // ---- criterion 2: a time request never harms the feed ------------------------------------------------------------

    public static TheoryData<string> Failures =>
        ["exception", "500", "429 Retry-After 10 min", "451", "200 not json"];

    [Theory]
    [MemberData(nameof(Failures))]
    public void A_failing_Binance_time_request_changes_nothing_else(string failure)
    {
        using var rig = new Rig(failure);
        rig.Run();
        rig.AdvanceFeeding(rig.Start + TimeSpan.FromMinutes(12), Fixtures.BinanceMiniTicker);

        var timeCalls = rig.TimeCalls.Count;
        Assert.True(timeCalls >= 2, $"{timeCalls} time requests in 12 min: a failed one must not stop asking");
        Assert.Equal(FeedState.Live, rig.Client.State);
        Assert.Equal(MarketSource.Binance, rig.Client.Source);
        Assert.Equal(FeedMode.WebSocket, rig.Client.Mode);
        Assert.Single(rig.Inner.Sockets);                         // the feed came up and stayed up
        Assert.Single(rig.Inner.CallsTo(Exchange.Binance), c => !c.IsSocket && PathHas(c, "klines")); // no reload
        Assert.Empty(rig.Inner.CallsTo(Exchange.Kraken));         // no 451 switch
        Assert.False(rig.Client.Clock.HasOffset);
        Assert.Equal(rig.Now, rig.Client.ExchangeNow);

        // A Retry-After of the time answer is not a notBefore: the reconnect after a drop is the usual 1 s backoff.
        rig.Inner.OpenSocket(Exchange.Binance)!.ServerClose();
        rig.Drain();
        rig.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(2, rig.Inner.Sockets.Count);
        Assert.Equal(2, rig.Inner.CallsTo(Exchange.Binance).Count(c => !c.IsSocket && PathHas(c, "klines")));
        Assert.Equal(MarketSource.Binance, rig.Client.Source);
        Assert.Equal(FeedState.Live, rig.Client.State);
    }

    [Fact]
    public void A_429_on_the_time_request_does_not_delay_REST_polling()
    {
        using var rig = new Rig("429 Retry-After 10 min");
        rig.Inner.Binance.SocketDown = true;       // REST up, socket refused: 3 cycles, then REST polling every 15 s
        rig.Run();
        rig.Advance(TimeSpan.FromSeconds(3 * 60));

        Assert.Equal(FeedMode.RestPoll, rig.Client.Mode);
        var polls = rig.Inner.CallsTo(Exchange.Binance).Where(c => !c.IsSocket && PathHas(c, "klines")).Select(c => c.At).ToList();
        var lastPolls = polls.Where(t => t > rig.Start + TimeSpan.FromSeconds(60)).ToList();
        Assert.True(lastPolls.Count >= 5, $"only {lastPolls.Count} polls in 2 min: the 429 of /time held REST back");
    }

    /// <summary>Sim's twin with a transport that answers /time with a scripted failure.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly SynchronizationContext? previous;
        private readonly CancellationTokenSource cts = new();
        private readonly DeterministicContext context = new();
        private readonly FakeTimeProvider time = new(Fixtures.RecordedAt);
        private Task? run;

        public Rig(string failure)
        {
            previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
            Inner = new FakeTransport(time);
            var transport = new FailingTime(Inner, failure, TimeCalls);
            Client = new MarketDataClient(transport, time, () => 0.5);
        }

        public FakeTransport Inner { get; }

        public List<Uri> TimeCalls { get; } = [];

        public MarketDataClient Client { get; }

        public DateTimeOffset Start { get; private set; }

        public DateTimeOffset Now => time.GetUtcNow();

        public void Run()
        {
            Start = Now;
            run = Client.RunAsync(cts.Token);
            Drain();
        }

        public void Drain()
        {
            context.RunUntilIdle();
            if (run is { IsFaulted: true })
            {
                ExceptionDispatchInfo.Capture(run.Exception!.GetBaseException()).Throw();
            }
        }

        public void Advance(TimeSpan by) => AdvanceTo(Now + by);

        public void AdvanceTo(DateTimeOffset target)
        {
            Drain();
            while (time.NextDue(target) is { } timer)
            {
                time.SetNow(timer.Due!.Value);
                timer.Fire();
                Drain();
            }

            time.SetNow(target);
            Drain();
        }

        public void AdvanceFeeding(DateTimeOffset target, string message)
        {
            while (Now < target)
            {
                AdvanceTo(Now + Tick < target ? Now + Tick : target);
                if (Inner.OpenSocket(Exchange.Binance) is { } socket)
                {
                    socket.Push(message);
                    Drain();
                }
            }
        }

        public void Dispose()
        {
            try
            {
                cts.Cancel();
                context.RunUntilIdle();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                cts.Dispose();
            }
        }
    }

    private sealed class FailingTime(FakeTransport inner, string failure, List<Uri> timeCalls) : IMarketTransport
    {
        public Task<HttpResult> GetAsync(Uri url, CancellationToken cancellationToken)
        {
            if (!url.AbsolutePath.EndsWith("/time", StringComparison.OrdinalIgnoreCase))
            {
                return inner.GetAsync(url, cancellationToken);
            }

            timeCalls.Add(url);
            return failure switch
            {
                "exception" => Task.FromException<HttpResult>(new HttpRequestException("fake: time request failed")),
                "500" => Task.FromResult(new HttpResult(500, "oops")),
                "451" => Task.FromResult(new HttpResult(451, "{\"code\":0,\"msg\":\"unavailable for legal reasons\"}")),
                "200 not json" => Task.FromResult(new HttpResult(200, "<html>not json</html>")),
                _ => Task.FromResult(new HttpResult(429, "{\"code\":-1003,\"msg\":\"Too many requests.\"}", TimeSpan.FromMinutes(10))),
            };
        }

        public Task<IMarketSocket> ConnectAsync(Uri url, CancellationToken cancellationToken) =>
            inner.ConnectAsync(url, cancellationToken);
    }
}
