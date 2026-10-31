namespace BitcoinWallpaper.Chart.Core.Layout;

/// <summary>On start and on a display change: the interval to show, and whether the series must be reloaded.</summary>
public sealed record IntervalDecision(TimeSpan Interval, bool Reload);

/// <summary>The candle interval per screen (D-13): the smallest interval of the ladder whose candles for the period
/// fit the drawable candle slots in front of the right offset. Pure: no clock, no I/O.</summary>
public static class IntervalPolicy
{
    /// <summary>30 days (D-13).</summary>
    public static TimeSpan DefaultPeriod { get; } = TimeSpan.FromDays(30);

    /// <summary>The intervals the policy picks from, ascending.</summary>
    public static IReadOnlyList<TimeSpan> Ladder { get; } =
    [
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(4),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(8),
        TimeSpan.FromHours(12),
        TimeSpan.FromDays(1),
    ];

    /// <summary>ceil(period / interval): 30 d / 4 h = 180.</summary>
    public static int CandleCount(TimeSpan period, TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        return (int)((period.Ticks + interval.Ticks - 1) / interval.Ticks);
    }

    /// <summary>The smallest ladder interval whose candle count plus the right offset fits the slots; when none
    /// fits, the largest.</summary>
    public static TimeSpan ForSlots(int candleSlots, int rightOffsetBars, TimeSpan period)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(candleSlots);
        ArgumentOutOfRangeException.ThrowIfNegative(rightOffsetBars);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);
        foreach (var interval in Ladder)
        {
            if ((long)CandleCount(period, interval) + rightOffsetBars <= candleSlots)
            {
                return interval;
            }
        }

        return Ladder[^1];
    }

    /// <summary><see cref="ForSlots"/> on the screen's layout; the slots do not depend on the candle count.</summary>
    public static TimeSpan ForScreen(int widthPx, int heightPx, double dpiScale, TimeSpan period)
    {
        var layout = LayoutEngine.Compute(widthPx, heightPx, dpiScale, 0);
        return ForSlots(layout.CandleSlots, layout.RightOffsetBars, period);
    }

    /// <summary>The screen's interval; a reload when it differs from <paramref name="current"/> or nothing is
    /// loaded yet (null).</summary>
    public static IntervalDecision OnDisplay(TimeSpan? current, int widthPx, int heightPx, double dpiScale, TimeSpan period)
    {
        var interval = ForScreen(widthPx, heightPx, dpiScale, period);
        return new IntervalDecision(interval, current != interval);
    }
}
