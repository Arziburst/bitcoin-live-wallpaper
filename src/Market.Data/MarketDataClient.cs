using System.Globalization;
using System.Text.Json;
using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Market.Data.Binance;
using BitcoinWallpaper.Market.Data.Kraken;

namespace BitcoinWallpaper.Market.Data;

/// <summary>The live feed: Binance first, Kraken after 3 consecutive failed connect cycles or an HTTP 451
/// at start. A connect cycle = history over REST + the WebSocket; it fails when either fails before the
/// feed is up. Backoff between cycles per ReconnectPolicy (it keeps growing while every source fails),
/// Retry-After honoured on 429, proactive reconnect before 24 h of socket life (new socket opened before
/// the old one is closed), State exposes staleness.
/// While on Kraken after a failover, Binance is tried every 30 min (+-10 %): a successful try switches back
/// with a full history reload, a failed one leaves the Kraken feed alone and is not a Kraken failure.
/// 3 cycles in a row whose REST answered but whose socket was refused start REST polling of the same
/// source every 15 s (Mode RestPoll), with a socket retry every 2 min that ends it.</summary>
/// <remarks>Every wait goes through the TimeProvider and every random draw through the jitter function;
/// the client starts no threads of its own, so it runs deterministically on a single-threaded context.</remarks>
public sealed class MarketDataClient
{
    /// <summary>Consecutive failed connect cycles after which the other source is tried.</summary>
    private const int FailedCyclesBeforeSwitch = 3;

    /// <summary>Cycles in a row with REST up and the socket refused after which REST is polled.</summary>
    private const int RefusedSocketsBeforePolling = 3;

    /// <summary>Polls in a row without an answer after which polling gives way to connect cycles again.</summary>
    private const int FailedPollsBeforeCycles = 3;

    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    /// <summary>A socket silent this long is treated as dead (Binance pushes every 1-2 s, Kraken sends
    /// heartbeats every second).</summary>
    private static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(60);

    /// <summary>Binance closes a socket after 24 h; it is replaced well before that.</summary>
    private static readonly TimeSpan SocketLifetime = TimeSpan.FromHours(23);

    private static readonly TimeSpan RenewRetry = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan CycleTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan TickerWindow = TimeSpan.FromHours(24);

    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan SocketRetryEvery = TimeSpan.FromMinutes(2);

    /// <summary>While on Kraken after a failover, Binance is tried this often (+-10 % jitter).</summary>
    private static readonly TimeSpan PrimaryTryEvery = TimeSpan.FromMinutes(30);

    private const string BinanceKlines =
        "https://data-api.binance.vision/api/v3/klines?symbol=BTCUSDT&interval=5m";

    private static readonly Uri BinanceKlinesUrl = new(BinanceKlines + "&limit=1000");

    /// <summary>A poll needs only the newest candle and the one before it.</summary>
    private static readonly Uri BinancePollUrl = new(BinanceKlines + "&limit=2");

    private static readonly Uri BinanceTickerUrl =
        new("https://data-api.binance.vision/api/v3/ticker/24hr?symbol=BTCUSDT");

    private static readonly Uri BinanceStreamUrl = new("wss://data-stream.binance.vision/ws");

    private const string BinanceSubscribe =
        """{"method":"SUBSCRIBE","params":["btcusdt@kline_5m","btcusdt@miniTicker"],"id":1}""";

    private const string KrakenOhlc = "https://api.kraken.com/0/public/OHLC?pair=XBTUSD&interval=5";

    private static readonly Uri KrakenOhlcUrl = new(KrakenOhlc);

    private static readonly Uri KrakenTickerUrl = new("https://api.kraken.com/0/public/Ticker?pair=XBTUSD");

    private static readonly Uri KrakenStreamUrl = new("wss://ws.kraken.com/v2");

    private const string KrakenSubscribe =
        """{"method":"subscribe","params":{"channel":"ohlc","symbol":["BTC/USD"],"interval":5}}""";

    private readonly IMarketTransport transport;
    private readonly TimeProvider time;
    private readonly Func<double> jitter;

    /// <summary>Earliest next REST request per source (Retry-After).</summary>
    private readonly Dictionary<MarketSource, DateTimeOffset> notBefore = [];

    private volatile MarketSource source = MarketSource.Binance;
    private volatile FeedMode mode = FeedMode.WebSocket;
    private volatile Ticker24h? ticker;
    private long lastDataTicks = long.MinValue;

    /// <summary>When Binance is tried next; set while on Kraken after a failover, null on Binance.</summary>
    private DateTimeOffset? primaryTryAt;

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

    /// <summary>How the data of Source arrives: over its WebSocket, or by REST polling while the socket
    /// is refused.</summary>
    public FeedMode Mode => mode;

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
        var refusedSockets = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            History? history = null;
            IMarketSocket? socket = null;
            var blocked = false;
            try
            {
                (history, socket) = await ConnectCycleAsync(source, cancellationToken);
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
                Commit(source, history!);
            }
            else if (history is not null)
            {
                // REST answered, the socket was refused: no reason to leave the source.
                failedCycles = 0;
                refusedSockets++;
                if (refusedSockets >= RefusedSocketsBeforePolling)
                {
                    refusedSockets = 0;
                    Commit(source, history);
                    socket = await PollAsync(cancellationToken);
                }
            }

            if (socket is not null)
            {
                attempt = 0;
                failedCycles = 0;
                refusedSockets = 0;
                try
                {
                    await StreamAsync(socket, source, cancellationToken);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // Connection lost: reconnect below.
                }
            }
            else if (history is null)
            {
                refusedSockets = 0;
                failedCycles++;
                if (blocked || failedCycles >= FailedCyclesBeforeSwitch)
                {
                    SwitchSource(source == MarketSource.Binance ? MarketSource.Kraken : MarketSource.Binance);
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

    private void SwitchSource(MarketSource to)
    {
        source = to;
        primaryTryAt = to == MarketSource.Kraken ? time.GetUtcNow() + PrimaryTryDelay() : null;
    }

    /// <summary>30 min scaled to [90 %, 110 %) by a jitter draw.</summary>
    private TimeSpan PrimaryTryDelay()
    {
        var draw = jitter();
        draw = double.IsFinite(draw) ? Math.Clamp(draw, 0.0, 1.0) : 0.5;
        return PrimaryTryEvery * (0.9 + (0.2 * draw));
    }

    /// <summary>History over REST, then the socket. Throws when REST fails; a refused socket comes back as
    /// a null socket with the fetched history. Nothing is committed here.</summary>
    private async Task<(History History, IMarketSocket? Socket)> ConnectCycleAsync(
        MarketSource cycleSource, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(CycleTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var ct = linked.Token;

        History history;
        if (cycleSource == MarketSource.Binance)
        {
            var klines = await GetOkAsync(cycleSource, BinanceKlinesUrl, ct);
            var tickerJson = await GetOkAsync(cycleSource, BinanceTickerUrl, ct);
            history = new History(
                BinanceParser.ParseKlines(klines, time.GetUtcNow()),
                BinanceParser.ParseTicker24h(tickerJson));
        }
        else
        {
            var ohlc = await GetOkAsync(cycleSource, KrakenOhlcUrl, ct);
            history = new History(KrakenParser.ParseOhlc(ohlc, time.GetUtcNow()), null);
        }

        try
        {
            return (history, await OpenSocketAsync(cycleSource, ct));
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return (history, null);
        }
    }

    /// <summary>Replaces the series with the history of <paramref name="historySource"/>.</summary>
    private void Commit(MarketSource historySource, History history)
    {
        Candles.Reset(historySource, history.Candles);
        ticker = history.Ticker ?? DeriveTicker();
        MarkData();
    }

    /// <summary>One Binance connect cycle while on Kraken. Success: switches to Binance with its full
    /// history and returns its socket. Failure: null, with the Kraken series and failure counts untouched.</summary>
    private async Task<IMarketSocket?> TryPrimaryAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (notBefore.TryGetValue(MarketSource.Binance, out var earliest) && earliest > now)
        {
            primaryTryAt = earliest;
            return null;
        }

        primaryTryAt = now + PrimaryTryDelay();
        try
        {
            var (history, socket) = await ConnectCycleAsync(MarketSource.Binance, ct);
            if (socket is null)
            {
                return null;
            }

            SwitchSource(MarketSource.Binance);
            Commit(MarketSource.Binance, history);
            return socket;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Polls the REST of Source every 15 s while its socket is refused; a socket retry every 2 min
    /// ends it. Returns the socket to stream (Source's, or Binance's after a successful primary try), or
    /// null when the polls stopped answering.</summary>
    private async Task<IMarketSocket?> PollAsync(CancellationToken ct)
    {
        mode = FeedMode.RestPoll;
        try
        {
            var nextPoll = time.GetUtcNow() + PollEvery;
            var nextRetry = time.GetUtcNow() + SocketRetryEvery;
            var failedPolls = 0;
            while (true)
            {
                var wakeAt = nextPoll < nextRetry ? nextPoll : nextRetry;
                if (source == MarketSource.Kraken && primaryTryAt is { } tryAt && tryAt < wakeAt)
                {
                    wakeAt = tryAt;
                }

                var wait = wakeAt - time.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, time, ct);
                }

                if (source == MarketSource.Kraken && primaryTryAt <= time.GetUtcNow())
                {
                    var primary = await TryPrimaryAsync(ct);
                    if (primary is not null)
                    {
                        return primary;
                    }
                }

                if (nextRetry <= time.GetUtcNow())
                {
                    nextRetry += SocketRetryEvery;
                    try
                    {
                        using var timeout = new CancellationTokenSource(CycleTimeout, time);
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                        return await OpenSocketAsync(source, linked.Token);
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        // Still refused: keep polling.
                    }

                    if (nextRetry <= time.GetUtcNow())
                    {
                        nextRetry = time.GetUtcNow() + SocketRetryEvery;
                    }
                }

                if (nextPoll <= time.GetUtcNow())
                {
                    nextPoll += PollEvery;
                    if (await PollOnceAsync(source, ct))
                    {
                        failedPolls = 0;
                    }
                    else if (++failedPolls >= FailedPollsBeforeCycles)
                    {
                        return null;
                    }

                    var now = time.GetUtcNow();
                    if (nextPoll <= now)
                    {
                        nextPoll = now + PollEvery;
                    }

                    if (notBefore.TryGetValue(source, out var earliest) && earliest > nextPoll)
                    {
                        nextPoll = earliest;
                    }
                }
            }
        }
        finally
        {
            mode = FeedMode.WebSocket;
        }
    }

    /// <summary>One poll: the newest candles and the 24 h ticker into the series. False when it failed.</summary>
    private async Task<bool> PollOnceAsync(MarketSource pollSource, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(CycleTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var ct = linked.Token;
        try
        {
            if (pollSource == MarketSource.Binance)
            {
                var klines = await GetOkAsync(pollSource, BinancePollUrl, ct);
                var tickerJson = await GetOkAsync(pollSource, BinanceTickerUrl, ct);
                var candles = BinanceParser.ParseKlines(klines, time.GetUtcNow());
                var restTicker = BinanceParser.ParseTicker24h(tickerJson);
                foreach (var candle in candles)
                {
                    Candles.Upsert(pollSource, candle);
                }

                ticker = restTicker;
            }
            else
            {
                var ohlc = await GetOkAsync(pollSource, KrakenPollUrl(), ct);
                var tickerJson = await GetOkAsync(pollSource, KrakenTickerUrl, ct);
                var candles = KrakenParser.ParseOhlc(ohlc, time.GetUtcNow());
                foreach (var candle in candles)
                {
                    Candles.Upsert(pollSource, candle);
                }

                // Kraken's ticker "o" is today's open, not the open 24 h ago: that one comes from the candles.
                var derived = DeriveTicker();
                ticker = derived is null ? null : KrakenParser.ParseTicker24h(tickerJson, derived.Open);
            }

            MarkData();
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>OHLC from the second newest held candle on (Kraken's "since"); the whole history when
    /// fewer are held.</summary>
    private Uri KrakenPollUrl()
    {
        var candles = Candles.Snapshot();
        return candles.Count < 2
            ? KrakenOhlcUrl
            : new Uri(KrakenOhlc + "&since=" + candles[^2].OpenTime.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
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
    /// <remarks>On Kraken after a failover it also runs the Binance tries; a successful one replaces the
    /// Kraken socket with the Binance one.</remarks>
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
                if (streamSource == MarketSource.Kraken && primaryTryAt is { } tryAt && tryAt < wakeAt)
                {
                    wakeAt = tryAt;
                }

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

                if (streamSource == MarketSource.Kraken && primaryTryAt <= now)
                {
                    var primary = await TryPrimaryAsync(ct);
                    if (primary is not null)
                    {
                        Observe(receive);
                        await DisposeQuietlyAsync(socket);
                        socket = primary;
                        streamSource = MarketSource.Binance;
                        receive = socket.ReceiveAsync(ct);
                        lastMessageAt = time.GetUtcNow();
                        renewAt = lastMessageAt + SocketLifetime;
                    }

                    continue;
                }

                if (now < renewAt)
                {
                    continue;
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

    /// <summary>A connect cycle's REST history; Ticker null when it is derived from the candles (Kraken).</summary>
    private sealed record History(IReadOnlyList<Candle> Candles, Ticker24h? Ticker);

    private sealed class SourceBlockedException(MarketSource blocked)
        : Exception($"{blocked} refused the request with HTTP 451 (unavailable for legal reasons)");
}
