using System.Net.WebSockets;

namespace BitcoinWallpaper.Market.Data.Tests.Fakes;

internal enum Exchange
{
    Binance,
    Kraken,
}

internal sealed record TransportCall(Exchange Exchange, bool IsSocket, Uri Url, DateTimeOffset At);

/// <summary>What one fake exchange does. Healthy by default: REST serves the recorded fixtures, sockets
/// connect and stay silent until the test pushes messages.</summary>
internal sealed class ExchangeScript
{
    /// <summary>Every REST request fails like a network error (HttpRequestException).</summary>
    public bool RestDown { get; set; }

    /// <summary>Every socket connect fails (WebSocketException).</summary>
    public bool SocketDown { get; set; }

    /// <summary>The next N socket connects fail (WebSocketException), later ones succeed.</summary>
    public int FailNextSockets { get; set; }

    /// <summary>Every REST request answers this HTTP status (e.g. 451), unless RestDown.</summary>
    public int? RestStatus { get; set; }

    /// <summary>Answers served, in order, before anything else (e.g. one 429 with Retry-After).</summary>
    public Queue<HttpResult> NextRestResults { get; } = new();

    public void Heal()
    {
        RestDown = false;
        SocketDown = false;
        FailNextSockets = 0;
        RestStatus = null;
        NextRestResults.Clear();
    }
}

/// <summary>Fake network: routes by host (contains "binance" / "kraken"), records every call with the fake
/// clock's time, and serves the recorded fixtures.</summary>
internal sealed class FakeTransport(TimeProvider time) : IMarketTransport
{
    public ExchangeScript Binance { get; } = new();

    public ExchangeScript Kraken { get; } = new();

    public List<TransportCall> Calls { get; } = [];

    public List<FakeSocket> Sockets { get; } = [];

    private long events;

    /// <summary>Global order of socket opens and disposals (the fake clock does not move between them).</summary>
    private long NextEvent() => ++events;

    public ExchangeScript Script(Exchange exchange) => exchange == Exchange.Binance ? Binance : Kraken;

    public Task<HttpResult> GetAsync(Uri url, CancellationToken cancellationToken)
    {
        var exchange = Classify(url);
        Calls.Add(new TransportCall(exchange, IsSocket: false, url, time.GetUtcNow()));
        var script = Script(exchange);

        if (script.RestDown)
        {
            return Task.FromException<HttpResult>(new HttpRequestException("fake: network down"));
        }

        if (script.NextRestResults.TryDequeue(out var scripted))
        {
            return Task.FromResult(scripted);
        }

        if (script.RestStatus is { } status)
        {
            return Task.FromResult(new HttpResult(status, "{\"code\":0,\"msg\":\"fake status\"}"));
        }

        var path = url.AbsolutePath;
        string? body = exchange switch
        {
            Exchange.Binance when Has(path, "klines") => Fixtures.BinanceKlines,
            Exchange.Binance when Has(path, "ticker") => Fixtures.BinanceTicker,
            Exchange.Binance when Has(path, "ping") => "{}",
            Exchange.Binance when Has(path, "time") =>
                "{\"serverTime\":" + time.GetUtcNow().ToUnixTimeMilliseconds() + "}",
            Exchange.Kraken when Has(path, "ohlc") => Fixtures.KrakenOhlc,
            Exchange.Kraken when Has(path, "ticker") => Fixtures.KrakenTicker,
            Exchange.Kraken when Has(path, "time") =>
                "{\"error\":[],\"result\":{\"unixtime\":" + time.GetUtcNow().ToUnixTimeSeconds() + "}}",
            _ => null,
        };

        return Task.FromResult(body is null ? new HttpResult(404, "") : new HttpResult(200, body));
    }

    public Task<IMarketSocket> ConnectAsync(Uri url, CancellationToken cancellationToken)
    {
        var exchange = Classify(url);
        Calls.Add(new TransportCall(exchange, IsSocket: true, url, time.GetUtcNow()));
        var script = Script(exchange);

        if (script.SocketDown)
        {
            return Task.FromException<IMarketSocket>(new WebSocketException("fake: connect refused"));
        }

        if (script.FailNextSockets > 0)
        {
            script.FailNextSockets--;
            return Task.FromException<IMarketSocket>(new WebSocketException("fake: connect refused"));
        }

        var socket = new FakeSocket(exchange, url, time, NextEvent);
        Sockets.Add(socket);
        return Task.FromResult<IMarketSocket>(socket);
    }

    /// <summary>The newest socket of the exchange that is still open, or null.</summary>
    public FakeSocket? OpenSocket(Exchange exchange) =>
        Sockets.LastOrDefault(s => s.Exchange == exchange && s.IsOpen);

    public IEnumerable<TransportCall> CallsTo(Exchange exchange) => Calls.Where(c => c.Exchange == exchange);

    /// <summary>Connect cycles = distinct instants with calls (calls of one cycle happen at one instant;
    /// cycles are separated by a backoff of at least 1 s).</summary>
    public IReadOnlyList<DateTimeOffset> CycleTimes(Exchange? exchange = null) =>
        Calls.Where(c => exchange is null || c.Exchange == exchange)
            .Select(c => c.At)
            .Distinct()
            .OrderBy(t => t)
            .ToList();

    private static bool Has(string path, string part) => path.Contains(part, StringComparison.OrdinalIgnoreCase);

    private static Exchange Classify(Uri url)
    {
        if (url.Host.Contains("binance", StringComparison.OrdinalIgnoreCase))
        {
            return Exchange.Binance;
        }

        if (url.Host.Contains("kraken", StringComparison.OrdinalIgnoreCase))
        {
            return Exchange.Kraken;
        }

        throw new InvalidOperationException("fake transport: unexpected host " + url);
    }
}

/// <summary>A fake WebSocket. The test pushes server messages; the client reads them in order.</summary>
internal sealed class FakeSocket(Exchange exchange, Uri url, TimeProvider time, Func<long> nextEvent) : IMarketSocket
{
    private readonly Queue<string?> inbox = new();
    private TaskCompletionSource<string?>? pending;
    private bool closedByServer;
    private bool failNextReceive;

    public Exchange Exchange { get; } = exchange;

    public Uri Url { get; } = url;

    public DateTimeOffset OpenedAt { get; } = time.GetUtcNow();

    /// <summary>Position of the open among all socket opens and disposals.</summary>
    public long OpenedOrder { get; } = nextEvent();

    /// <summary>Position of the first disposal among all socket opens and disposals, or null.</summary>
    public long? DisposedOrder { get; private set; }

    public DateTimeOffset? DisposedAt { get; private set; }

    public List<string> Sent { get; } = [];

    public bool IsOpen => DisposedAt is null && !closedByServer && !failNextReceive;

    public void Push(string text)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("fake socket is not open");
        }

        var waiter = pending;
        pending = null;
        if (waiter is not null)
        {
            waiter.TrySetResult(text);
        }
        else
        {
            inbox.Enqueue(text);
        }
    }

    public void ServerClose()
    {
        closedByServer = true;
        var waiter = pending;
        pending = null;
        if (waiter is not null)
        {
            waiter.TrySetResult(null);
        }
        else
        {
            inbox.Enqueue(null);
        }
    }

    public void Fail()
    {
        var waiter = pending;
        pending = null;
        if (waiter is not null)
        {
            waiter.TrySetException(new WebSocketException("fake: connection reset"));
        }

        failNextReceive = true;
    }

    public Task SendAsync(string text, CancellationToken cancellationToken)
    {
        if (DisposedAt is not null)
        {
            return Task.FromException(new ObjectDisposedException(nameof(FakeSocket)));
        }

        Sent.Add(text);
        return Task.CompletedTask;
    }

    public Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (DisposedAt is not null)
        {
            return Task.FromException<string?>(new ObjectDisposedException(nameof(FakeSocket)));
        }

        if (inbox.TryDequeue(out var queued))
        {
            return Task.FromResult(queued);
        }

        if (failNextReceive)
        {
            return Task.FromException<string?>(new WebSocketException("fake: connection reset"));
        }

        if (closedByServer)
        {
            return Task.FromResult<string?>(null);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<string?>(cancellationToken);
        }

        var waiter = new TaskCompletionSource<string?>();
        pending = waiter;
        cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
        return waiter.Task;
    }

    public ValueTask DisposeAsync()
    {
        DisposedAt ??= time.GetUtcNow();
        DisposedOrder ??= nextEvent();
        var waiter = pending;
        pending = null;
        waiter?.TrySetCanceled();
        return ValueTask.CompletedTask;
    }
}
