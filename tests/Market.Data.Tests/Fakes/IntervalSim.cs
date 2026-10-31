using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;

namespace BitcoinWallpaper.Market.Data.Tests.Fakes;

/// <summary>Contract #18: a fake network that answers per requested interval, the way the exchanges do.
/// Binance /api/v3/klines: synthetic rows of the query's interval code (1m..1d), the newest min(limit, 1000)
/// (default 500); an unknown code answers HTTP 400. Kraken /0/public/OHLC: synthetic rows of the query's interval
/// in minutes, at most 720, from "since" on; an interval Kraken does not offer (e.g. 360) answers
/// {"error":["EGeneral:Invalid arguments"]}. Tickers are the recorded fixtures. Sockets are <see cref="FakeSocket"/>s.</summary>
internal sealed class IntervalTransport(TimeProvider time) : IMarketTransport
{
    public List<TransportCall> Calls { get; } = [];

    public List<FakeSocket> Sockets { get; } = [];

    public int? BinanceRestStatus { get; set; }

    public bool BinanceSocketDown { get; set; }

    public bool KrakenSocketDown { get; set; }

    private long events;

    public Task<HttpResult> GetAsync(Uri url, CancellationToken cancellationToken)
    {
        var binance = IsBinance(url);
        Calls.Add(new TransportCall(binance ? Exchange.Binance : Exchange.Kraken, IsSocket: false, url, time.GetUtcNow()));
        var path = url.AbsolutePath;
        if (binance)
        {
            if (BinanceRestStatus is { } status)
            {
                return Ok(status, "{\"code\":0,\"msg\":\"fake status\"}");
            }

            if (Has(path, "klines"))
            {
                var interval = IntervalData.FromBinanceCode(Query(url, "interval") ?? "");
                if (interval == TimeSpan.Zero)
                {
                    return Ok(400, "{\"code\":-1120,\"msg\":\"Invalid interval.\"}");
                }

                var limit = int.TryParse(Query(url, "limit"), CultureInfo.InvariantCulture, out var l) ? l : 500;
                return Ok(200, IntervalData.BinanceKlinesJson(interval, time.GetUtcNow(), Math.Clamp(limit, 1, 1000)));
            }

            if (Has(path, "ticker"))
            {
                return Ok(200, Fixtures.BinanceTicker);
            }

            return Ok(404, "");
        }

        if (Has(path, "ohlc"))
        {
            if (!int.TryParse(Query(url, "interval") ?? "1", CultureInfo.InvariantCulture, out var minutes) ||
                !IntervalData.KrakenNativeMinutes.Contains(minutes))
            {
                return Ok(200, "{\"error\":[\"EGeneral:Invalid arguments\"]}");
            }

            long? since = long.TryParse(Query(url, "since"), CultureInfo.InvariantCulture, out var s) ? s : null;
            return Ok(200, IntervalData.KrakenOhlcJson(TimeSpan.FromMinutes(minutes), time.GetUtcNow(), 720, since));
        }

        if (Has(path, "ticker"))
        {
            return Ok(200, Fixtures.KrakenTicker);
        }

        return Ok(404, "");
    }

    public Task<IMarketSocket> ConnectAsync(Uri url, CancellationToken cancellationToken)
    {
        var exchange = IsBinance(url) ? Exchange.Binance : Exchange.Kraken;
        Calls.Add(new TransportCall(exchange, IsSocket: true, url, time.GetUtcNow()));
        if (exchange == Exchange.Binance ? BinanceSocketDown : KrakenSocketDown)
        {
            return Task.FromException<IMarketSocket>(new WebSocketException("fake: connect refused"));
        }

        var socket = new FakeSocket(exchange, url, time, () => ++events);
        Sockets.Add(socket);
        return Task.FromResult<IMarketSocket>(socket);
    }

    public FakeSocket? OpenSocket(Exchange exchange) => Sockets.LastOrDefault(s => s.Exchange == exchange && s.IsOpen);

    /// <summary>REST requests to the path part (e.g. "klines", "ohlc"), in order.</summary>
    public List<TransportCall> Rest(Exchange exchange, string pathPart) =>
        Calls.Where(c => c.Exchange == exchange && !c.IsSocket && Has(c.Url.AbsolutePath, pathPart)).ToList();

    public static string? Query(Uri url, string name) =>
        url.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2 && string.Equals(p[0], name, StringComparison.OrdinalIgnoreCase))
            .Select(p => Uri.UnescapeDataString(p[1]))
            .FirstOrDefault();

    private static Task<HttpResult> Ok(int status, string body) => Task.FromResult(new HttpResult(status, body));

    private static bool Has(string path, string part) => path.Contains(part, StringComparison.OrdinalIgnoreCase);

    private static bool IsBinance(Uri url)
    {
        if (url.Host.Contains("binance", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (url.Host.Contains("kraken", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new InvalidOperationException("fake transport: unexpected host " + url);
    }
}

/// <summary>Contract #18: the deterministic harness of <see cref="Sim"/> around a client built with an interval
/// (the new constructor) over an <see cref="IntervalTransport"/>.</summary>
internal sealed class IntervalSim : IDisposable
{
    private const int MaxTimerFirings = 3_000_000;
    private readonly SynchronizationContext? previousContext;
    private readonly CancellationTokenSource cts = new();
    private Task? run;

    public IntervalSim(TimeSpan interval, DateTimeOffset? start = null)
    {
        previousContext = SynchronizationContext.Current;
        Context = new DeterministicContext();
        SynchronizationContext.SetSynchronizationContext(Context);
        try
        {
            Time = new FakeTimeProvider(start ?? Fixtures.RecordedAt);
            Transport = new IntervalTransport(Time);
            Client = new MarketDataClient(Transport, Time, () => 0.5, interval);
        }
        catch
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            throw;
        }
    }

    public FakeTimeProvider Time { get; }

    public IntervalTransport Transport { get; }

    public DeterministicContext Context { get; }

    public MarketDataClient Client { get; }

    public DateTimeOffset Now => Time.GetUtcNow();

    public void Run()
    {
        run = Client.RunAsync(cts.Token);
        Drain();
    }

    public void Drain()
    {
        Context.RunUntilIdle();
        if (run is { IsFaulted: true })
        {
            ExceptionDispatchInfo.Capture(run.Exception!.GetBaseException()).Throw();
        }
    }

    public void Advance(TimeSpan by) => AdvanceTo(Now + by);

    public void AdvanceTo(DateTimeOffset target)
    {
        Drain();
        for (var firings = 0; ; firings++)
        {
            if (firings > MaxTimerFirings)
            {
                throw new InvalidOperationException("Too many timer firings: the client busy-waits on tiny delays.");
            }

            var timer = Time.NextDue(target);
            if (timer is null)
            {
                break;
            }

            Time.SetNow(timer.Due!.Value);
            timer.Fire();
            Drain();
        }

        Time.SetNow(target);
        Drain();
    }

    /// <summary>Advances in steps, pushing the message to the exchange's open socket after each step.</summary>
    public void AdvanceFeeding(Exchange exchange, DateTimeOffset target, TimeSpan every, string message)
    {
        while (Now < target)
        {
            var next = Now + every;
            AdvanceTo(next < target ? next : target);
            var socket = Transport.OpenSocket(exchange);
            if (socket is not null)
            {
                socket.Push(message);
                Drain();
            }
        }
    }

    public void Push(Exchange exchange, string message)
    {
        var socket = Transport.OpenSocket(exchange)
            ?? throw new Xunit.Sdk.XunitException($"no open {exchange} socket at {Now:O}");
        socket.Push(message);
        Drain();
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
