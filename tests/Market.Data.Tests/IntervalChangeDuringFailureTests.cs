// Contract #18, criterion 2 (verifier finding, round 5): ChangeInterval cancels the running connect cycle; when a
// REST request or a socket connect of that cycle fails just after the cancel (an HTTP 500, a thrown
// HttpRequestException or WebSocketException that does not observe the token), the failure must not escape
// RunAsync. The client reloads the new interval at once and keeps running: RunAsync is not faulted or finished,
// the 6h history is loaded, the new socket subscribes btcusdt@kline_6h, State is Live again.
//
// No new API: the existing MarketDataClient (4-argument constructor, ChangeInterval, Candles, State) over the
// IntervalTransport fake, wrapped here so that one request can be held open across ChangeInterval and then
// failed (the race the verifier found: the answer arrives after the cancel, the transport ignores the token).

using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Market.Data.Tests.Fakes;

namespace BitcoinWallpaper.Market.Data.Tests;

public class IntervalChangeDuringFailureTests
{
    private static readonly TimeSpan FourHours = TimeSpan.FromHours(4);
    private static readonly TimeSpan SixHours = TimeSpan.FromHours(6);

    /// <summary>Passes everything to <see cref="IntervalTransport"/>, except the first request that matches the armed
    /// hold: that one stays pending (ignoring its token) until the test fails it.</summary>
    private sealed class GatedTransport(IntervalTransport inner) : IMarketTransport
    {
        public IntervalTransport Inner { get; } = inner;

        /// <summary>REST requests whose path contains this are held (the first one only).</summary>
        public string? HoldGetPath { get; set; }

        /// <summary>The first socket connect is held.</summary>
        public bool HoldConnect { get; set; }

        public TaskCompletionSource<HttpResult>? HeldGet { get; private set; }

        public TaskCompletionSource<IMarketSocket>? HeldConnect { get; private set; }

        public Task<HttpResult> GetAsync(Uri url, CancellationToken cancellationToken)
        {
            if (HoldGetPath is { } part && url.AbsolutePath.Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                HoldGetPath = null;
                HeldGet = new TaskCompletionSource<HttpResult>();
                return HeldGet.Task;
            }

            return Inner.GetAsync(url, cancellationToken);
        }

        public Task<IMarketSocket> ConnectAsync(Uri url, CancellationToken cancellationToken)
        {
            if (HoldConnect)
            {
                HoldConnect = false;
                HeldConnect = new TaskCompletionSource<IMarketSocket>();
                return HeldConnect.Task;
            }

            return Inner.ConnectAsync(url, cancellationToken);
        }
    }

    /// <summary>The deterministic harness of <see cref="IntervalSim"/> over a <see cref="GatedTransport"/>.</summary>
    private sealed class GatedSim : IDisposable
    {
        private readonly SynchronizationContext? previousContext;
        private readonly CancellationTokenSource cts = new();

        public GatedSim(TimeSpan interval)
        {
            previousContext = SynchronizationContext.Current;
            Context = new DeterministicContext();
            SynchronizationContext.SetSynchronizationContext(Context);
            Time = new FakeTimeProvider(Fixtures.RecordedAt);
            Transport = new GatedTransport(new IntervalTransport(Time));
            Client = new MarketDataClient(Transport, Time, () => 0.5, interval);
        }

        public DeterministicContext Context { get; }

        public FakeTimeProvider Time { get; }

        public GatedTransport Transport { get; }

        public MarketDataClient Client { get; }

        public Task? Run { get; private set; }

        public DateTimeOffset Now => Time.GetUtcNow();

        public void Start()
        {
            Run = Client.RunAsync(cts.Token);
            Context.RunUntilIdle();
        }

        /// <summary>Fails the test with RunAsync's own exception when it faulted, or when it ended at all.</summary>
        public void AssertRunning(string when)
        {
            Assert.NotNull(Run);
            if (Run.IsFaulted)
            {
                var e = Run.Exception!.GetBaseException();
                Assert.Fail($"{when}: RunAsync faulted with {e.GetType().Name}: {e.Message} - the feed has stopped");
            }

            Assert.False(Run.IsCompleted, $"{when}: RunAsync ended ({Run.Status}) - the feed has stopped");
        }

        public void Advance(TimeSpan by)
        {
            var target = Now + by;
            Context.RunUntilIdle();
            for (var firings = 0; firings < 1_000_000; firings++)
            {
                var timer = Time.NextDue(target);
                if (timer is null)
                {
                    break;
                }

                Time.SetNow(timer.Due!.Value);
                timer.Fire();
                Context.RunUntilIdle();
            }

            Time.SetNow(target);
            Context.RunUntilIdle();
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
                SynchronizationContext.SetSynchronizationContext(previousContext);
                cts.Dispose();
            }
        }
    }

    public static TheoryData<string> Failures => new()
    {
        "klines HTTP 500",
        "klines HttpRequestException",
        "ticker HTTP 500",
        "socket WebSocketException",
    };

    private static void Arm(GatedTransport transport, string failure)
    {
        if (failure.StartsWith("klines", StringComparison.Ordinal))
        {
            transport.HoldGetPath = "klines";
        }
        else if (failure.StartsWith("ticker", StringComparison.Ordinal))
        {
            transport.HoldGetPath = "ticker";
        }
        else
        {
            transport.HoldConnect = true;
        }
    }

    private static void Fail(GatedTransport transport, string failure)
    {
        switch (failure)
        {
            case "klines HTTP 500":
            case "ticker HTTP 500":
                Assert.NotNull(transport.HeldGet);
                transport.HeldGet.SetResult(new HttpResult(500, "{\"code\":-1000,\"msg\":\"fake: internal error\"}"));
                break;
            case "klines HttpRequestException":
                Assert.NotNull(transport.HeldGet);
                transport.HeldGet.SetException(new HttpRequestException("fake: connection reset"));
                break;
            default:
                Assert.NotNull(transport.HeldConnect);
                transport.HeldConnect.SetException(new WebSocketException("fake: connect refused"));
                break;
        }
    }

    private static List<string> Subscribed(FakeSocket socket)
    {
        var result = new List<string>();
        foreach (var text in socket.Sent)
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Array)
            {
                result.AddRange(p.EnumerateArray().Select(e => e.GetString() ?? ""));
            }
        }

        return result;
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public void A_failure_in_flight_when_the_interval_changes_does_not_stop_the_feed(string failure)
    {
        using var sim = new GatedSim(FourHours);
        Arm(sim.Transport, failure);
        sim.Start();
        Assert.True(sim.Transport.HeldGet is not null || sim.Transport.HeldConnect is not null,
            $"{failure}: the first connect cycle did not reach the held request");
        sim.AssertRunning("before the change");

        var callsBefore = sim.Transport.Inner.Calls.Count;
        sim.Client.ChangeInterval(SixHours);
        Fail(sim.Transport, failure);
        sim.Context.RunUntilIdle();
        sim.AssertRunning($"right after the {failure} that arrived after ChangeInterval");

        // The reload: within one connect cycle (35 s at most) a socket subscribed to the 6h klines is open.
        var deadline = sim.Now + TimeSpan.FromSeconds(35);
        while (sim.Now < deadline &&
               !(sim.Transport.Inner.OpenSocket(Exchange.Binance) is { } open && Subscribed(open).Contains("btcusdt@kline_6h")))
        {
            sim.Advance(TimeSpan.FromMilliseconds(250));
        }

        sim.AssertRunning($"up to 35 s after the {failure}");

        Assert.Equal(SixHours, sim.Client.Interval);
        var reload = sim.Transport.Inner.Calls.Skip(callsBefore)
            .Where(c => !c.IsSocket && c.Url.AbsolutePath.Contains("klines", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(reload);
        Assert.All(reload, c => Assert.Equal("6h", IntervalTransport.Query(c.Url, "interval")));

        var limit = int.Parse(IntervalTransport.Query(reload[^1].Url, "limit")!, System.Globalization.CultureInfo.InvariantCulture);
        var expected = IntervalData.OpenTimes(SixHours, sim.Now, limit).Select(t => IntervalData.BinanceCandle(t, SixHours, sim.Now)).ToList();
        Assert.Equal(expected, sim.Client.Candles.Snapshot());
        Assert.Equal(MarketSource.Binance, sim.Client.Candles.Source);

        var socket = sim.Transport.Inner.OpenSocket(Exchange.Binance);
        Assert.NotNull(socket);
        Assert.Contains("btcusdt@kline_6h", Subscribed(socket));
        Assert.DoesNotContain("btcusdt@kline_4h", Subscribed(socket));
        Assert.Equal(FeedState.Live, sim.Client.State);

        // Still streaming: a 6h kline updates the forming candle.
        var forming = Fixtures.Utc("2026-10-31T00:00:00Z");
        socket.Push(IntervalData.BinanceKlineMessage("6h", forming, SixHours, 70100m, 70900m, 69950m, 70777.7m, 1234.5m, closed: false));
        sim.Context.RunUntilIdle();
        Assert.Equal(new Candle(forming, 70100m, 70900m, 69950m, 70777.7m, 1234.5m, false), sim.Client.Candles.Snapshot()[^1]);
        sim.AssertRunning("after a 6h kline");
    }
}
