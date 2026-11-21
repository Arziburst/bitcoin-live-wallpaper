using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Market.Data.Tests.Fakes;

namespace BitcoinWallpaper.Market.Data.Tests;

// Contract #32 (D-27), criteria 3-5: the filter inside the client (fake transport and clock) and the Kraken 24 h ticker.
//
// API SKETCH (#237), src/Market.Data/MarketDataClient.cs (TickFilter itself: TickFilterTests.cs):
//   public event Action<string>? TickDropped;  // a dropped candle/ticker, with TickFilter's reason; the CLIENT rate-limits
//       // it with its TimeProvider: the same reason is not raised again within 60 s of the raised one; the first always is.
// Every candle and ticker of every path passes TickFilter before the buffer; each Commit resets the candle reference to
// the committed series' last close; a history with no accepted candle is a failed cycle. On Kraken with a chart interval
// over 60 min the ticker (and the poll's derived open) comes from a separate REST OHLC interval=60 request (history load
// and each poll; no "since", or one at least 24 h back), never mixed into Candles.
public class TickFilterFeedTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly TimeSpan Five = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan FourHours = TimeSpan.FromHours(4);
    private static readonly TimeSpan SixHours = TimeSpan.FromHours(6);

    // ---- harness -----------------------------------------------------------------------------------

    /// <summary>The interval transport, with the bodies of 200 answers rewritable per request (null = as is).</summary>
    private sealed class TamperTransport(IntervalTransport inner) : IMarketTransport
    {
        public Func<Uri, string, string?>? Rewrite { get; set; }

        public async Task<HttpResult> GetAsync(Uri url, CancellationToken cancellationToken)
        {
            var result = await inner.GetAsync(url, cancellationToken);
            return result.StatusCode == 200 && Rewrite?.Invoke(url, result.Body) is { } body ? result with { Body = body } : result;
        }

        public Task<IMarketSocket> ConnectAsync(Uri url, CancellationToken cancellationToken) => inner.ConnectAsync(url, cancellationToken);
    }

    private sealed class FSim : IDisposable
    {
        private readonly SynchronizationContext? previous = SynchronizationContext.Current;
        private readonly CancellationTokenSource cts = new();
        private readonly TamperTransport net;
        private Task? run;

        private readonly bool kraken;

        public FSim(TimeSpan interval, bool kraken = false, bool socketDown = false, DateTimeOffset? start = null)
        {
            this.kraken = kraken;
            Context = new DeterministicContext();
            SynchronizationContext.SetSynchronizationContext(Context);
            Time = new FakeTimeProvider(start ?? Fixtures.RecordedAt);
            Inner = new IntervalTransport(Time);
            net = new TamperTransport(Inner);
            Client = new MarketDataClient(net, Time, () => 0.5, interval);
            Client.TickDropped += reason => Drops.Add((reason, Time.GetUtcNow()));
            Inner.BinanceRestStatus = kraken ? 451 : null; // blocked at start: Kraken at once
            Inner.BinanceSocketDown = !kraken && socketDown;
            Inner.KrakenSocketDown = kraken && socketDown;
        }

        /// <summary>Runs the client (after the test set its Rewrite); on Kraken waits for the first history.</summary>
        public FSim Start()
        {
            run = Client.RunAsync(cts.Token);
            Drain();
            if (kraken)
            {
                Advance(TimeSpan.FromSeconds(10));
                Assert.Equal(MarketSource.Kraken, Client.Candles.Source);
            }

            return this;
        }

        public FakeTimeProvider Time { get; }

        public IntervalTransport Inner { get; }

        public DeterministicContext Context { get; }

        public MarketDataClient Client { get; }

        public List<(string Reason, DateTimeOffset At)> Drops { get; } = [];

        public DateTimeOffset Now => Time.GetUtcNow();

        public Func<Uri, string, string?>? Rewrite { get => net.Rewrite; set => net.Rewrite = value; }

        public IReadOnlyList<Candle> Held => Client.Candles.Snapshot();

        public void Drain()
        {
            Context.RunUntilIdle();
            if (run is { IsFaulted: true })
            {
                ExceptionDispatchInfo.Capture(run.Exception!.GetBaseException()).Throw();
            }
        }

        public void Advance(TimeSpan by)
        {
            var target = Now + by;
            Drain();
            while (Time.NextDue(target) is { } timer)
            {
                Time.SetNow(timer.Due!.Value);
                timer.Fire();
                Drain();
            }

            Time.SetNow(target);
            Drain();
        }

        public void Push(Exchange exchange, string message)
        {
            (Inner.OpenSocket(exchange) ?? throw new Xunit.Sdk.XunitException($"no open {exchange} socket at {Now:O}")).Push(message);
            Drain();
        }

        /// <summary>Waits (1 s steps) until the client polls REST.</summary>
        public void WaitForPolling()
        {
            for (var i = 0; i < 40 && Client.Mode != FeedMode.RestPoll; i++)
            {
                Advance(TimeSpan.FromSeconds(1));
            }

            Assert.Equal(FeedMode.RestPoll, Client.Mode);
        }

        public void Dispose()
        {
            try
            {
                cts.Cancel();
                Context.RunUntilIdle();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                cts.Dispose();
            }
        }
    }

    // ---- data helpers --------------------------------------------------------------------------------

    private static bool IsBinanceKlines(Uri u) => u.AbsolutePath.Contains("klines", StringComparison.Ordinal);

    private static bool IsKrakenOhlc(Uri u) => u.AbsolutePath.Contains("OHLC", StringComparison.OrdinalIgnoreCase);

    private static bool IsTicker(Uri u) => u.AbsolutePath.Contains("ticker", StringComparison.OrdinalIgnoreCase);

    private static string Rows(string json, bool kraken, Action<JsonArray> edit)
    {
        var root = JsonNode.Parse(json)!;
        edit(kraken ? root["result"]!["XXBTZUSD"]!.AsArray() : root.AsArray());
        return root.ToJsonString();
    }

    private static void Put(JsonArray rows, int i, params (int Col, string Value)[] cells)
    {
        foreach (var (col, value) in cells)
        {
            rows[i]![col] = value;
        }
    }

    /// <summary>A close and high far above the neighbours (a real shape: a jump, not an impossible one).</summary>
    private static void Jump(JsonArray rows, int i) => Put(rows, i, (2, "200000"), (4, "200000"));

    private static void Scale(JsonArray rows, decimal factor)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            for (var col = 1; col <= 4; col++)
            {
                rows[i]![col] = (decimal.Parse(rows[i]![col]!.GetValue<string>(), Inv) * factor).ToString("F8", Inv);
            }
        }
    }

    private static string Kline(string code, TimeSpan interval, DateTimeOffset open, decimal c, decimal? o = null,
        decimal? h = null, decimal? l = null, decimal v = 2m) =>
        IntervalData.BinanceKlineMessage(code, open, interval, o ?? c, h ?? c + 1, l ?? c - 1, c, v, closed: false);

    private static string Mini(decimal c, decimal o, decimal h, decimal l) => string.Create(Inv,
        $$"""{"e":"24hrMiniTicker","E":1793412319002,"s":"BTCUSDT","c":"{{c:F8}}","o":"{{o:F8}}","h":"{{h:F8}}","l":"{{l:F8}}","v":"100.0","q":"1.0"}""");

    private static decimal CloseAt(DateTimeOffset now, TimeSpan interval) =>
        IntervalData.Values(IntervalData.Floor(now, interval), interval).Close;

    private static void AssertNoChange(FSim sim, Exchange exchange, params string[] messages)
    {
        var candles = sim.Held;
        var ticker = sim.Client.Ticker;
        foreach (var message in messages)
        {
            sim.Push(exchange, message);
            Assert.Equal(candles, sim.Held);
            Assert.Equal(ticker, sim.Client.Ticker);
        }
    }

    /// <summary>The 24 h ticker an oracle derives from the last 24 h of 60-minute candles.</summary>
    private static void AssertHourlyTicker(Ticker24h? actual, DateTimeOffset now, bool openOnly = false)
    {
        var hours = IntervalData.OpenTimes(Hour, now, 720).Select(t => IntervalData.Source(t, Hour)).ToList();
        var window = hours.Where(c => c.OpenTime > hours[^1].OpenTime - TimeSpan.FromHours(24)).ToList();
        Assert.NotNull(actual);
        Assert.Equal(window[0].Open, actual.Open);
        if (openOnly)
        {
            return;
        }

        Assert.Equal(window[^1].Close, actual.Last);
        Assert.Equal(window.Max(c => c.High), actual.High);
        Assert.Equal(window.Min(c => c.Low), actual.Low);
        Assert.Equal(window.Sum(c => c.Volume), actual.Volume);
        Assert.Equal((double)((window[^1].Close - window[0].Open) / window[0].Open * 100m), (double)actual.ChangePercent, 9);
    }


    private static FSim Go(TimeSpan interval, Func<Uri, string, string?>? rewrite = null, bool kraken = false,
        bool socketDown = false, DateTimeOffset? start = null) =>
        new FSim(interval, kraken, socketDown, start) { Rewrite = rewrite }.Start();

    private static Func<Uri, string, string?> BinanceRows(Action<JsonArray> edit) =>
        (u, b) => IsBinanceKlines(u) ? Rows(b, false, edit) : null;

    private static Func<Uri, string, string?> KrakenRows(Action<JsonArray> edit) =>
        (u, b) => IsKrakenOhlc(u) ? Rows(b, true, edit) : null;

    // ---- Binance -----------------------------------------------------------------------------------

    [Fact]
    public void Binance_history_drops_impossible_candles_and_keeps_every_other()
    {
        using var sim = Go(Five, BinanceRows(rows =>
        {
            Jump(rows, 100); // close +43 % from the one before
            Put(rows, 200, (2, "1")); // high below low
            Put(rows, 300, (1, "0")); // zero open
            Put(rows, 400, (5, "-1")); // negative volume
            Jump(rows, 500); // two spikes in a row: the first is not a reference for the second
            Jump(rows, 501);
            Put(rows, 600, (2, "210000")); // a huge wick with a normal close is real
        }));

        var bad = new[] { 100, 200, 300, 400, 500, 501 };
        var times = IntervalData.OpenTimes(Five, sim.Now, 1000);
        Assert.Equal(times.Where((_, i) => !bad.Contains(i)).ToList(), sim.Held.Select(c => c.OpenTime).ToList());
        var wick = sim.Held.Single(c => c.OpenTime == times[600]);
        Assert.Equal(210000m, wick.High);
        Assert.Equal(IntervalData.Values(times[600], Five).Close, wick.Close);

        Assert.True(sim.Drops.Count >= 2, "a jump and a structural drop are two reasons");
        Assert.Equal(sim.Drops.Count, sim.Drops.Select(d => d.Reason).Distinct().Count()); // one per reason per minute
        Assert.All(sim.Drops, d => Assert.False(string.IsNullOrWhiteSpace(d.Reason)));
    }

    [Fact]
    public void A_history_without_an_accepted_candle_is_a_failed_cycle_and_retried()
    {
        using var sim = Go(Five, BinanceRows(rows =>
        {
            for (var i = 0; i < rows.Count; i++)
            {
                Put(rows, i, (2, "1")); // high below low, every candle
            }
        }));

        sim.Advance(TimeSpan.FromSeconds(2));
        Assert.True(sim.Inner.Rest(Exchange.Binance, "klines").Count >= 2, "no retry after the failed cycle");
        Assert.Empty(sim.Held);
        Assert.Null(sim.Client.Candles.Source);
        Assert.Null(sim.Client.Ticker);
        Assert.Equal(FeedState.Connecting, sim.Client.State);

        sim.Rewrite = null; // the exchange recovers: the next cycle commits
        sim.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(1000, sim.Held.Count);
        Assert.NotNull(sim.Client.Ticker);
    }

    [Fact]
    public void A_later_history_without_an_accepted_candle_leaves_the_last_good_data()
    {
        using var sim = Go(Five);
        var candles = sim.Held;
        var ticker = sim.Client.Ticker;
        Assert.Equal(1000, candles.Count);
        var requests = sim.Inner.Rest(Exchange.Binance, "klines").Count;

        sim.Rewrite = BinanceRows(rows =>
        {
            for (var i = 0; i < rows.Count; i++)
            {
                Put(rows, i, (1, "0"));
            }
        });
        sim.Inner.OpenSocket(Exchange.Binance)!.ServerClose();
        sim.Drain();
        sim.Advance(TimeSpan.FromSeconds(2));

        Assert.True(sim.Inner.Rest(Exchange.Binance, "klines").Count > requests, "the failed cycle is not retried");
        Assert.Equal(candles, sim.Held);
        Assert.Equal(ticker, sim.Client.Ticker);
    }

    [Fact]
    public void Binance_ws_kline_drops_impossible_candles_and_the_reference_is_the_last_accepted_close()
    {
        using var sim = Go(Five);
        var open = IntervalData.Floor(sim.Now, Five);
        var last = CloseAt(sim.Now, Five);
        Assert.Equal(last, sim.Held[^1].Close);

        AssertNoChange(sim, Exchange.Binance,
            Kline("5m", Five, open, last, o: 0m),
            Kline("5m", Five, open, last, h: last - 5m, l: last + 5m),
            Kline("5m", Five, open, last, o: last + 50m, h: last + 1m),
            Kline("5m", Five, open, last * 1.5m), // +50 % from the reference
            Kline("5m", Five, open, last * 0.8m));

        sim.Push(Exchange.Binance, Kline("5m", Five, open, last * 1.10m, h: last * 3m)); // a wick of 3x is shown as it is
        Assert.Equal(last * 1.10m, sim.Held[^1].Close);
        Assert.Equal(last * 3m, sim.Held[^1].High);

        AssertNoChange(sim, Exchange.Binance, Kline("5m", Five, open, last * 1.5m)); // +36 % from the accepted close
        sim.Push(Exchange.Binance, Kline("5m", Five, open, last * 1.21m)); // +10 % from it, +21 % from the first: accepted
        Assert.Equal(last * 1.21m, sim.Held[^1].Close);
    }

    [Fact]
    public void Binance_miniTicker_drops_impossible_tickers_only()
    {
        using var sim = Go(Five);
        var t = sim.Client.Ticker!;

        AssertNoChange(sim, Exchange.Binance,
            Mini(0m, t.Open, t.High, t.Low),
            Mini(t.Last, 0m, t.High, t.Low),
            Mini(t.Last, t.Open, t.High, -1m),
            Mini(t.Last, t.Open, t.Low, t.High), // low above high
            Mini(t.High + 1m, t.Open, t.High, t.Low), // last above the range
            Mini(t.Low - 1m, t.Open, t.High, t.Low));
        Assert.NotEmpty(sim.Drops);

        sim.Push(Exchange.Binance, Mini(t.Last + 10m, t.High * 2m, t.High, t.Low)); // open is not range-checked
        Assert.Equal(t.High * 2m, sim.Client.Ticker!.Open);
        Assert.Equal(t.Last + 10m, sim.Client.Ticker!.Last);

        sim.Push(Exchange.Binance, Mini(t.Last * 3m, t.Open, t.Last * 3m + 1m, t.Low)); // never jump-checked
        Assert.Equal(t.Last * 3m, sim.Client.Ticker!.Last);
    }

    [Fact]
    public void Binance_rest_poll_drops_a_bad_candle_and_a_bad_ticker_and_keeps_the_good_one()
    {
        using var sim = Go(Five, socketDown: true);
        sim.WaitForPolling();
        var before = sim.Held;
        var ticker = sim.Client.Ticker;
        var last = CloseAt(sim.Now, Five);
        sim.Rewrite = (u, b) =>
        {
            if (IsBinanceKlines(u))
            {
                return Rows(b, false, rows =>
                {
                    Jump(rows, 0);
                    Put(rows, 1, (2, (last * 1.06m).ToString(Inv)), (4, (last * 1.05m).ToString(Inv)));
                });
            }

            var o = JsonNode.Parse(b)!;
            o["lastPrice"] = "0";
            return o.ToJsonString();
        };

        sim.Advance(TimeSpan.FromSeconds(16));

        Assert.Equal(before[^2], sim.Held[^2]);
        Assert.Equal(last * 1.05m, sim.Held[^1].Close);
        Assert.Equal(ticker, sim.Client.Ticker);
    }

    [Fact]
    public void A_dropped_tick_is_raised_once_per_reason_per_minute()
    {
        using var sim = Go(Five);
        Assert.Empty(sim.Drops);
        var open = IntervalData.Floor(sim.Now, Five);
        var last = CloseAt(sim.Now, Five);
        var zero = Kline("5m", Five, open, last, o: 0m);
        var inverted = Kline("5m", Five, open, last, h: last - 5m, l: last + 5m);

        sim.Push(Exchange.Binance, zero);
        sim.Push(Exchange.Binance, zero);
        sim.Push(Exchange.Binance, zero);
        Assert.Single(sim.Drops);
        sim.Push(Exchange.Binance, inverted);
        Assert.Equal(2, sim.Drops.Count);
        Assert.NotEqual(sim.Drops[0].Reason, sim.Drops[1].Reason);

        sim.Advance(TimeSpan.FromSeconds(30));
        sim.Push(Exchange.Binance, zero);
        Assert.Equal(2, sim.Drops.Count);

        sim.Advance(TimeSpan.FromSeconds(40)); // 70 s after the first one of its reason
        sim.Push(Exchange.Binance, zero);
        Assert.Equal(3, sim.Drops.Count);
        Assert.Equal(sim.Drops[0].Reason, sim.Drops[2].Reason);
        Assert.Equal(sim.Now, sim.Drops[2].At);
    }

    // ---- references reset on every Commit ------------------------------------------------------------

    [Fact]
    public void After_a_long_offline_gap_the_new_level_is_not_locked_out()
    {
        using var sim = Go(Five);
        var open = IntervalData.Floor(sim.Now, Five);
        var last = CloseAt(sim.Now, Five);
        sim.Push(Exchange.Binance, Kline("5m", Five, open, last * 1.01m)); // the reference is at the old level
        sim.Inner.OpenSocket(Exchange.Binance)!.ServerClose();
        sim.Drain();
        sim.Rewrite = BinanceRows(rows => Scale(rows, 1.5m)); // the market moved 50 % while offline
        sim.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(last * 1.5m, sim.Held[^1].Close);
        sim.Push(Exchange.Binance, Kline("5m", Five, open, last * 1.5m * 1.02m));
        Assert.Equal(last * 1.5m * 1.02m, sim.Held[^1].Close);
        AssertNoChange(sim, Exchange.Binance, Kline("5m", Five, open, last)); // the old level is now the impossible one
    }

    [Fact]
    public void After_a_source_switch_the_new_source_is_not_locked_out()
    {
        using var sim = Go(Five);
        var open = IntervalData.Floor(sim.Now, Five);
        var last = CloseAt(sim.Now, Five);
        sim.Push(Exchange.Binance, Kline("5m", Five, open, last * 1.01m)); // the reference is at the old level
        sim.Inner.OpenSocket(Exchange.Binance)!.ServerClose();
        sim.Drain();
        sim.Inner.BinanceRestStatus = 500;
        sim.Rewrite = KrakenRows(rows => Scale(rows, 2m)); // Kraken quotes 2x Binance's last good level
        sim.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(MarketSource.Kraken, sim.Client.Source);
        Assert.Equal(last * 2m, sim.Held[^1].Close);
        sim.Push(Exchange.Kraken, IntervalData.KrakenWsOhlc(5, open, last * 2.02m, last * 2.03m, last * 2.01m, last * 2.02m, 3m, sim.Now));
        Assert.Equal(last * 2.02m, sim.Held[^1].Close);
        AssertNoChange(sim, Exchange.Kraken, IntervalData.KrakenWsOhlc(5, open, last, last + 1m, last - 1m, last, 3m, sim.Now));
    }

    [Fact]
    public void After_an_interval_change_the_reloaded_series_is_not_locked_out()
    {
        using var sim = Go(Five);
        sim.Push(Exchange.Binance, Kline("5m", Five, IntervalData.Floor(sim.Now, Five), CloseAt(sim.Now, Five) * 1.01m)); // old reference
        sim.Rewrite = (u, b) => IsBinanceKlines(u) && IntervalTransport.Query(u, "interval") == "4h" ? Rows(b, false, rows => Scale(rows, 1.5m)) : null;
        sim.Client.ChangeInterval(FourHours);
        sim.Advance(TimeSpan.FromSeconds(3));

        var open = IntervalData.Floor(sim.Now, FourHours);
        var last = CloseAt(sim.Now, FourHours);
        Assert.Equal(FourHours, sim.Client.Interval);
        Assert.Equal(last * 1.5m, sim.Held[^1].Close);
        sim.Push(Exchange.Binance, Kline("4h", FourHours, open, last * 1.5m * 1.02m));
        Assert.Equal(last * 1.5m * 1.02m, sim.Held[^1].Close);
        AssertNoChange(sim, Exchange.Binance, Kline("4h", FourHours, open, last));
    }

    // ---- Kraken --------------------------------------------------------------------------------------

    [Fact]
    public void Kraken_rest_history_drops_impossible_candles_and_keeps_every_other()
    {
        using var sim = Go(Five, KrakenRows(rows =>
        {
            Jump(rows, 100);
            Put(rows, 200, (3, "0")); // zero low
            Put(rows, 300, (2, "1")); // high below low
            Put(rows, 400, (6, "-1")); // negative volume
        }), kraken: true);

        var bad = new[] { 100, 200, 300, 400 };
        var expected = IntervalData.OpenTimes(Five, sim.Now, 720).Where((_, i) => !bad.Contains(i))
            .Select(t => IntervalData.Ohlcv(IntervalData.Source(t, Five)));
        Assert.Equal(expected, sim.Held.Select(IntervalData.Ohlcv));
    }

    [Fact]
    public void Kraken_ws_drops_impossible_candles_and_keeps_good_ones()
    {
        using var sim = Go(Five, kraken: true);
        var open = IntervalData.Floor(sim.Now, Five);
        var last = CloseAt(sim.Now, Five);
        string Ws(decimal o, decimal h, decimal l, decimal c, decimal v = 3m) => IntervalData.KrakenWsOhlc(5, open, o, h, l, c, v, sim.Now);

        AssertNoChange(sim, Exchange.Kraken,
            Ws(0m, last + 1m, last - 1m, last),
            Ws(last, last - 5m, last + 5m, last),
            Ws(last, last + 1m, last - 1m, last + 50m),
            Ws(last * 1.5m, last * 1.5m + 1m, last * 1.5m - 1m, last * 1.5m));
        Assert.NotEmpty(sim.Drops);

        sim.Push(Exchange.Kraken, Ws(last, last * 1.1m, last - 1m, last * 1.05m));
        Assert.Equal(last * 1.05m, sim.Held[^1].Close);
    }

    [Fact]
    public void Kraken_6h_series_is_aggregated_without_a_dropped_60_minute_candle()
    {
        var start = Fixtures.Utc("2026-10-31T17:20:00Z");
        using var sim = Go(SixHours, KrakenRows(rows => Jump(rows, 700)), kraken: true, start: start);

        var hours = IntervalData.OpenTimes(Hour, sim.Now, 720).Select(t => IntervalData.Source(t, Hour)).ToList();
        hours.RemoveAt(700);
        Assert.Equal(IntervalData.Aggregate(hours, SixHours, sim.Now), sim.Held);
    }

    [Fact]
    public void Kraken_rest_poll_drops_a_bad_candle_and_a_bad_ticker_and_keeps_the_good_one()
    {
        using var sim = Go(Five, kraken: true, socketDown: true);
        sim.WaitForPolling();
        sim.Advance(TimeSpan.FromSeconds(16)); // one good poll: the ticker is the REST one now
        var before = sim.Held;
        var ticker = sim.Client.Ticker;
        var last = CloseAt(sim.Now, Five);
        sim.Rewrite = (u, b) =>
        {
            if (IsKrakenOhlc(u))
            {
                return Rows(b, true, rows =>
                {
                    Jump(rows, 0);
                    Put(rows, 1, (2, (last * 1.06m).ToString(Inv)), (4, (last * 1.05m).ToString(Inv)));
                });
            }

            var o = JsonNode.Parse(b)!;
            o["result"]!["XXBTZUSD"]!["c"]![0] = "0";
            return o.ToJsonString();
        };

        sim.Advance(TimeSpan.FromSeconds(15));

        Assert.Equal(before[^2], sim.Held[^2]);
        Assert.Equal(last * 1.05m, sim.Held[^1].Close);
        Assert.Equal(ticker, sim.Client.Ticker);
    }

    // ---- Kraken: the 24 h ticker of a chart coarser than 60 minutes -----------------------------------

    private static List<TransportCall> Hourly(FSim sim) =>
        sim.Inner.Rest(Exchange.Kraken, "ohlc").Where(c => IntervalTransport.Query(c.Url, "interval") == "60").ToList();
    private static void AssertCovers24h(TransportCall call)
    {
        if (IntervalTransport.Query(call.Url, "since") is { } since)
        {
            Assert.True(long.Parse(since, Inv) <= (call.At - TimeSpan.FromHours(24)).ToUnixTimeSeconds(), "since " + call.Url);
        }
    }

    [Theory]
    [InlineData(4, 240)]
    [InlineData(8, 240)]
    [InlineData(12, 240)]
    [InlineData(24, 1440)]
    public void Kraken_ticker_over_60_minutes_comes_from_a_separate_24h_fetch_of_60_minute_candles(int hours, int chartMinutes)
    {
        var interval = TimeSpan.FromHours(hours);
        using var sim = Go(interval, kraken: true);

        var ohlc = sim.Inner.Rest(Exchange.Kraken, "ohlc");
        Assert.Contains(ohlc, c => IntervalTransport.Query(c.Url, "interval") == chartMinutes.ToString(Inv));
        Assert.NotEmpty(Hourly(sim));
        Assert.All(Hourly(sim), AssertCovers24h);
        AssertHourlyTicker(sim.Client.Ticker, sim.Now);

        var candles = sim.Held;
        Assert.NotEmpty(candles);
        Assert.Equal(IntervalData.Floor(sim.Now, interval), candles[^1].OpenTime);
        for (var i = 1; i < candles.Count; i++)
        {
            Assert.Equal(interval, candles[i].OpenTime - candles[i - 1].OpenTime); // no 60-minute candle in the chart
        }
    }

    [Theory]
    [InlineData(4)]
    public void Kraken_poll_over_60_minutes_derives_its_open_from_a_fresh_24h_hourly_fetch(int hours)
    {
        using var sim = Go(TimeSpan.FromHours(hours), kraken: true, socketDown: true);
        sim.WaitForPolling();
        var fetches = Hourly(sim).Count;

        sim.Advance(TimeSpan.FromSeconds(31)); // two polls

        Assert.True(Hourly(sim).Count >= fetches + 2, "each poll fetches the 60-minute candles for the ticker");
        Assert.All(Hourly(sim), AssertCovers24h);
        AssertHourlyTicker(sim.Client.Ticker, sim.Now, openOnly: true);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void Kraken_up_to_6h_asks_for_no_extra_ticker_fetch(int hours)
    {
        using var sim = Go(TimeSpan.FromHours(hours), kraken: true);

        var ohlc = sim.Inner.Rest(Exchange.Kraken, "ohlc");
        Assert.Single(ohlc);
        Assert.Equal("60", IntervalTransport.Query(ohlc[0].Url, "interval"));
        AssertHourlyTicker(sim.Client.Ticker, sim.Now);
    }
}
