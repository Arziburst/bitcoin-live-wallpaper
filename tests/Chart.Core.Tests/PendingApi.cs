// PENDING API (#3). Stubs only, so the red tests compile and fail with NotImplementedException.
// The worker implements exactly these public types and signatures in src/Chart.Core/Candles/ and then
// DELETES this file (the only test edit allowed; while it exists next to the real types the build fails
// with CS0436, so it cannot be forgotten). Nothing here is behaviour the tests rely on.
namespace BitcoinWallpaper.Chart.Core.Candles;

/// <summary>Which exchange a series comes from. A buffer never holds candles of two sources.</summary>
public enum MarketSource
{
    Binance,
    Kraken,
}

/// <summary>One candle. OpenTime is UTC (Offset == 0). IsClosed is false for the forming candle.</summary>
public sealed record Candle(
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    bool IsClosed);

/// <summary>Ring buffer of candles of one source, oldest first, at most Capacity (default 1000).</summary>
public sealed class CandleBuffer
{
    public const int DefaultCapacity = 1000;

    public CandleBuffer(int capacity = DefaultCapacity) => throw new NotImplementedException();

    public int Capacity => throw new NotImplementedException();

    public int Count => throw new NotImplementedException();

    /// <summary>Source of the candles held; null before the first Reset.</summary>
    public MarketSource? Source => throw new NotImplementedException();

    /// <summary>A copy of the candles, oldest first, strictly increasing OpenTime.</summary>
    public IReadOnlyList<Candle> Snapshot() => throw new NotImplementedException();

    /// <summary>Clears everything and loads history of the given source (any order, duplicates allowed);
    /// keeps the newest Capacity candles.</summary>
    public void Reset(MarketSource source, IEnumerable<Candle> history) => throw new NotImplementedException();

    /// <summary>Same OpenTime as a held candle: replaces it in place. Newer than the last: appends,
    /// evicting the oldest beyond Capacity. A source other than Source: InvalidOperationException,
    /// buffer unchanged.</summary>
    public void Upsert(MarketSource source, Candle candle) => throw new NotImplementedException();
}
