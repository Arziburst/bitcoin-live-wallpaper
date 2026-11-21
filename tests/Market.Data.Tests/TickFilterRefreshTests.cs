using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Market.Data.Tests.Fakes;

namespace BitcoinWallpaper.Market.Data.Tests;

// Contract #32, round 2 (verifier F1-F3). No new access point.
// F1+F3: streaming Kraken with a chart over 60 min, the ticker's 60-minute candles (REST OHLC interval=60) are fetched
// again at most every 15 min, started by a stream ohlc message (not by a heartbeat, not by a timer), never two at once.
// A failed refresh (HTTP 429 with Retry-After, 500, an exception) keeps the old hours and neither sets nor reads
// notBefore for it: the next reconnect timing is that of any lost socket (1 s). A refresh is skipped while
// notBefore[Kraken] is in the future (set by something else, here a 429 on a poll).
// F2: Binance REST poll whose klines are all impossible but whose ticker is valid: the ticker is applied, the poll
// does not mark the feed Live (no MarkData) and counts as a failed poll (3 in a row end the polling, a new cycle follows).
public class TickFilterRefreshTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly TimeSpan FourHours = TimeSpan.FromHours(4);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private sealed class Scripted(IntervalTransport inner) : IMarketTransport
    {
        /// <summary>Answers a request instead of the fake exchange (null: the fake exchange answers).</summary>
        public Func<Uri, Task<HttpResult>?>? Hook { get; set; }

        public Func<Uri, string, string?>? Rewrite { get; set; }

        /// <summary>Every GET asked, answered by the hook or not.</summary>
        public List<Uri> Asked { get; } = [];

        public async Task<HttpResult> GetAsync(Uri url, CancellationToken cancellationToken)
        {
            Asked.Add(url);
            if (Hook?.Invoke(url) is { } answer)
            {
                return await answer;
            }

            var result = await inner.GetAsync(url, cancellationToken);
            return result.StatusCode == 200 && Rewrite?.Invoke(url, result.Body) is { } body ? result with { Body = body } : result;
        }

        public Task<IMarketSocket> ConnectAsync(Uri url, CancellationToken cancellationToken) => inner.ConnectAsync(url, cancellationToken);
    }

    private sealed class RSim : IDisposable
    {
        private readonly SynchronizationContext? previous = SynchronizationContext.Current;
        private readonly CancellationTokenSource cts = new();
        private readonly Task run;

        public RSim(TimeSpan interval, bool kraken, bool socketDown = false)
        {
            Context = new DeterministicContext();
            SynchronizationContext.SetSynchronizationContext(Context);
            Time = new FakeTimeProvider(Fixtures.RecordedAt);
            Inner = new IntervalTransport(Time) { BinanceRestStatus = kraken ? 451 : null, BinanceSocketDown = !kraken && socketDown, KrakenSocketDown = kraken && socketDown };
            Net = new Scripted(Inner);
            Client = new MarketDataClient(Net, Time, () => 0.5, interval);
            run = Client.RunAsync(cts.Token);
            Drain();
            Advance(TimeSpan.FromSeconds(10));
        }

        public FakeTimeProvider Time { get; }

        public IntervalTransport Inner { get; }

        public Scripted Net { get; }

        public DeterministicContext Context { get; }

        public MarketDataClient Client { get; }

        public DateTimeOffset Now => Time.GetUtcNow();

        public void Drain()
        {
            Context.RunUntilIdle();
            if (run.IsFaulted)
            {
                ExceptionDispatchInfo.Capture(run.Exception!.GetBaseException()).Throw();
            }
        }

        /// <summary>Moves time; a Kraken heartbeat every 20 s keeps the stream alive.</summary>
        public void Advance(TimeSpan by, bool heartbeats = true)
        {
            var target = Now + by;
            Drain();
            while (Now < target)
            {
                var step = Now + TimeSpan.FromSeconds(20) < target ? Now + TimeSpan.FromSeconds(20) : target;
                while (Time.NextDue(step) is { } timer)
                {
                    Time.SetNow(timer.Due!.Value);
                    timer.Fire();
                    Drain();
                }

                Time.SetNow(step);
                Drain();
                if (heartbeats && Inner.OpenSocket(Exchange.Kraken) is { } socket)
                {
                    socket.Push(IntervalData.KrakenHeartbeat);
                    Drain();
                }
            }
        }

        /// <summary>A 4h ohlc message for the forming candle, close inside the filter.</summary>
        public void PushOhlc()
        {
            var c = Client.Candles.Snapshot()[^1].Close;
            Inner.OpenSocket(Exchange.Kraken)!.Push(IntervalData.KrakenWsOhlc(240, IntervalData.Floor(Now, FourHours), c, c + 1m, c - 1m, c, 2m, Now));
            Drain();
        }

        public int Hourly() => Net.Asked.Count(IsHourly);

        public void WaitForPolling()
        {
            for (var i = 0; i < 40 && Client.Mode != FeedMode.RestPoll; i++)
            {
                Advance(TimeSpan.FromSeconds(1), heartbeats: false);
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

    private static bool IsHourly(Uri u) => u.AbsolutePath.Contains("OHLC", StringComparison.OrdinalIgnoreCase) && IntervalTransport.Query(u, "interval") == "60";

    private static Task<HttpResult> Answer(HttpResult r) => Task.FromResult(r);

    private static void AssertOpenKept(RSim sim)
    {
        var hours = IntervalData.OpenTimes(Hour, sim.Now, 720);
        var window = hours.Where(t => t > hours[^1] - TimeSpan.FromHours(24)).First();
        Assert.Equal(IntervalData.Source(window, Hour).Open, sim.Client.Ticker!.Open);
    }

    [Fact]
    public void The_hourly_refresh_runs_after_15_minutes_on_a_stream_message_only_once()
    {
        using var sim = new RSim(FourHours, kraken: true);
        var before = sim.Hourly();
        Assert.Equal(1, before);

        sim.Advance(TimeSpan.FromMinutes(14));
        sim.PushOhlc();
        Assert.Equal(before, sim.Hourly()); // 14 min: not due

        sim.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(before, sim.Hourly()); // due, but nothing but heartbeats came: no timer of its own
        sim.PushOhlc();
        Assert.Equal(before + 1, sim.Hourly());

        sim.PushOhlc();
        sim.Advance(TimeSpan.FromMinutes(5));
        sim.PushOhlc();
        Assert.Equal(before + 1, sim.Hourly()); // answered: the next one is 15 min after it
    }

    [Fact]
    public void A_second_message_during_a_refresh_starts_no_overlapping_request()
    {
        using var sim = new RSim(FourHours, kraken: true);
        sim.Advance(TimeSpan.FromMinutes(16));
        var before = sim.Hourly();
        var pending = new TaskCompletionSource<HttpResult>();
        sim.Net.Hook = u => IsHourly(u) ? pending.Task : null;

        sim.PushOhlc();
        sim.PushOhlc();
        sim.Advance(TimeSpan.FromSeconds(30));
        sim.PushOhlc();
        Assert.Equal(before + 1, sim.Hourly());

        sim.Net.Hook = null;
        pending.SetResult(new HttpResult(500, ""));
        sim.Drain();
        sim.PushOhlc();
        Assert.Equal(before + 1, sim.Hourly()); // a failed refresh is retried after 15 min, not at once
    }

    [Theory]
    [InlineData("429")]
    [InlineData("500")]
    [InlineData("throws")]
    public void A_failed_refresh_keeps_the_old_hours_and_leaves_the_backoff_alone(string failure)
    {
        using var sim = new RSim(FourHours, kraken: true);
        sim.Advance(TimeSpan.FromMinutes(16));
        sim.Net.Hook = u => !IsHourly(u) ? null : failure switch
        {
            "429" => Answer(new HttpResult(429, "", TimeSpan.FromMinutes(5))),
            "500" => Answer(new HttpResult(500, "{}")),
            _ => Task.FromException<HttpResult>(new HttpRequestException("fake: network down")),
        };
        var before = sim.Hourly();

        sim.PushOhlc();
        Assert.Equal(before + 1, sim.Hourly());
        AssertOpenKept(sim);

        sim.Net.Hook = null;
        var lost = sim.Now;
        sim.Inner.OpenSocket(Exchange.Kraken)!.ServerClose();
        sim.Drain();
        sim.Advance(TimeSpan.FromSeconds(3), heartbeats: false);
        var chart = sim.Inner.Rest(Exchange.Kraken, "ohlc").Where(c => c.At >= lost && IntervalTransport.Query(c.Url, "interval") == "240").ToList();
        Assert.NotEmpty(chart); // the reconnect came after 1 s, not after the refresh's Retry-After
        Assert.True(chart[0].At - lost <= TimeSpan.FromSeconds(2), $"reconnect {chart[0].At - lost} after the loss");
    }

    [Fact]
    public void A_refresh_is_skipped_while_the_exchange_asked_to_be_left_alone()
    {
        using var sim = new RSim(FourHours, kraken: true, socketDown: true);
        sim.WaitForPolling();
        var asked = false;
        sim.Net.Hook = u =>
        {
            if (!asked && u.AbsolutePath.Contains("OHLC", StringComparison.OrdinalIgnoreCase))
            {
                asked = true;
                return Answer(new HttpResult(429, "", TimeSpan.FromMinutes(20))); // the first poll: Kraken wants 20 min of peace
            }

            return null;
        };
        sim.Advance(TimeSpan.FromSeconds(16), heartbeats: false);
        Assert.True(asked);
        sim.Inner.KrakenSocketDown = false;
        sim.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(FeedMode.WebSocket, sim.Client.Mode);
        sim.Advance(TimeSpan.FromMinutes(14)); // 17 min on: over 15 min since the hours, under the 20 min of peace
        var before = sim.Hourly();

        sim.PushOhlc();
        Assert.Equal(before, sim.Hourly());

        sim.Advance(TimeSpan.FromMinutes(5)); // peace is over
        sim.PushOhlc();
        Assert.Equal(before + 1, sim.Hourly());
    }

    [Fact]
    public void Binance_poll_with_only_impossible_candles_applies_the_valid_ticker_but_is_not_live_and_fails()
    {
        using var sim = new RSim(TimeSpan.FromMinutes(5), kraken: false, socketDown: true);
        sim.WaitForPolling();
        var ticker = sim.Client.Ticker!;
        int Histories() => sim.Inner.Rest(Exchange.Binance, "klines").Count(c => IntervalTransport.Query(c.Url, "limit") == "1000");
        var histories = Histories();
        sim.Net.Rewrite = (u, b) =>
        {
            if (u.AbsolutePath.Contains("klines", StringComparison.Ordinal) && IntervalTransport.Query(u, "limit") == "2")
            {
                var rows = JsonNode.Parse(b)!.AsArray();
                foreach (var row in rows)
                {
                    row![2] = "1"; // high below low
                }

                return rows.ToJsonString();
            }

            if (u.AbsolutePath.Contains("ticker", StringComparison.Ordinal))
            {
                var o = JsonNode.Parse(b)!;
                o["lastPrice"] = (ticker.Last + 10m).ToString(Inv);
                return o.ToJsonString();
            }

            return null;
        };

        sim.Advance(TimeSpan.FromSeconds(31), heartbeats: false); // two polls, both with a good ticker and no good candle
        Assert.Equal(ticker.Last + 10m, sim.Client.Ticker!.Last);
        Assert.Equal(FeedState.Stale, sim.Client.State); // the polls marked no data

        sim.Advance(TimeSpan.FromSeconds(20), heartbeats: false); // the third failed poll ends the polling
        Assert.True(Histories() > histories, "no new connect cycle after three failed polls");
    }
}
