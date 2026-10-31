// PENDING API (#3). Stubs only, so the red tests compile and fail with NotImplementedException.
// The worker implements exactly these public types and signatures (Candle, MarketSource, CandleBuffer in
// src/Chart.Core/Candles/; everything else in src/Market.Data/) and then DELETES this file and
// tests/Chart.Core.Tests/PendingApi.cs (the only test edit allowed; while a stub exists next to the real
// type the build fails with CS0436, so it cannot be forgotten). Nothing here is behaviour the tests rely on.
//
// Rules the tests rely on (see the comments on each type):
// - Parsers are pure, culture-invariant, decimal-exact; times are UTC (Offset == 0).
// - MarketDataClient never touches the network itself: every request goes through IMarketTransport, every
//   wait/timer through the TimeProvider (Task.Delay(delay, time, ct), time.CreateTimer, PeriodicTimer with
//   the TimeProvider, CancellationTokenSource(delay, time)), every random draw through the jitter function.
//   It uses no Task.Run, no thread-pool hops of its own, no real timers: under test it runs on a
//   single-threaded SynchronizationContext with a fake TimeProvider and a fake transport.
// - Binance: REST host data-api.binance.vision (/api/v3/klines?symbol=BTCUSDT&interval=5m&limit=1000,
//   /api/v3/ticker/24hr?symbol=BTCUSDT); ONE WebSocket on data-stream.binance.vision delivering the raw
//   (not combined-envelope) payloads of btcusdt@kline_5m and btcusdt@miniTicker.
//   Kraken: REST host api.kraken.com (/0/public/OHLC?pair=XBTUSD&interval=5); WebSocket ws.kraken.com/v2,
//   channel ohlc, symbol BTC/USD, interval 5.
namespace BitcoinWallpaper.Chart.Core.Candles
{
    public enum MarketSource
    {
        Binance,
        Kraken,
    }

    public sealed record Candle(
        DateTimeOffset OpenTime,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close,
        decimal Volume,
        bool IsClosed);

    public sealed class CandleBuffer
    {
        public const int DefaultCapacity = 1000;

        public CandleBuffer(int capacity = DefaultCapacity) => throw new NotImplementedException();

        public int Capacity => throw new NotImplementedException();

        public int Count => throw new NotImplementedException();

        public MarketSource? Source => throw new NotImplementedException();

        public IReadOnlyList<Candle> Snapshot() => throw new NotImplementedException();

        public void Reset(MarketSource source, IEnumerable<Candle> history) => throw new NotImplementedException();

        public void Upsert(MarketSource source, Candle candle) => throw new NotImplementedException();
    }
}

namespace BitcoinWallpaper.Market.Data
{
    using BitcoinWallpaper.Chart.Core.Candles;

    /// <summary>24 h ticker. ChangeAbs = Last - Open; ChangePercent = ChangeAbs / Open * 100.
    /// Volume is the base-asset (BTC) volume.</summary>
    public sealed record Ticker24h(
        decimal Last,
        decimal Open,
        decimal High,
        decimal Low,
        decimal ChangeAbs,
        decimal ChangePercent,
        decimal Volume);

    /// <summary>A parsed stream message.</summary>
    public abstract record MarketEvent;

    public sealed record CandleUpdate(Candle Candle) : MarketEvent;

    public sealed record TickerUpdate(Ticker24h Ticker) : MarketEvent;

    /// <summary>Connecting: no market data received yet. Live: data within the last 30 s.
    /// Stale: more than 30 s since the last market data (REST or stream).</summary>
    public enum FeedState
    {
        Connecting,
        Live,
        Stale,
    }

    /// <summary>An HTTP answer of any status. RetryAfter is the Retry-After header as a delay, if present.</summary>
    public sealed record HttpResult(int StatusCode, string Body, TimeSpan? RetryAfter = null);

    /// <summary>Network seam. Network failures throw (HttpRequestException, WebSocketException, IOException);
    /// any HTTP status is returned as an HttpResult.</summary>
    public interface IMarketTransport
    {
        Task<HttpResult> GetAsync(Uri url, CancellationToken cancellationToken);

        Task<IMarketSocket> ConnectAsync(Uri url, CancellationToken cancellationToken);
    }

    /// <summary>A connected WebSocket carrying whole text messages.</summary>
    public interface IMarketSocket : IAsyncDisposable
    {
        Task SendAsync(string text, CancellationToken cancellationToken);

        /// <summary>The next whole text message; null when the server closed the socket.</summary>
        Task<string?> ReceiveAsync(CancellationToken cancellationToken);
    }

    /// <summary>Jittered exponential backoff between reconnect attempts.</summary>
    public static class ReconnectPolicy
    {
        /// <summary>1 s.</summary>
        public static TimeSpan MinDelay => throw new NotImplementedException();

        /// <summary>60 s.</summary>
        public static TimeSpan MaxDelay => throw new NotImplementedException();

        /// <summary>Delay before attempt number <paramref name="attempt"/> (1 = first retry) for a jitter
        /// draw in [0, 1). Always within [MinDelay, MaxDelay].</summary>
        public static TimeSpan BackoffDelay(int attempt, double jitter) => throw new NotImplementedException();
    }

    /// <summary>The live feed: Binance first, Kraken after 3 consecutive failed connect cycles or an HTTP 451
    /// at start. A connect cycle = history over REST + the WebSocket; it fails when either fails before the
    /// feed is up. Backoff between cycles per ReconnectPolicy (it keeps growing while every source fails),
    /// Retry-After honoured on 429, proactive reconnect before 24 h of socket life (new socket opened before
    /// the old one is closed), State exposes staleness.</summary>
    public sealed class MarketDataClient
    {
        /// <param name="jitter">Returns a uniform draw in [0, 1); the only source of randomness.</param>
        public MarketDataClient(IMarketTransport transport, TimeProvider time, Func<double> jitter) =>
            throw new NotImplementedException();

        /// <summary>The source the client currently uses (Binance before the first failover).</summary>
        public MarketSource Source => throw new NotImplementedException();

        public FeedState State => throw new NotImplementedException();

        /// <summary>Candles of Source only (capacity 1000).</summary>
        public CandleBuffer Candles => throw new NotImplementedException();

        /// <summary>The latest 24 h ticker, or null before the first one.</summary>
        public Ticker24h? Ticker => throw new NotImplementedException();

        /// <summary>Runs until cancelled.</summary>
        public Task RunAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}

namespace BitcoinWallpaper.Market.Data.Binance
{
    using BitcoinWallpaper.Chart.Core.Candles;

    public static class BinanceParser
    {
        /// <summary>REST /api/v3/klines. IsClosed = the kline's close time is before <paramref name="now"/>.</summary>
        public static IReadOnlyList<Candle> ParseKlines(string json, DateTimeOffset now) =>
            throw new NotImplementedException();

        /// <summary>REST /api/v3/ticker/24hr.</summary>
        public static Ticker24h ParseTicker24h(string json) => throw new NotImplementedException();

        /// <summary>A raw stream payload: kline -> CandleUpdate (IsClosed = k.x), 24hrMiniTicker -> TickerUpdate;
        /// anything else (subscribe replies, other events) -> null.</summary>
        public static MarketEvent? ParseStreamMessage(string json) => throw new NotImplementedException();
    }
}

namespace BitcoinWallpaper.Market.Data.Kraken
{
    using BitcoinWallpaper.Chart.Core.Candles;

    public static class KrakenParser
    {
        /// <summary>REST /0/public/OHLC (row: time, open, high, low, close, vwap, volume, count).
        /// IsClosed = open time + 5 min is not after <paramref name="now"/>.</summary>
        public static IReadOnlyList<Candle> ParseOhlc(string json, DateTimeOffset now) =>
            throw new NotImplementedException();

        /// <summary>WS v2 message. Channel ohlc (snapshot or update) -> its candles, OpenTime = interval_begin,
        /// IsClosed = interval_begin + interval is not after <paramref name="now"/>; any other message -> empty.</summary>
        public static IReadOnlyList<Candle> ParseWsOhlc(string json, DateTimeOffset now) =>
            throw new NotImplementedException();
    }
}
