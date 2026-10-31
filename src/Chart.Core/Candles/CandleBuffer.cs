namespace BitcoinWallpaper.Chart.Core.Candles;

/// <summary>Ring buffer of candles of one source, oldest first, at most Capacity (default 1000).
/// Thread-safe: the feed writes while the renderer takes snapshots.</summary>
public sealed class CandleBuffer
{
    public const int DefaultCapacity = 1000;

    private readonly object gate = new();
    private readonly List<Candle> candles;
    private MarketSource? source;

    public CandleBuffer(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        candles = new List<Candle>(capacity + 1);
    }

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (gate)
            {
                return candles.Count;
            }
        }
    }

    /// <summary>Source of the candles held; null before the first Reset.</summary>
    public MarketSource? Source
    {
        get
        {
            lock (gate)
            {
                return source;
            }
        }
    }

    /// <summary>A copy of the candles, oldest first, strictly increasing OpenTime.</summary>
    public IReadOnlyList<Candle> Snapshot()
    {
        lock (gate)
        {
            return candles.ToArray();
        }
    }

    /// <summary>Clears everything and loads history of the given source (any order, duplicates allowed);
    /// keeps the newest Capacity candles.</summary>
    public void Reset(MarketSource source, IEnumerable<Candle> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        // A later duplicate wins: the newer copy of a candle is the more complete one.
        var byTime = new SortedDictionary<DateTimeOffset, Candle>();
        foreach (var candle in history)
        {
            byTime[candle.OpenTime] = candle;
        }

        var newest = byTime.Values.Skip(Math.Max(0, byTime.Count - Capacity)).ToList();
        lock (gate)
        {
            this.source = source;
            candles.Clear();
            candles.AddRange(newest);
        }
    }

    /// <summary>Same OpenTime as a held candle: replaces it in place. Newer than the last: appends,
    /// evicting the oldest beyond Capacity. A source other than Source: InvalidOperationException,
    /// buffer unchanged.</summary>
    public void Upsert(MarketSource source, Candle candle)
    {
        ArgumentNullException.ThrowIfNull(candle);
        lock (gate)
        {
            if (this.source != source)
            {
                throw new InvalidOperationException(
                    $"The buffer holds {this.source?.ToString() ?? "no"} candles; a {source} candle cannot be mixed in.");
            }

            if (candles.Count == 0 || candle.OpenTime > candles[^1].OpenTime)
            {
                candles.Add(candle);
            }
            else
            {
                var index = IndexOf(candle.OpenTime);
                if (index >= 0)
                {
                    candles[index] = candle;
                }
                else
                {
                    // An older candle filling a gap goes to its place in time order.
                    candles.Insert(~index, candle);
                }
            }

            if (candles.Count > Capacity)
            {
                candles.RemoveRange(0, candles.Count - Capacity);
            }
        }
    }

    private int IndexOf(DateTimeOffset openTime)
    {
        int low = 0, high = candles.Count - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var cmp = candles[mid].OpenTime.CompareTo(openTime);
            if (cmp == 0)
            {
                return mid;
            }

            if (cmp < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return ~low;
    }
}
