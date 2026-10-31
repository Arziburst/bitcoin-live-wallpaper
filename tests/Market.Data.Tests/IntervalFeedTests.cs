// Contract #18, criterion 2: the market data client takes the candle interval as a parameter - Binance REST
// klines interval=<code> and WS btcusdt@kline_<code> (4h, 6h, ...), Kraken OHLC interval in minutes (240 is
// native; 360 is not offered -> 60-minute candles aggregated to 6h) - keeps 30 days of history (Binance limit >=
// the 30-day count), and never mixes intervals in one buffer: an interval change clears and reloads, a stream
// message of another interval is never applied. The existing resilience behaviour is unchanged (the existing
// Market.Data tests stay green, unchanged, on the 3-argument constructor).
//
// API SKETCH (written before the code, #237), in src/Market.Data/MarketDataClient.cs:
//
//   public sealed class MarketDataClient
//   {
//       // Existing constructor unchanged: the interval the existing tests run on, 5 minutes.
//       public MarketDataClient(IMarketTransport transport, TimeProvider time, Func<double> jitter);
//
//       // interval: 5 min or one of IntervalPolicy.Ladder (1h, 2h, 4h, 6h, 8h, 12h, 1d); anything else
//       // ArgumentOutOfRangeException.
//       public MarketDataClient(IMarketTransport transport, TimeProvider time, Func<double> jitter, TimeSpan interval);
//
//       // The interval of the candles in Candles.
//       public TimeSpan Interval { get; }
//
//       // Same interval: nothing happens. Another supported interval: at once (before it returns) Interval is the
//       // new one and Candles holds no candle of the old interval (cleared, or already the new history); then a
//       // full reload with the new interval (REST history + a new socket subscribed
//       // to the new interval; the old socket is closed) within one connect cycle - 35 s on the fake clock at
//       // most. An unsupported interval: ArgumentOutOfRangeException, nothing changes. Called on the thread
//       // that runs RunAsync (the host's), or before RunAsync.
//       public void ChangeInterval(TimeSpan interval);
//   }
//
// Requests per interval (the tests read them from the fake transport):
//   Binance REST  .../api/v3/klines?symbol=BTCUSDT&interval=<code>&limit=<n>, ceil(30 d / interval) <= n <= 1000;
//                 D-29 (owner 2026-10-31): the chart fills all drawable slots with older history, so n is also >=
//                 the CandleSlots of every screen whose IntervalPolicy.ForScreen interval is this one (1000, the
//                 Binance maximum, satisfies it for every screen tested);
//                 polls (REST fallback) .../klines?...&interval=<code>&limit=2.
//   Kraken        serves at most the newest 720 rows: 4h (native 240) -> 720 candles (120 days, fills any slots);
//                 6h (from 60) -> 720 hours -> 120 6h candles (119 when the oldest bucket is partial), i.e. 30
//                 days: fewer than the TV's ~133 drawable slots, so on Kraken the TV chart does not quite fill.
//   Binance WS    SUBSCRIBE params "btcusdt@kline_<code>" and "btcusdt@miniTicker".
//   Kraken REST   .../0/public/OHLC?pair=XBTUSD&interval=<m> (also with &since= for polls), Kraken WS v2
//                 subscribe {"channel":"ohlc", ..., "interval": <m>}, where m is a Kraken interval (1, 5, 15, 30,
//                 60, 240, 1440, ...) that divides the candle interval and gives >= 30 days in Kraken's 720 rows:
//                 the interval itself when Kraken offers it (60, 240, 1440), else 60 or 240 (6h -> 60, 2h -> 60),
//                 aggregated with CandleAggregator (CandleAggregatorTests).
//   Stream        a Binance kline whose "i" is not the client's code, and a Kraken ohlc item whose "interval"
//                 is not m, is ignored (no candle of another interval ever enters Candles).

using System.Text.Json;
using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Chart.Core.Layout;
using BitcoinWallpaper.Market.Data.Tests.Fakes;

namespace BitcoinWallpaper.Market.Data.Tests;

public class IntervalFeedTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan FourHours = TimeSpan.FromHours(4);
    private static readonly TimeSpan SixHours = TimeSpan.FromHours(6);

    public static TheoryData<double> LadderHours => new() { 1, 2, 4, 6, 8, 12, 24 };

    private static TimeSpan Hours(double h) => TimeSpan.FromHours(h);

    private static int Limit(TransportCall call) => int.Parse(IntervalTransport.Query(call.Url, "limit")!, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>What the client subscribed to on the socket: Binance params entries, Kraken "interval=<m>".</summary>
    private static List<string> SentParams(FakeSocket socket)
    {
        var result = new List<string>();
        foreach (var text in socket.Sent)
        {
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("params", out var p))
            {
                continue;
            }

            if (p.ValueKind == JsonValueKind.Array)
            {
                result.AddRange(p.EnumerateArray().Select(e => e.GetString() ?? ""));
            }
            else if (p.TryGetProperty("interval", out var i))
            {
                result.Add("interval=" + i.GetRawText());
            }
        }

        return result;
    }

    /// <summary>The newest `limit` Binance candles of the interval as the client must hold them.</summary>
    private static List<Candle> ExpectedBinance(TimeSpan interval, DateTimeOffset now, int limit) =>
        IntervalData.OpenTimes(interval, now, limit).Select(t => IntervalData.BinanceCandle(t, interval, now)).ToList();

    private static void AssertOneIntervalSeries(IReadOnlyList<Candle> candles, TimeSpan interval)
    {
        for (var i = 0; i < candles.Count; i++)
        {
            Assert.True(IntervalData.Aligned(candles[i].OpenTime, interval),
                $"candle {i} opens at {candles[i].OpenTime:O}, not on a {interval} boundary");
            if (i > 0)
            {
                Assert.True(candles[i].OpenTime > candles[i - 1].OpenTime, $"candles {i - 1} and {i} out of order");
            }
        }
    }

    // ---- constructor and interval ---------------------------------------------------------------

    [Fact]
    public void The_existing_constructor_keeps_5_minute_candles()
    {
        var time = new FakeTimeProvider(Fixtures.RecordedAt);
        var client = new MarketDataClient(new IntervalTransport(time), time, () => 0.5);
        Assert.Equal(TimeSpan.FromMinutes(5), client.Interval);
    }

    [Theory]
    [MemberData(nameof(LadderHours))]
    public void The_interval_is_a_constructor_parameter(double hours)
    {
        var time = new FakeTimeProvider(Fixtures.RecordedAt);
        var client = new MarketDataClient(new IntervalTransport(time), time, () => 0.5, Hours(hours));
        Assert.Equal(Hours(hours), client.Interval);
    }

    [Theory]
    [InlineData(7)]      // 7 minutes
    [InlineData(180)]    // 3 hours: no Binance code
    [InlineData(0)]
    [InlineData(-60)]
    public void An_unsupported_interval_is_refused(int minutes)
    {
        var time = new FakeTimeProvider(Fixtures.RecordedAt);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MarketDataClient(new IntervalTransport(time), time, () => 0.5, TimeSpan.FromMinutes(minutes)));

        var client = new MarketDataClient(new IntervalTransport(time), time, () => 0.5, FourHours);
        Assert.Throws<ArgumentOutOfRangeException>(() => client.ChangeInterval(TimeSpan.FromMinutes(minutes)));
        Assert.Equal(FourHours, client.Interval);
    }

    // ---- Binance --------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(LadderHours))]
    public void Binance_history_and_stream_use_the_interval_code_and_keep_30_days(double hours)
    {
        var interval = Hours(hours);
        var code = IntervalData.BinanceCode(interval);
        using var sim = new IntervalSim(interval);
        sim.Run();
        sim.Advance(TimeSpan.FromSeconds(1));

        var history = sim.Transport.Rest(Exchange.Binance, "klines");
        Assert.NotEmpty(history);
        Assert.All(history, c => Assert.Equal(code, IntervalTransport.Query(c.Url, "interval")));
        var limit = Limit(history[0]);
        Assert.InRange(limit, IntervalData.ThirtyDayCount(interval), 1000);

        var socket = sim.Transport.OpenSocket(Exchange.Binance);
        Assert.NotNull(socket);
        var subscribed = SentParams(socket);
        Assert.Contains("btcusdt@kline_" + code, subscribed);
        Assert.Contains("btcusdt@miniTicker", subscribed);
        Assert.DoesNotContain("btcusdt@kline_5m", subscribed);

        Assert.Equal(MarketSource.Binance, sim.Client.Candles.Source);
        Assert.Equal(ExpectedBinance(interval, sim.Now, limit), sim.Client.Candles.Snapshot());
    }

    public static TheoryData<int, int, double> Screens => new()
    {
        { 5120, 1440, 1.75 }, // owner's desk: 4h, ~297 slots (D-29: ~48 days)
        { 3840, 2160, 1.0 },  // owner's TV: 6h, ~141 slots
        { 3840, 2160, 1.5 },
        { 3440, 1440, 1.0 },
        { 2560, 1440, 1.0 },
        { 1920, 1080, 1.0 },
        { 1080, 1920, 1.0 },
        { 7680, 4320, 1.0 },
    };

    // D-29 (owner 2026-10-31): the chart fills all drawable slots, older history to the left of the 30 days.
    [Theory]
    [MemberData(nameof(Screens))]
    public void Binance_history_is_long_enough_to_fill_the_drawable_slots_of_the_screen(int w, int h, double s)
    {
        var interval = IntervalPolicy.ForScreen(w, h, s, IntervalPolicy.DefaultPeriod);
        var slots = LayoutEngine.Compute(w, h, s, 0).CandleSlots;
        using var sim = new IntervalSim(interval);
        sim.Run();
        sim.Advance(TimeSpan.FromSeconds(1));

        var history = sim.Transport.Rest(Exchange.Binance, "klines");
        Assert.NotEmpty(history);
        Assert.InRange(Limit(history[0]), slots, 1000);
        Assert.True(sim.Client.Candles.Count >= slots,
            $"{w}x{h}@{s}: {sim.Client.Candles.Count} candles of {interval} held, the screen has {slots} slots");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public void Binance_rest_polls_use_the_interval_code(double hours)
    {
        var interval = Hours(hours);
        using var sim = new IntervalSim(interval);
        sim.Transport.BinanceSocketDown = true;
        sim.Run();
        sim.Advance(TimeSpan.FromSeconds(90));

        var polls = sim.Transport.Rest(Exchange.Binance, "klines").Where(c => IntervalTransport.Query(c.Url, "limit") == "2").ToList();
        Assert.True(polls.Count > 0, "no Binance REST poll (klines limit=2) with the socket refused");
        Assert.All(polls, c => Assert.Equal(IntervalData.BinanceCode(interval), IntervalTransport.Query(c.Url, "interval")));
        AssertOneIntervalSeries(sim.Client.Candles.Snapshot(), interval);
    }

    [Fact]
    public void Binance_kline_of_the_interval_updates_the_forming_candle()
    {
        // 02:05 UTC: the forming 4h candle opened at 00:00.
        using var sim = new IntervalSim(FourHours);
        sim.Run();
        var forming = Fixtures.Utc("2026-10-31T00:00:00Z");
        Assert.Equal(forming, sim.Client.Candles.Snapshot()[^1].OpenTime);
        var count = sim.Client.Candles.Count;

        sim.Push(Exchange.Binance, IntervalData.BinanceKlineMessage("4h", forming, FourHours, 70100m, 70900m, 69950m, 70777.7m, 1234.5m, closed: false));

        var candles = sim.Client.Candles.Snapshot();
        Assert.Equal(count, candles.Count);
        Assert.Equal(new Candle(forming, 70100m, 70900m, 69950m, 70777.7m, 1234.5m, false), candles[^1]);
    }

    [Fact]
    public void Binance_klines_of_another_interval_never_enter_the_buffer()
    {
        using var sim = new IntervalSim(FourHours);
        sim.Run();
        var before = sim.Client.Candles.Snapshot();
        var forming = Fixtures.Utc("2026-10-31T00:00:00Z");

        // A 5m kline at 02:05 (the recorded fixture, "i":"5m") would append a candle; a 6h kline at 00:00 would
        // overwrite the forming 4h candle with 6h values; a 1h kline at 01:00 would fill a "gap".
        sim.Push(Exchange.Binance, Fixtures.BinanceKlineForming);
        sim.Push(Exchange.Binance, IntervalData.BinanceKlineMessage("6h", forming, SixHours, 1m, 2m, 0.5m, 1.5m, 9m, closed: false));
        sim.Push(Exchange.Binance, IntervalData.BinanceKlineMessage("1h", forming.AddHours(1), Hour, 1m, 2m, 0.5m, 1.5m, 9m, closed: true));

        Assert.Equal(before, sim.Client.Candles.Snapshot());
    }

    // ---- Kraken ---------------------------------------------------------------------------------

    private static int KrakenMinutes(IntervalSim sim)
    {
        var ohlc = sim.Transport.Rest(Exchange.Kraken, "ohlc");
        Assert.NotEmpty(ohlc);
        var minutes = ohlc.Select(c => IntervalTransport.Query(c.Url, "interval")).Distinct().ToList();
        return int.Parse(Assert.Single(minutes)!, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IntervalSim OnKraken(TimeSpan interval, DateTimeOffset? start = null)
    {
        var sim = new IntervalSim(interval, start);
        sim.Transport.BinanceRestStatus = 451; // blocked at start: Kraken at once
        sim.Run();
        sim.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(MarketSource.Kraken, sim.Client.Candles.Source);
        return sim;
    }

    [Theory]
    [MemberData(nameof(LadderHours))]
    public void Kraken_uses_an_offered_interval_that_divides_the_candle_interval(double hours)
    {
        var interval = Hours(hours);
        using var sim = OnKraken(interval);

        var m = KrakenMinutes(sim);
        Assert.Contains(m, new[] { 60, 240, 1440 });
        Assert.True(interval.TotalMinutes % m == 0, $"Kraken interval {m} min does not divide {interval}");
        if (IntervalData.KrakenNativeMinutes.Contains((int)interval.TotalMinutes))
        {
            Assert.Equal((int)interval.TotalMinutes, m); // offered by Kraken: used as is (240 for 4h)
        }

        var socket = sim.Transport.OpenSocket(Exchange.Kraken);
        Assert.NotNull(socket);
        Assert.Contains("interval=" + m, SentParams(socket));

        var candles = sim.Client.Candles.Snapshot();
        Assert.NotEmpty(candles);
        AssertOneIntervalSeries(candles, interval);
        Assert.Equal(IntervalData.Floor(sim.Now, interval), candles[^1].OpenTime);
    }

    [Fact]
    public void Kraken_4h_is_native_240()
    {
        using var sim = OnKraken(FourHours);
        Assert.Equal(240, KrakenMinutes(sim));
        var expected = IntervalData.OpenTimes(FourHours, sim.Now, 720).Select(t => IntervalData.Ohlcv(IntervalData.Source(t, FourHours)));
        Assert.Equal(expected, sim.Client.Candles.Snapshot().Select(IntervalData.Ohlcv));
    }

    [Fact]
    public void Kraken_6h_history_is_aggregated_from_60_minute_candles()
    {
        var start = Fixtures.Utc("2026-10-31T17:20:00Z");
        using var sim = OnKraken(SixHours, start);

        Assert.Equal(60, KrakenMinutes(sim));
        var hours = IntervalData.OpenTimes(Hour, sim.Now, 720).Select(t => IntervalData.Source(t, Hour)).ToList();
        var expected = IntervalData.Aggregate(hours, SixHours, sim.Now);
        Assert.Equal(expected, sim.Client.Candles.Snapshot());
        Assert.Equal(120, expected.Count);
    }

    [Fact]
    public void Kraken_6h_forming_candle_follows_the_60_minute_stream()
    {
        var start = Fixtures.Utc("2026-10-31T17:20:00Z");
        using var sim = OnKraken(SixHours, start);
        var hours = IntervalData.OpenTimes(Hour, start, 720).Select(t => IntervalData.Source(t, Hour)).ToList();
        var noon = Fixtures.Utc("2026-10-31T12:00:00Z");
        var five = Fixtures.Utc("2026-10-31T17:00:00Z");

        // The forming hour 17:00 trades higher: the 6h candle of 12:00 takes its high, close and volume.
        sim.Push(Exchange.Kraken, IntervalData.KrakenWsOhlc(60, five, hours[^1].Open, 79999.9m, hours[^1].Low, 79888.8m, 42.5m, sim.Now));

        hours[^1] = hours[^1] with { High = 79999.9m, Close = 79888.8m, Volume = 42.5m, IsClosed = false };
        var expected = IntervalData.Aggregate(hours, SixHours, sim.Now);
        var candles = sim.Client.Candles.Snapshot();
        Assert.Equal(expected, candles);
        Assert.Equal(new Candle(
            noon,
            hours[^6].Open,
            79999.9m,
            hours.Skip(714).Min(c => c.Low),
            79888.8m,
            hours.Skip(714).Take(5).Sum(c => c.Volume) + 42.5m,
            IsClosed: false), candles[^1]);

        // The next hour starts the 18:00 candle; the 12:00 one keeps its six hours.
        sim.AdvanceFeeding(Exchange.Kraken, Fixtures.Utc("2026-10-31T18:00:05Z"), TimeSpan.FromSeconds(20), IntervalData.KrakenHeartbeat);
        var six = Fixtures.Utc("2026-10-31T18:00:00Z");
        sim.Push(Exchange.Kraken, IntervalData.KrakenWsOhlc(60, six, 79888.8m, 79900m, 79800m, 79850m, 0.5m, sim.Now));

        candles = sim.Client.Candles.Snapshot();
        AssertOneIntervalSeries(candles, SixHours);
        Assert.Equal(new Candle(six, 79888.8m, 79900m, 79800m, 79850m, 0.5m, IsClosed: false), candles[^1]);
        Assert.Equal(expected[^1] with { IsClosed = candles[^2].IsClosed }, candles[^2]);
    }

    [Fact]
    public void Kraken_ohlc_of_another_interval_never_enters_the_buffer()
    {
        using var sim = OnKraken(FourHours);
        var before = sim.Client.Candles.Snapshot();

        // The recorded 5-minute update (02:05) and a 60-minute one would add candles of another interval.
        sim.Push(Exchange.Kraken, Fixtures.KrakenWsUpdate);
        sim.Push(Exchange.Kraken, IntervalData.KrakenWsOhlc(60, Fixtures.Utc("2026-10-31T01:00:00Z"), 1m, 2m, 0.5m, 1.5m, 9m, sim.Now));

        Assert.Equal(before, sim.Client.Candles.Snapshot());
    }

    // ---- interval change: clear and reload ------------------------------------------------------

    /// <summary>The buffer holds 6h Binance candles only (or nothing): no candle of the 4h series.</summary>
    private static void AssertOnlyBinance6h(IntervalSim sim)
    {
        var seen = sim.Client.Candles.Snapshot();
        AssertOneIntervalSeries(seen, SixHours);
        Assert.All(seen, c => Assert.Equal(IntervalData.BinanceCandle(c.OpenTime, SixHours, sim.Now) with { IsClosed = c.IsClosed }, c));
    }

    /// <summary>The buffer holds 6h candles aggregated from Kraken hours only (or nothing).</summary>
    private static void AssertOnlyKraken6h(IntervalSim sim)
    {
        var hours = IntervalData.OpenTimes(Hour, sim.Now, 720).Select(t => IntervalData.Source(t, Hour)).ToList();
        var expected = IntervalData.Aggregate(hours, SixHours, sim.Now).ToDictionary(c => c.OpenTime);
        foreach (var c in sim.Client.Candles.Snapshot())
        {
            Assert.True(expected.TryGetValue(c.OpenTime, out var e), $"candle at {c.OpenTime:O} is not a 6h candle of the Kraken hours");
            Assert.Equal(e with { IsClosed = c.IsClosed }, c);
        }
    }

    [Fact]
    public void An_interval_change_clears_at_once_and_reloads_the_new_interval()
    {
        using var sim = new IntervalSim(FourHours);
        sim.Run();
        sim.Advance(TimeSpan.FromSeconds(5));
        var oldSocket = sim.Transport.OpenSocket(Exchange.Binance);
        Assert.NotNull(oldSocket);
        var requestsBefore = sim.Transport.Rest(Exchange.Binance, "klines").Count;

        sim.Client.ChangeInterval(SixHours);

        Assert.Equal(SixHours, sim.Client.Interval);
        AssertOnlyBinance6h(sim);

        // Within one connect cycle the 6h history is there; no 4h candle is ever seen meanwhile.
        var deadline = sim.Now + TimeSpan.FromSeconds(35);
        while (sim.Now < deadline)
        {
            sim.Advance(TimeSpan.FromMilliseconds(250));
            AssertOnlyBinance6h(sim);
        }

        var reload = sim.Transport.Rest(Exchange.Binance, "klines").Skip(requestsBefore).ToList();
        Assert.NotEmpty(reload);
        Assert.All(reload, c => Assert.Equal("6h", IntervalTransport.Query(c.Url, "interval")));
        var limit = Limit(reload[0]);
        Assert.InRange(limit, 120, 1000);
        Assert.Equal(ExpectedBinance(SixHours, sim.Now, limit), sim.Client.Candles.Snapshot());

        Assert.False(oldSocket.IsOpen, "the 4h socket is still open after the change");
        var socket = sim.Transport.OpenSocket(Exchange.Binance);
        Assert.NotNull(socket);
        Assert.Contains("btcusdt@kline_6h", SentParams(socket));
        Assert.DoesNotContain("btcusdt@kline_4h", SentParams(socket));

        // On the new socket: a late 4h kline is ignored, a 6h kline updates the forming candle.
        var forming = Fixtures.Utc("2026-10-31T00:00:00Z");
        var held = sim.Client.Candles.Snapshot();
        sim.Push(Exchange.Binance, IntervalData.BinanceKlineMessage("4h", forming, FourHours, 1m, 2m, 0.5m, 1.5m, 9m, closed: false));
        Assert.Equal(held, sim.Client.Candles.Snapshot());
        sim.Push(Exchange.Binance, IntervalData.BinanceKlineMessage("6h", forming, SixHours, 70100m, 70900m, 69950m, 70777.7m, 1234.5m, closed: false));
        Assert.Equal(new Candle(forming, 70100m, 70900m, 69950m, 70777.7m, 1234.5m, false), sim.Client.Candles.Snapshot()[^1]);
    }

    [Fact]
    public void An_interval_change_on_Kraken_reloads_the_aggregated_series()
    {
        var start = Fixtures.Utc("2026-10-31T17:20:00Z");
        using var sim = OnKraken(FourHours, start);
        Assert.Equal(240, KrakenMinutes(sim));
        var requestsBefore = sim.Transport.Rest(Exchange.Kraken, "ohlc").Count;

        sim.Client.ChangeInterval(SixHours);
        Assert.Equal(SixHours, sim.Client.Interval);
        AssertOnlyKraken6h(sim);
        sim.Advance(TimeSpan.FromSeconds(35));

        var reload = sim.Transport.Rest(Exchange.Kraken, "ohlc").Skip(requestsBefore).ToList();
        Assert.NotEmpty(reload);
        Assert.All(reload, c => Assert.Equal("60", IntervalTransport.Query(c.Url, "interval")));
        var hours = IntervalData.OpenTimes(Hour, sim.Now, 720).Select(t => IntervalData.Source(t, Hour)).ToList();
        Assert.Equal(IntervalData.Aggregate(hours, SixHours, sim.Now), sim.Client.Candles.Snapshot());
        Assert.Equal(MarketSource.Kraken, sim.Client.Candles.Source);
        var socket = sim.Transport.OpenSocket(Exchange.Kraken);
        Assert.NotNull(socket);
        Assert.Contains("interval=60", SentParams(socket));
    }

    [Fact]
    public void Changing_to_the_same_interval_changes_nothing()
    {
        using var sim = new IntervalSim(FourHours);
        sim.Run();
        sim.Advance(TimeSpan.FromSeconds(5));
        var before = sim.Client.Candles.Snapshot();
        var calls = sim.Transport.Calls.Count;
        var socket = sim.Transport.OpenSocket(Exchange.Binance);

        sim.Client.ChangeInterval(FourHours);
        sim.Advance(TimeSpan.FromSeconds(35));

        Assert.Equal(before, sim.Client.Candles.Snapshot());
        Assert.Equal(calls, sim.Transport.Calls.Count);
        Assert.Same(socket, sim.Transport.OpenSocket(Exchange.Binance));
    }
}
