using System.Text.Json;
using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Market.Data.Binance;
using BitcoinWallpaper.Market.Data.Kraken;

namespace BitcoinWallpaper.Market.Data;

/// <summary>The live feed: Binance first, Kraken after 3 consecutive failed connect cycles or an HTTP 451
/// at start. A connect cycle = history over REST + the WebSocket; it fails when either fails before the
/// feed is up. Backoff between cycles per ReconnectPolicy (it keeps growing while every source fails),
/// Retry-After honoured on 429, proactive reconnect before 24 h of socket life (new socket opened before
/// the old one is closed), State exposes staleness.</summary>
/// <remarks>Every wait goes through the TimeProvider and every random draw through the jitter function;
/// the client starts no threads of its own, so it runs deterministically on a single-threaded context.</remarks>
public sealed class MarketDataClient
{
    /// <summary>Consecutive failed connect cycles after which the other source is tried.</summary>
    private const int FailedCyclesBeforeSwitch = 3;

    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    /// <summary>A socket silent this long is treated as dead (Binance pushes every 1-2 s, Kraken sends
    /// heartbeats every second).</summary>
    private static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(60);

    /// <summary>Binance closes a socket after 24 h; it is replaced well before that.</summary>
    private static readonly TimeSpan SocketLifetime = TimeSpan.FromHours(23);

    private static readonly TimeSpan RenewRetry = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan CycleTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan TickerWindow = TimeSpan.FromHours(24);

    private static readonly Uri BinanceKlinesUrl =
        new("https://data-api.binance.vision/api/v3/klines?symbol=BTCUSDT&interval=5m&limit=1000");

    private static readonly Uri BinanceTickerUrl =
        new("https://data-api.binance.vision/api/v3/ticker/24hr?symbol=BTCUSDT");

    private static readonly Uri BinanceStreamUrl = new("wss://data-stream.binance.vision/ws");

    private const string BinanceSubscribe =
        """{"method":"SUBSCRIBE","params":["btcusdt@kline_5m","btcusdt@miniTicker"],"id":1}""";

    private static readonly Uri KrakenOhlcUrl = new("https://api.kraken.com/0/public/OHLC?pair=XBTUSD&interval=5");

    private static readonly Uri KrakenStreamUrl = new("wss://ws.kraken.com/v2");

    private const string KrakenSubscribe =
        """{"method":"subscribe","params":{"channel":"ohlc","symbol":["BTC/USD"],"interval":5}}""";

    private readonly IMarketTransport transport;
    private readonly TimeProvider time;
    private readonly Func<double> jitter;

    /// <summary>Earliest next REST request per source (Retry-After).</summary>
    private readonly Dictionary<MarketSource, DateTimeOffset> notBefore = [];

    private volatile MarketSource source = MarketSource.Binance;
    private volatile Ticker24h? ticker;
    private long lastDataTicks = long.MinValue;

    /// <param name="jitter">Returns a uniform draw in [0, 1); the only source of randomness.</param>
    public MarketDataClient(IMarketTransport transport, TimeProvider time, Func<double> jitter)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(jitter);
        this.transport = transport;
        this.time = time;
        this.jitter = jitter;
    }

    /// <summary>The source the client currently uses (Binance before the first failover).</summary>
    public MarketSource Source => source;

    public FeedState State
    {
        get
        {
            var last = Interlocked.Read(ref lastDataTicks);
            if (last == long.MinValue)
            {
                return FeedState.Connecting;
            }

            return time.GetUtcNow().UtcTicks - last > StaleAfter.Ticks ? FeedState.Stale : FeedState.Live;
        }
    }

    /// <summary>Candles of Source only (capacity 1000).</summary>
    public CandleBuffer Candles { get; } = new();

    /// <summary>The latest 24 h ticker, or null before the first one.</summary>
    public Ticker24h? Ticker => ticker;

    /// <summary>Runs until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        var failedCycles = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IMarketSocket? socket = null;
            var blocked = false;
            try
            {
                socket = await ConnectCycleAsync(source, cancellationToken);
            }
            catch (SourceBlockedException)
            {
                blocked = true;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
            }

            if (socket is not null)
            {
                attempt = 0;
                failedCycles = 0;
                try
                {
                    await StreamAsync(socket, source, cancellationToken);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // Connection lost: reconnect below.
                }
            }
            else
            {
                failedCycles++;
                if (blocked || failedCycles >= FailedCyclesBeforeSwitch)
                {
                    source = source == MarketSource.Binance ? MarketSource.Kraken : MarketSource.Binance;
                    failedCycles = 0;
                }
            }

            attempt++;
            var delay = ReconnectPolicy.BackoffDelay(attempt, jitter());
            if (notBefore.TryGetValue(source, out var earliest) && earliest - time.GetUtcNow() > delay)
            {
                delay = earliest - time.GetUtcNow();
            }

            await Task.Delay(delay, time, cancellationToken);
        }
    }

    /// <summary>History over REST, then the socket; commits the history only when both succeeded.</summary>
    private async Task<IMarketSocket> ConnectCycleAsync(MarketSource cycleSource, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(CycleTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var ct = linked.Token;

        if (cycleSource == MarketSource.Binance)
        {
            var klines = await GetOkAsync(cycleSource, BinanceKlinesUrl, ct);
            var tickerJson = await GetOkAsync(cycleSource, BinanceTickerUrl, ct);
            var history = BinanceParser.ParseKlines(klines, time.GetUtcNow());
            var restTicker = BinanceParser.ParseTicker24h(tickerJson);
            var socket = await OpenSocketAsync(cycleSource, ct);
            Candles.Reset(cycleSource, history);
            ticker = restTicker;
            MarkData();
            return socket;
        }
        else
        {
            var ohlc = await GetOkAsync(cycleSource, KrakenOhlcUrl, ct);
            var history = KrakenParser.ParseOhlc(ohlc, time.GetUtcNow());
            var socket = await OpenSocketAsync(cycleSource, ct);
            Candles.Reset(cycleSource, history);
            ticker = DeriveTicker();
            MarkData();
            return socket;
        }
    }

    private async Task<IMarketSocket> OpenSocketAsync(MarketSource socketSource, CancellationToken ct)
    {
        var binance = socketSource == MarketSource.Binance;
        var socket = await transport.ConnectAsync(binance ? BinanceStreamUrl : KrakenStreamUrl, ct);
        try
        {
            await socket.SendAsync(binance ? BinanceSubscribe : KrakenSubscribe, ct);
            return socket;
        }
        catch
        {
            await DisposeQuietlyAsync(socket);
            throw;
        }
    }

    private async Task<string> GetOkAsync(MarketSource requestSource, Uri url, CancellationToken ct)
    {
        var result = await transport.GetAsync(url, ct);
        if (result.StatusCode == 200)
        {
            return result.Body;
        }

        if (result.RetryAfter is { } retryAfter)
        {
            notBefore[requestSource] = time.GetUtcNow() + retryAfter;
        }

        if (result.StatusCode == 451)
        {
            throw new SourceBlockedException(requestSource);
        }

        throw new HttpRequestException($"{requestSource} answered HTTP {result.StatusCode} for {url}");
    }

    /// <summary>Reads the socket until it closes, fails or falls silent; replaces it before its 24 h
    /// limit, opening the new socket before closing the old one.</summary>
    private async Task StreamAsync(IMarketSocket socket, MarketSource streamSource, CancellationToken ct)
    {
        var renewAt = time.GetUtcNow() + SocketLifetime;
        var lastMessageAt = time.GetUtcNow();
        var receive = socket.ReceiveAsync(ct);
        try
        {
            while (true)
            {
                var wakeAt = lastMessageAt + SilenceLimit < renewAt ? lastMessageAt + SilenceLimit : renewAt;
                var wait = wakeAt - time.GetUtcNow();
                using (var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    var timer = Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, time, waitCts.Token);
                    var first = await Task.WhenAny(receive, timer);
                    waitCts.Cancel();
                    if (first == receive)
                    {
                        var message = await receive;
                        if (message is null)
                        {
                            return;
                        }

                        lastMessageAt = time.GetUtcNow();
                        Apply(streamSource, message);
                        receive = socket.ReceiveAsync(ct);
                        continue;
                    }
                }

                ct.ThrowIfCancellationRequested();
                var now = time.GetUtcNow();
                if (now >= lastMessageAt + SilenceLimit)
                {
                    return;
                }

                IMarketSocket? replacement = null;
                try
                {
                    replacement = await OpenSocketAsync(streamSource, ct);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                }

                if (replacement is null)
                {
                    renewAt = time.GetUtcNow() + RenewRetry;
                    continue;
                }

                Observe(receive);
                await DisposeQuietlyAsync(socket);
                socket = replacement;
                receive = socket.ReceiveAsync(ct);
                lastMessageAt = time.GetUtcNow();
                renewAt = lastMessageAt + SocketLifetime;
            }
        }
        finally
        {
            Observe(receive);
            await DisposeQuietlyAsync(socket);
        }
    }

    private void Apply(MarketSource messageSource, string message)
    {
        // A message of a socket that no longer belongs to the series is dropped.
        if (messageSource != source || Candles.Source != messageSource)
        {
            return;
        }

        try
        {
            if (messageSource == MarketSource.Binance)
            {
                switch (BinanceParser.ParseStreamMessage(message))
                {
                    case CandleUpdate update:
                        Candles.Upsert(messageSource, update.Candle);
                        MarkData();
                        break;
                    case TickerUpdate update:
                        ticker = update.Ticker;
                        MarkData();
                        break;
                }
            }
            else
            {
                var candles = KrakenParser.ParseWsOhlc(message, time.GetUtcNow());
                foreach (var candle in candles)
                {
                    Candles.Upsert(messageSource, candle);
                }

                if (candles.Count > 0)
                {
                    ticker = DeriveTicker();
                    MarkData();
                }
            }
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException
                                      or KeyNotFoundException or OverflowException)
        {
            // A malformed message is skipped; the stream itself is fine.
        }
    }

    /// <summary>Kraken has no rolling 24 h ticker over WS: it is derived from the last 24 h of candles.</summary>
    private Ticker24h? DeriveTicker()
    {
        var candles = Candles.Snapshot();
        if (candles.Count == 0)
        {
            return null;
        }

        var from = candles[^1].OpenTime - TickerWindow;
        var window = candles.Where(c => c.OpenTime > from).ToList();
        return JsonValues.Ticker(
            window[^1].Close,
            window[0].Open,
            window.Max(c => c.High),
            window.Min(c => c.Low),
            window.Sum(c => c.Volume));
    }

    private void MarkData() => Interlocked.Exchange(ref lastDataTicks, time.GetUtcNow().UtcTicks);

    private static async Task DisposeQuietlyAsync(IMarketSocket socket)
    {
        try
        {
            await socket.DisposeAsync();
        }
        catch (Exception)
        {
            // Closing a broken socket may fail; it is gone either way.
        }
    }

    /// <summary>Marks a pending receive of a dropped socket as observed (it faults when the socket closes).</summary>
    private static void Observe(Task task) =>
        task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private sealed class SourceBlockedException(MarketSource blocked)
        : Exception($"{blocked} refused the request with HTTP 451 (unavailable for legal reasons)");
}
