using BitcoinWallpaper.Chart.Core.Candles;

namespace BitcoinWallpaper.Chart.Core.Tests;

// Contract #3, criterion: CandleBuffer keeps at most 1000 candles in time order, replaces the forming
// candle in place, never mixes sources (a source switch clears and reloads).
// API used (see PendingApi.cs): Candle, MarketSource, CandleBuffer { DefaultCapacity, Capacity, Count,
// Source, Snapshot(), Reset(source, history), Upsert(source, candle) }.
public class CandleBufferTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);

    private static Candle C(int index, decimal close = 77000m, bool closed = true) =>
        new(Origin.AddMinutes(5 * index), close - 5m, close + 10m, close - 10m, close, 1.5m + index, closed);

    private static List<Candle> Range(int from, int count, decimal close = 77000m) =>
        Enumerable.Range(from, count).Select(i => C(i, close + i)).ToList();

    private static void AssertStrictTimeOrder(IReadOnlyList<Candle> candles)
    {
        for (var i = 1; i < candles.Count; i++)
        {
            Assert.True(candles[i - 1].OpenTime < candles[i].OpenTime,
                $"candle {i} ({candles[i].OpenTime:O}) is not after candle {i - 1} ({candles[i - 1].OpenTime:O})");
        }
    }

    [Fact]
    public void Default_capacity_is_1000()
    {
        Assert.Equal(1000, CandleBuffer.DefaultCapacity);
        Assert.Equal(1000, new CandleBuffer().Capacity);
    }

    [Fact]
    public void Reset_loads_history_in_time_order()
    {
        var history = Range(0, 10);
        var shuffled = new[] { 7, 2, 9, 0, 4, 1, 8, 3, 6, 5 }.Select(i => history[i]).ToList();
        var buffer = new CandleBuffer();

        buffer.Reset(MarketSource.Binance, shuffled);

        Assert.Equal(10, buffer.Count);
        Assert.Equal(MarketSource.Binance, buffer.Source);
        Assert.Equal(history, buffer.Snapshot());
    }

    [Fact]
    public void Reset_with_duplicate_open_times_keeps_one_candle_per_time()
    {
        var history = Range(0, 5);
        history.Add(C(2, close: 1m));
        var buffer = new CandleBuffer();

        buffer.Reset(MarketSource.Binance, history);

        var snapshot = buffer.Snapshot();
        Assert.Equal(5, snapshot.Count);
        AssertStrictTimeOrder(snapshot);
    }

    [Fact]
    public void Reset_with_more_than_1000_keeps_the_newest_1000()
    {
        var history = Range(0, 1500);
        var buffer = new CandleBuffer();

        buffer.Reset(MarketSource.Binance, history);

        var snapshot = buffer.Snapshot();
        Assert.Equal(1000, buffer.Count);
        Assert.Equal(1000, snapshot.Count);
        Assert.Equal(history[500], snapshot[0]);
        Assert.Equal(history[1499], snapshot[^1]);
        AssertStrictTimeOrder(snapshot);
    }

    [Fact]
    public void Upsert_of_a_newer_candle_appends_it()
    {
        var buffer = new CandleBuffer();
        buffer.Reset(MarketSource.Binance, Range(0, 3));
        var next = C(3, closed: false);

        buffer.Upsert(MarketSource.Binance, next);

        var snapshot = buffer.Snapshot();
        Assert.Equal(4, buffer.Count);
        Assert.Equal(next, snapshot[^1]);
        AssertStrictTimeOrder(snapshot);
    }

    [Fact]
    public void Upsert_with_the_open_time_of_the_forming_candle_replaces_it_in_place()
    {
        var history = Range(0, 4);
        history.Add(C(4, close: 77100m, closed: false));
        var buffer = new CandleBuffer();
        buffer.Reset(MarketSource.Binance, history);
        var update = C(4, close: 77123.45m, closed: false);

        buffer.Upsert(MarketSource.Binance, update);

        var snapshot = buffer.Snapshot();
        Assert.Equal(5, buffer.Count);
        Assert.Equal(update, snapshot[^1]);
        Assert.Equal(history.Take(4), snapshot.Take(4));
    }

    [Fact]
    public void Closing_the_forming_candle_then_the_next_candle_starts_a_new_one()
    {
        var history = Range(0, 4);
        history.Add(C(4, close: 77100m, closed: false));
        var buffer = new CandleBuffer();
        buffer.Reset(MarketSource.Binance, history);
        var closed = C(4, close: 77111m, closed: true);
        var next = C(5, close: 77112m, closed: false);

        buffer.Upsert(MarketSource.Binance, closed);
        buffer.Upsert(MarketSource.Binance, next);

        var snapshot = buffer.Snapshot();
        Assert.Equal(6, snapshot.Count);
        Assert.Equal(closed, snapshot[4]);
        Assert.Equal(next, snapshot[5]);
        AssertStrictTimeOrder(snapshot);
    }

    [Fact]
    public void Appending_to_a_full_buffer_evicts_the_oldest()
    {
        var history = Range(0, 1000);
        var buffer = new CandleBuffer();
        buffer.Reset(MarketSource.Binance, history);
        var next = C(1000, closed: false);

        buffer.Upsert(MarketSource.Binance, next);

        var snapshot = buffer.Snapshot();
        Assert.Equal(1000, buffer.Count);
        Assert.Equal(history[1], snapshot[0]);
        Assert.Equal(next, snapshot[^1]);
        AssertStrictTimeOrder(snapshot);
    }

    [Fact]
    public void Count_never_exceeds_1000_over_a_long_live_session()
    {
        var buffer = new CandleBuffer();
        buffer.Reset(MarketSource.Binance, Range(0, 10));

        for (var i = 10; i < 2600; i++)
        {
            buffer.Upsert(MarketSource.Binance, C(i, closed: false));
            buffer.Upsert(MarketSource.Binance, C(i, close: 77001m, closed: true));
            Assert.True(buffer.Count <= 1000, $"count {buffer.Count} after candle {i}");
        }

        var snapshot = buffer.Snapshot();
        Assert.Equal(1000, snapshot.Count);
        Assert.Equal(Origin.AddMinutes(5 * 1600), snapshot[0].OpenTime);
        Assert.Equal(Origin.AddMinutes(5 * 2599), snapshot[^1].OpenTime);
        AssertStrictTimeOrder(snapshot);
    }

    [Fact]
    public void An_out_of_order_older_candle_never_breaks_time_order()
    {
        var history = Range(0, 3).Concat(Range(5, 3)).ToList(); // gap at 3 and 4
        var buffer = new CandleBuffer();
        buffer.Reset(MarketSource.Binance, history);

        buffer.Upsert(MarketSource.Binance, C(4, close: 1m)); // older than the last, not held
        buffer.Upsert(MarketSource.Binance, C(1, close: 2m)); // older than the last, held

        var snapshot = buffer.Snapshot();
        Assert.InRange(snapshot.Count, 6, 7);
        AssertStrictTimeOrder(snapshot);
        Assert.Equal(history[^1], snapshot[^1]);
    }

    [Fact]
    public void Upsert_from_another_source_is_refused_and_changes_nothing()
    {
        var history = Range(0, 10);
        var buffer = new CandleBuffer();
        buffer.Reset(MarketSource.Binance, history);

        Assert.Throws<InvalidOperationException>(() => buffer.Upsert(MarketSource.Kraken, C(10, close: 1m)));
        Assert.Throws<InvalidOperationException>(() => buffer.Upsert(MarketSource.Kraken, C(9, close: 1m)));

        Assert.Equal(MarketSource.Binance, buffer.Source);
        Assert.Equal(history, buffer.Snapshot());
    }

    [Fact]
    public void A_source_switch_clears_and_reloads()
    {
        var binance = Range(0, 10, close: 77400m);
        // Kraken history overlaps the Binance times and also starts earlier: nothing of Binance may survive.
        var kraken = Range(-3, 8, close: 77200m);
        var buffer = new CandleBuffer();
        buffer.Reset(MarketSource.Binance, binance);

        buffer.Reset(MarketSource.Kraken, kraken);

        Assert.Equal(MarketSource.Kraken, buffer.Source);
        Assert.Equal(8, buffer.Count);
        Assert.Equal(kraken, buffer.Snapshot());

        buffer.Upsert(MarketSource.Kraken, C(5, close: 77211m, closed: false));
        Assert.Equal(9, buffer.Count);
        Assert.Throws<InvalidOperationException>(() => buffer.Upsert(MarketSource.Binance, C(6, close: 1m)));
        Assert.DoesNotContain(buffer.Snapshot(), c => binance.Contains(c));
    }
}
