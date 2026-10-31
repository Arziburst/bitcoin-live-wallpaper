using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Market.Data.Tests.Fakes;

namespace BitcoinWallpaper.Market.Data.Tests;

// Contract #3, criterion: reconnect uses jittered exponential backoff 1 s to 60 s; ... Retry-After honoured
// on 429; proactive reconnect before 24 h; stale after 30 s without data is exposed as a state.
// (The switch to Kraken is in FailoverTests.) Client tests run on a fake clock and a fake transport (Sim).
public class ReconnectPolicyTests
{
    private static readonly double[] Jitters = [0.0, 0.25, 0.5, 0.75, 0.999999];

    // ---- ReconnectPolicy.BackoffDelay (pure) ----

    [Fact]
    public void Backoff_bounds_are_1_and_60_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), ReconnectPolicy.MinDelay);
        Assert.Equal(TimeSpan.FromSeconds(60), ReconnectPolicy.MaxDelay);
    }

    [Fact]
    public void Backoff_delay_always_stays_between_1_and_60_seconds()
    {
        var attempts = Enumerable.Range(1, 40).Concat([100, 1000, int.MaxValue]);
        foreach (var attempt in attempts)
        {
            foreach (var jitter in Jitters)
            {
                var delay = ReconnectPolicy.BackoffDelay(attempt, jitter);
                Assert.InRange(delay, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
            }
        }
    }

    [Fact]
    public void Backoff_starts_near_1_second_and_grows_exponentially()
    {
        foreach (var jitter in Jitters)
        {
            Assert.True(ReconnectPolicy.BackoffDelay(1, jitter) <= TimeSpan.FromSeconds(2),
                $"first delay {ReconnectPolicy.BackoffDelay(1, jitter)} at jitter {jitter}");
            for (var attempt = 1; attempt < 40; attempt++)
            {
                Assert.True(
                    ReconnectPolicy.BackoffDelay(attempt + 1, jitter) >= ReconnectPolicy.BackoffDelay(attempt, jitter),
                    $"delay shrinks from attempt {attempt} to {attempt + 1} at jitter {jitter}");
            }
        }

        const double high = 0.999999;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var now = ReconnectPolicy.BackoffDelay(attempt, high);
            var next = ReconnectPolicy.BackoffDelay(attempt + 1, high);
            Assert.True(next.TotalSeconds >= 1.5 * now.TotalSeconds,
                $"attempt {attempt}: {now} -> {next} is not exponential growth");
        }
    }

    [Fact]
    public void Backoff_reaches_the_60_second_cap()
    {
        Assert.True(ReconnectPolicy.BackoffDelay(20, 0.999999) >= TimeSpan.FromSeconds(59));
        Assert.True(ReconnectPolicy.BackoffDelay(int.MaxValue, 0.999999) >= TimeSpan.FromSeconds(59));
    }

    [Fact]
    public void Jitter_spreads_the_delay()
    {
        var delays = new[] { 0.0, 0.1, 0.3, 0.5, 0.7, 0.9, 0.999999 }
            .Select(j => ReconnectPolicy.BackoffDelay(6, j))
            .Distinct()
            .Count();

        Assert.True(delays >= 5, $"only {delays} distinct delays for 7 jitter draws at attempt 6");
    }

    // ---- MarketDataClient timing (fake clock, fake transport) ----

    [Fact]
    public void Client_backoff_between_failed_cycles_grows_from_1s_to_the_60s_cap_while_everything_is_down()
    {
        using var sim = new Sim(jitter: () => 0.999999);
        sim.Transport.Binance.RestDown = sim.Transport.Binance.SocketDown = true;
        sim.Transport.Kraken.RestDown = sim.Transport.Kraken.SocketDown = true;
        sim.Run();

        var end = sim.Start + TimeSpan.FromMinutes(30);
        sim.AdvanceTo(end);

        var cycles = sim.Transport.CycleTimes();
        Assert.True(cycles.Count >= 5, $"only {cycles.Count} connect cycles in 30 minutes");
        Assert.Equal(sim.Start, cycles[0]);
        var gaps = cycles.Zip(cycles.Skip(1), (a, b) => b - a).ToList();
        Assert.InRange(gaps[0], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        Assert.All(gaps, gap => Assert.InRange(gap, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60)));
        Assert.True(gaps.Max() >= TimeSpan.FromSeconds(59),
            $"backoff never reached the cap; gaps: {string.Join(", ", gaps.Select(g => g.TotalSeconds))}");
        Assert.True(cycles[^1] >= end - TimeSpan.FromSeconds(60), "the client stopped retrying");
    }

    [Fact]
    public void Client_backoff_uses_the_injected_jitter()
    {
        static List<TimeSpan> Schedule(double jitter)
        {
            using var sim = new Sim(jitter: () => jitter);
            sim.Transport.Binance.RestDown = sim.Transport.Binance.SocketDown = true;
            sim.Transport.Kraken.RestDown = sim.Transport.Kraken.SocketDown = true;
            sim.Run();
            sim.Advance(TimeSpan.FromMinutes(10));
            return sim.Transport.CycleTimes().Select(t => t - sim.Start).ToList();
        }

        Assert.NotEqual(Schedule(0.0), Schedule(0.999999));
    }

    [Fact]
    public void Retry_After_on_429_is_honoured()
    {
        using var sim = new Sim();
        sim.Transport.Binance.NextRestResults.Enqueue(
            new HttpResult(429, "{\"code\":-1003,\"msg\":\"Too many requests.\"}", TimeSpan.FromSeconds(90)));
        sim.Run();

        sim.AdvanceFeeding(Exchange.Binance, sim.Start + TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(1),
            Fixtures.BinanceMiniTicker);

        var rest = sim.Transport.CallsTo(Exchange.Binance).Where(c => !c.IsSocket).Select(c => c.At).ToList();
        Assert.Equal(sim.Start, rest[0]);
        Assert.DoesNotContain(rest, t => t > sim.Start && t < sim.Start + TimeSpan.FromSeconds(90));
        Assert.Contains(rest, t => t >= sim.Start + TimeSpan.FromSeconds(90) && t <= sim.Start + TimeSpan.FromSeconds(151));
        Assert.Empty(sim.Transport.CallsTo(Exchange.Kraken));
        Assert.Equal(MarketSource.Binance, sim.Client.Source);
        Assert.Equal(5, sim.Client.Candles.Count);
    }

    [Fact]
    public void Proactive_reconnect_happens_before_24_hours_of_socket_life()
    {
        using var sim = new Sim();
        sim.Run();
        var first = sim.Transport.OpenSocket(Exchange.Binance);
        Assert.NotNull(first);

        sim.AdvanceFeeding(Exchange.Binance, sim.Start + TimeSpan.FromHours(24), TimeSpan.FromSeconds(10),
            Fixtures.BinanceMiniTicker);

        var sockets = sim.Transport.Sockets.Where(s => s.Exchange == Exchange.Binance).ToList();
        Assert.True(sockets.Count >= 2, "no reconnect within 24 h of a healthy socket");
        Assert.True(sockets[1].OpenedAt < first.OpenedAt + TimeSpan.FromHours(24),
            $"second socket opened at {sockets[1].OpenedAt:O}, first at {first.OpenedAt:O}");
        Assert.NotNull(first.DisposedAt);
        for (var i = 1; i < sockets.Count; i++)
        {
            Assert.True(sockets[i].OpenedAt - sockets[i - 1].OpenedAt >= TimeSpan.FromHours(1),
                $"healthy socket {i - 1} replaced after only {sockets[i].OpenedAt - sockets[i - 1].OpenedAt}");
        }

        Assert.Empty(sim.Transport.CallsTo(Exchange.Kraken));
        Assert.Equal(FeedState.Live, sim.Client.State);
    }

    [Fact]
    public void Proactive_reconnect_opens_the_new_socket_before_closing_the_old()
    {
        using var sim = new Sim();
        sim.Run();

        sim.AdvanceFeeding(Exchange.Binance, sim.Start + TimeSpan.FromHours(24), TimeSpan.FromSeconds(10),
            Fixtures.BinanceMiniTicker);

        var sockets = sim.Transport.Sockets.Where(s => s.Exchange == Exchange.Binance).ToList();
        Assert.True(sockets.Count >= 2, "no reconnect within 24 h of a healthy socket");
        Assert.NotNull(sockets[0].DisposedOrder);
        Assert.True(sockets[0].DisposedOrder > sockets[1].OpenedOrder,
            $"old socket closed (event {sockets[0].DisposedOrder}, {sockets[0].DisposedAt:O}) before the new one opened " +
            $"(event {sockets[1].OpenedOrder}, {sockets[1].OpenedAt:O})");
    }

    [Fact]
    public void Stale_after_30_seconds_without_data_is_exposed_as_state()
    {
        using var sim = new Sim();
        sim.Run();
        sim.Push(Exchange.Binance, Fixtures.BinanceKlineForming);
        var lastData = sim.Now;
        Assert.Equal(FeedState.Live, sim.Client.State);

        // From here no source can deliver data: the open socket stays silent, REST and Kraken are down.
        sim.Transport.Binance.RestDown = true;
        sim.Transport.Kraken.RestDown = sim.Transport.Kraken.SocketDown = true;

        sim.AdvanceTo(lastData + TimeSpan.FromSeconds(29));
        Assert.Equal(FeedState.Live, sim.Client.State);

        sim.AdvanceTo(lastData + TimeSpan.FromSeconds(31));
        Assert.Equal(FeedState.Stale, sim.Client.State);
        Assert.Equal(5, sim.Client.Candles.Count); // stale keeps the series, it does not fake or clear it

        // Data flows again: the state returns to Live.
        sim.Transport.Binance.Heal();
        sim.Transport.Kraken.Heal();
        for (var i = 0; i < 180 && sim.Client.State != FeedState.Live; i++)
        {
            sim.Advance(TimeSpan.FromSeconds(1));
            sim.Transport.OpenSocket(Exchange.Binance)?.Push(Fixtures.BinanceMiniTicker);
            sim.Transport.OpenSocket(Exchange.Kraken)?.Push(Fixtures.KrakenWsUpdate);
            sim.Drain();
        }

        Assert.Equal(FeedState.Live, sim.Client.State);
    }
}
