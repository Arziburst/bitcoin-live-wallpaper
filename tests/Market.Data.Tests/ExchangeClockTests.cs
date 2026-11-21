namespace BitcoinWallpaper.Market.Data.Tests;

// Contract #30, criterion 1 (D-25): the pure estimator of the exchange's clock. No I/O, no clock of its own.
//
// API SKETCH (written before the code, #237). Namespace BitcoinWallpaper.Market.Data,
// src/Market.Data/ExchangeClock.cs. Everything public.
//
//   public sealed class ExchangeClock
//   {
//       public static readonly TimeSpan MaxRoundTrip = TimeSpan.FromSeconds(2);
//       public static readonly TimeSpan WarnAbove = TimeSpan.FromSeconds(5);
//       public const int WindowSize = 5;
//
//       // Local send time, local receive time, the server's time from the answer.
//       // Round trip = received - sent. A sample over MaxRoundTrip (strictly) is ignored: returns false, changes
//       // nothing. Else its offset = server - (sent + received) / 2 joins the window of the last 5 accepted samples
//       // (the oldest leaves) and the method returns true.
//       public bool AddSample(DateTimeOffset sentLocal, DateTimeOffset receivedLocal, DateTimeOffset serverTime);
//
//       // The median of the window; Zero while there is no sample. The first accepted sample applies at once
//       // (the median of one). Only odd window sizes (1, 3, 5) are pinned: an even one is the implementer's choice.
//       public TimeSpan Offset { get; }
//       public bool HasOffset { get; }              // true after the first accepted sample
//
//       public DateTimeOffset Now(DateTimeOffset localNow);   // localNow + Offset
//
//       // Raised (once) when Offset's magnitude goes from <= 5 s (or no sample) to > 5 s; the argument is the
//       // new Offset. Raised again only after the magnitude was back at <= 5 s. Exactly 5 s does not warn.
//       // Nothing is raised when the offset falls back.
//       public event Action<TimeSpan>? Warning;
//   }
//
// No kline-event-time refinement: the estimator has no other input than AddSample.
public class ExchangeClockTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A sample with the given true offset and round trip: the server answered at the midpoint.</summary>
    private static bool Add(ExchangeClock clock, double offsetSeconds, double rttSeconds = 0, int n = 0)
    {
        var sent = T0.AddMinutes(10 * n);
        var received = sent.AddSeconds(rttSeconds);
        var server = sent.AddSeconds((rttSeconds / 2) + offsetSeconds);
        return clock.AddSample(sent, received, server);
    }

    private static void AssertOffset(double seconds, ExchangeClock clock) =>
        Assert.Equal(seconds, clock.Offset.TotalSeconds, 3);

    [Fact]
    public void Before_any_sample_there_is_no_offset_and_Now_is_the_local_time()
    {
        var clock = new ExchangeClock();
        Assert.False(clock.HasOffset);
        Assert.Equal(TimeSpan.Zero, clock.Offset);
        Assert.Equal(T0, clock.Now(T0));
    }

    [Theory]
    [InlineData(90, 1.0)]
    [InlineData(-37.5, 0.4)]
    [InlineData(0.25, 2.0)]
    public void The_offset_is_the_server_time_minus_the_midpoint_of_the_round_trip(double offset, double rtt)
    {
        var clock = new ExchangeClock();
        Assert.True(Add(clock, offset, rtt));
        Assert.True(clock.HasOffset);
        AssertOffset(offset, clock);
    }

    [Fact]
    public void The_midpoint_is_used_not_the_send_or_receive_time()
    {
        var clock = new ExchangeClock();
        // Sent 12:00:00, received 12:00:02, the server says 12:01:31 -> offset 90 s from the midpoint 12:00:01.
        clock.AddSample(T0, T0.AddSeconds(2), T0.AddSeconds(91));
        AssertOffset(90, clock);
    }

    [Fact]
    public void The_first_accepted_sample_applies_at_once()
    {
        var clock = new ExchangeClock();
        Add(clock, 123);
        AssertOffset(123, clock);
    }

    [Fact]
    public void Now_is_the_local_time_plus_the_offset()
    {
        var clock = new ExchangeClock();
        Add(clock, -90);
        var local = T0.AddHours(7);
        Assert.Equal(local.AddSeconds(-90), clock.Now(local));
        Assert.Equal(local.AddHours(1).AddSeconds(-90), clock.Now(local.AddHours(1))); // the argument rules, no own clock
    }

    [Fact]
    public void The_offset_is_the_median_of_the_last_five_samples()
    {
        var clock = new ExchangeClock();
        Add(clock, 10, n: 0);
        AssertOffset(10, clock);                 // 1 sample

        Add(clock, 20, n: 1);
        Add(clock, 30, n: 2);
        AssertOffset(20, clock);                 // 10 20 30

        Add(clock, 1000, n: 3);
        Add(clock, 40, n: 4);
        AssertOffset(30, clock);                 // 10 20 30 1000 40: the median, not the mean (220) and not the outlier

        Add(clock, 50, n: 5);
        AssertOffset(40, clock);                 // the first one left: 20 30 1000 40 50

        Add(clock, 60, n: 6);
        AssertOffset(50, clock);                 // 30 1000 40 50 60

        Add(clock, -500, n: 7);
        AssertOffset(50, clock);                 // 1000 40 50 60 -500
    }

    [Fact]
    public void A_step_of_the_exchange_clock_needs_three_of_five_samples_to_apply()
    {
        var clock = new ExchangeClock();
        for (var i = 0; i < 5; i++)
        {
            Add(clock, 0, n: i);
        }

        Add(clock, 90, n: 5);
        Add(clock, 90, n: 6);
        AssertOffset(0, clock);                  // 0 0 0 90 90
        Add(clock, 90, n: 7);
        AssertOffset(90, clock);                 // 0 0 90 90 90
    }

    [Fact]
    public void A_round_trip_over_two_seconds_is_ignored()
    {
        var clock = new ExchangeClock();
        Assert.False(Add(clock, 90, rttSeconds: 2.5));
        Assert.False(clock.HasOffset);
        Assert.Equal(TimeSpan.Zero, clock.Offset);
        Assert.Equal(T0, clock.Now(T0));
    }

    [Fact]
    public void A_round_trip_of_exactly_two_seconds_is_accepted_and_just_over_is_not()
    {
        var clock = new ExchangeClock();
        Assert.True(Add(clock, 10, rttSeconds: 2.0));
        Assert.False(Add(clock, 500, rttSeconds: 2.001, n: 1));
        AssertOffset(10, clock);
    }

    [Fact]
    public void An_ignored_sample_does_not_enter_the_window()
    {
        var clock = new ExchangeClock();
        Add(clock, 10, n: 0);
        Add(clock, 20, n: 1);
        Add(clock, 30, n: 2);
        Add(clock, 9999, rttSeconds: 5, n: 3);
        Add(clock, 9999, rttSeconds: 5, n: 4);
        Add(clock, 9999, rttSeconds: 5, n: 5);
        AssertOffset(20, clock);                 // still 10 20 30
    }

    [Fact]
    public void The_warning_is_raised_once_per_crossing_of_five_seconds()
    {
        var clock = new ExchangeClock();
        var warnings = new List<TimeSpan>();
        clock.Warning += warnings.Add;

        for (var i = 0; i < 5; i++)
        {
            Add(clock, 10, n: i);                // above from the first sample on
        }

        Assert.Single(warnings);
        AssertOffset(10, clock);
        Assert.Equal(10, warnings[0].TotalSeconds, 3);

        for (var i = 5; i < 8; i++)
        {
            Add(clock, 0, n: i);                 // 10 10 10 10 0 / 10 10 10 0 0 / 10 10 0 0 0
        }

        AssertOffset(0, clock);
        Assert.Single(warnings);                 // falling back raises nothing

        for (var i = 8; i < 11; i++)
        {
            Add(clock, 10, n: i);                // 0 0 0 0 10 / 0 0 0 10 10 / 0 0 10 10 10
        }

        AssertOffset(10, clock);
        Assert.Equal(2, warnings.Count);         // a new crossing: a new warning
    }

    [Fact]
    public void A_negative_offset_warns_by_its_magnitude()
    {
        var clock = new ExchangeClock();
        var warnings = new List<TimeSpan>();
        clock.Warning += warnings.Add;
        Add(clock, -90);
        Assert.Single(warnings);
        Assert.Equal(-90, warnings[0].TotalSeconds, 3);
    }

    [Fact]
    public void Exactly_five_seconds_does_not_warn_but_just_over_does()
    {
        var at5 = new ExchangeClock();
        var warnings = new List<TimeSpan>();
        at5.Warning += warnings.Add;
        Add(at5, 5);
        Add(at5, -5, n: 1);
        Add(at5, 4.9, n: 2);
        Assert.Empty(warnings);

        var over = new ExchangeClock();
        over.Warning += warnings.Add;
        Add(over, 5.001);
        Assert.Single(warnings);
    }

    [Fact]
    public void An_ignored_sample_never_warns()
    {
        var clock = new ExchangeClock();
        var warnings = new List<TimeSpan>();
        clock.Warning += warnings.Add;
        Add(clock, 90, rttSeconds: 3);
        Assert.Empty(warnings);
    }
}
