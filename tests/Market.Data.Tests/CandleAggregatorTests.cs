// Contract #18, criterion 2: Kraken does not offer 6-hour OHLC (its intervals are 1, 5, 15, 30, 60, 240, 1440,
// 10080, 21600 minutes), so 6h candles are aggregated from 60-minute ones - correctly: O/H/L/C/V, aligned to
// 00/06/12/18 UTC, the forming 6h candle from the partial hours.
//
// API SKETCH (written before the code, #237). In src/Market.Data/CandleAggregator.cs, namespace
// BitcoinWallpaper.Market.Data. Pure.
//
//   public static class CandleAggregator
//   {
//       // Groups source candles (ascending OpenTime, one finer interval that divides `interval`) into buckets
//       // aligned to multiples of `interval` since the Unix epoch (6h: 00/06/12/18 UTC). Per bucket, one candle:
//       // OpenTime = bucket start (UTC), Open = the first source candle's Open, High = max High, Low = min Low,
//       // Close = the last source candle's Close, Volume = sum of Volume, IsClosed = bucket start + interval <= now.
//       // A leading bucket whose first source candle does not open at the bucket start is incomplete and is
//       // dropped; later buckets with a missing source candle are kept with what they have. Result ascending.
//       // interval <= 0: ArgumentOutOfRangeException.
//       public static IReadOnlyList<Candle> Aggregate(IReadOnlyList<Candle> candles, TimeSpan interval, DateTimeOffset now);
//   }

using BitcoinWallpaper.Chart.Core.Candles;
using BitcoinWallpaper.Market.Data.Tests.Fakes;

namespace BitcoinWallpaper.Market.Data.Tests;

public class CandleAggregatorTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan SixHours = TimeSpan.FromHours(6);
    private static readonly DateTimeOffset Day = Fixtures.Utc("2026-10-30T00:00:00Z");

    private static Candle H(int hour, decimal o, decimal h, decimal l, decimal c, decimal v, bool closed = true) =>
        new(Day.AddHours(hour), o, h, l, c, v, closed);

    [Fact]
    public void Six_hours_make_one_candle_with_first_open_max_high_min_low_last_close_and_summed_volume()
    {
        Candle[] hours =
        [
            H(0, 100m, 110m, 95m, 105m, 1.5m),
            H(1, 105m, 130m, 104m, 120m, 2.25m),
            H(2, 120m, 121m, 90m, 92m, 0.125m),
            H(3, 92m, 99m, 91m, 98m, 3m),
            H(4, 98m, 101m, 97m, 100m, 1m),
            H(5, 100m, 102m, 96m, 97m, 0.5m),
        ];

        var result = CandleAggregator.Aggregate(hours, SixHours, Day.AddHours(7));

        var c = Assert.Single(result);
        Assert.Equal(new Candle(Day, 100m, 130m, 90m, 97m, 8.375m, IsClosed: true), c);
    }

    [Fact]
    public void Buckets_are_aligned_to_00_06_12_18_utc()
    {
        var hours = Enumerable.Range(0, 24).Select(i => IntervalData.Source(Day.AddHours(i), Hour)).ToList();

        var result = CandleAggregator.Aggregate(hours, SixHours, Day.AddDays(2));

        Assert.Equal([Day, Day.AddHours(6), Day.AddHours(12), Day.AddHours(18)], result.Select(c => c.OpenTime).ToArray());
        Assert.All(result, c => Assert.Equal(TimeSpan.Zero, c.OpenTime.Offset));
        Assert.Equal(IntervalData.Aggregate(hours, SixHours, Day.AddDays(2)), result);
    }

    [Fact]
    public void The_forming_candle_is_made_of_the_partial_hours_and_is_not_closed()
    {
        // 12:00..14:00 so far, 14:00 still forming; now 14:20.
        Candle[] hours =
        [
            H(6, 200m, 210m, 199m, 205m, 1m),
            H(7, 205m, 206m, 200m, 201m, 1m),
            H(8, 201m, 202m, 198m, 199m, 1m),
            H(9, 199m, 204m, 197m, 203m, 1m),
            H(10, 203m, 208m, 202m, 207m, 1m),
            H(11, 207m, 209m, 206m, 208m, 1m),
            H(12, 208m, 215m, 207m, 214m, 2m),
            H(13, 214m, 216m, 211m, 212m, 3m),
            H(14, 212m, 213m, 205m, 206m, 0.75m, closed: false),
        ];
        var now = Day.AddHours(14).AddMinutes(20);

        var result = CandleAggregator.Aggregate(hours, SixHours, now);

        Assert.Equal(2, result.Count);
        Assert.Equal(new Candle(Day.AddHours(6), 200m, 210m, 197m, 208m, 6m, IsClosed: true), result[0]);
        Assert.Equal(new Candle(Day.AddHours(12), 208m, 216m, 205m, 206m, 5.75m, IsClosed: false), result[1]);
    }

    [Fact]
    public void A_bucket_closes_when_its_six_hours_have_passed_not_before()
    {
        var hours = Enumerable.Range(0, 6).Select(i => IntervalData.Source(Day.AddHours(i), Hour)).ToList();

        Assert.False(CandleAggregator.Aggregate(hours, SixHours, Day.AddHours(6).AddTicks(-1))[0].IsClosed);
        Assert.True(CandleAggregator.Aggregate(hours, SixHours, Day.AddHours(6))[0].IsClosed);
    }

    [Fact]
    public void A_leading_incomplete_bucket_is_dropped()
    {
        // Kraken serves the newest 720 hours: the oldest bucket usually starts mid-way (here at 03:00).
        var hours = Enumerable.Range(3, 15).Select(i => IntervalData.Source(Day.AddHours(i), Hour)).ToList(); // 03..17

        var result = CandleAggregator.Aggregate(hours, SixHours, Day.AddHours(17).AddMinutes(30));

        Assert.Equal([Day.AddHours(6), Day.AddHours(12)], result.Select(c => c.OpenTime).ToArray());
        Assert.Equal(hours[3].Open, result[0].Open); // 06:00, not the 03:00 open
        Assert.False(result[1].IsClosed);
    }

    [Fact]
    public void A_missing_hour_inside_a_bucket_leaves_the_bucket_with_the_hours_it_has()
    {
        Candle[] hours =
        [
            H(6, 300m, 305m, 299m, 301m, 1m),
            H(7, 301m, 302m, 290m, 295m, 2m),
            H(9, 295m, 330m, 294m, 320m, 4m), // 08:00 missing
            H(10, 320m, 321m, 310m, 311m, 8m),
            H(11, 311m, 312m, 309m, 310m, 16m),
        ];

        var c = Assert.Single(CandleAggregator.Aggregate(hours, SixHours, Day.AddDays(1)));

        Assert.Equal(new Candle(Day.AddHours(6), 300m, 330m, 290m, 310m, 31m, IsClosed: true), c);
    }

    [Fact]
    public void Thirty_days_of_hours_make_the_6h_series_of_the_oracle()
    {
        // The Kraken history: the newest 720 hourly candles at 17:20 UTC.
        var now = Fixtures.Utc("2026-10-31T17:20:00Z");
        var hours = IntervalData.OpenTimes(Hour, now, 720).Select(t => IntervalData.Source(t, Hour)).ToList();

        var result = CandleAggregator.Aggregate(hours, SixHours, now);

        var expected = IntervalData.Aggregate(hours, SixHours, now);
        Assert.Equal(expected, result);
        Assert.Equal(120, result.Count); // 2026-10-01 18:00 .. 2026-10-31 12:00
        Assert.Equal(Fixtures.Utc("2026-10-31T12:00:00Z"), result[^1].OpenTime);
        Assert.False(result[^1].IsClosed);
        Assert.All(result.SkipLast(1), c => Assert.True(c.IsClosed));
    }

    [Fact]
    public void Two_hour_buckets_work_the_same_way()
    {
        var hours = Enumerable.Range(0, 6).Select(i => IntervalData.Source(Day.AddHours(i), Hour)).ToList();
        var twoHours = TimeSpan.FromHours(2);

        Assert.Equal(IntervalData.Aggregate(hours, twoHours, Day.AddHours(5)), CandleAggregator.Aggregate(hours, twoHours, Day.AddHours(5)));
    }

    [Fact]
    public void No_candles_make_no_buckets()
    {
        Assert.Empty(CandleAggregator.Aggregate([], SixHours, Day));
    }
}
