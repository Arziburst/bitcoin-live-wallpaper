using BitcoinWallpaper.Chart.Core.Candles;

namespace BitcoinWallpaper.Chart.Core.Motion;

/// <summary>
/// The chart's animations (D-10, coordinator/research/design-data.md "Motion"): the forming candle tweens from
/// the OHLC shown to the new one (300 ms ease-out), a new bar shifts the series one pitch (500 ms ease-out), a
/// close change pulses the price colour (250 ms, at most one pulse a second). Pure: every value is evaluated at
/// the TimeProvider's now, no timers, no threads; reading a property has no side effects.
/// </summary>
public sealed class ChartMotion
{
    private readonly TimeProvider time;

    private Candle? target;
    private Ohlc from;
    private DateTimeOffset formingStart;
    private DateTimeOffset formingEnd;
    private DateTimeOffset shiftStart;
    private DateTimeOffset shiftEnd;
    private DateTimeOffset pulseStart;
    private DateTimeOffset pulseEnd;
    private DateTimeOffset? lastPulseStart;

    /// <summary>The end of the last change: a frame taken at or after it shows the final values.</summary>
    private DateTimeOffset? settledAt;

    private DateTimeOffset? lastFrameAt;

    public ChartMotion(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        this.time = time;
    }

    public static TimeSpan FormingDuration { get; } = TimeSpan.FromMilliseconds(300);

    public static TimeSpan ShiftDuration { get; } = TimeSpan.FromMilliseconds(500);

    public static TimeSpan PulseDuration { get; } = TimeSpan.FromMilliseconds(250);

    public static TimeSpan PulseMinInterval { get; } = TimeSpan.FromSeconds(1);

    /// <summary>The latest candle as shown now; null before the first Apply.</summary>
    public Candle? Forming
    {
        get
        {
            if (target is null)
            {
                return null;
            }

            var now = time.GetUtcNow();
            if (now >= formingEnd)
            {
                return target;
            }

            var e = EaseOut(Progress(now, formingStart, FormingDuration));
            return target with
            {
                Open = Lerp(from.Open, target.Open, e),
                High = Lerp(from.High, target.High, e),
                Low = Lerp(from.Low, target.Low, e),
                Close = Lerp(from.Close, target.Close, e),
            };
        }
    }

    /// <summary>Horizontal offset of the whole series in candle pitches: 1 when a new bar arrives, then 0.</summary>
    public double ShiftPitches
    {
        get
        {
            var now = time.GetUtcNow();
            return now >= shiftEnd ? 0.0 : 1.0 - (double)EaseOut(Progress(now, shiftStart, ShiftDuration));
        }
    }

    /// <summary>Price colour pulse strength, 1 at a close change, fading linearly to 0.</summary>
    public double Pulse
    {
        get
        {
            var now = time.GetUtcNow();
            return now >= pulseEnd ? 0.0 : 1.0 - (double)Progress(now, pulseStart, PulseDuration);
        }
    }

    /// <summary>Whether the close change that started the latest pulse went up.</summary>
    public bool PulseUp { get; private set; }

    public bool IsIdle
    {
        get
        {
            var now = time.GetUtcNow();
            return now >= formingEnd && now >= shiftEnd && now >= pulseEnd;
        }
    }

    /// <summary>True while a tween runs, and until a frame is taken at or after the last change settled.</summary>
    public bool NeedsFrame => settledAt is { } settled && !(lastFrameAt >= settled);

    /// <summary>The host drew a frame at the clock's now.</summary>
    public void FrameTaken() => lastFrameAt = time.GetUtcNow();

    /// <summary>The series' latest candle (forming or just closed) as the feed has it now.</summary>
    public void Apply(Candle latest)
    {
        ArgumentNullException.ThrowIfNull(latest);
        var now = time.GetUtcNow();
        if (target is null)
        {
            target = latest;
            formingEnd = shiftEnd = pulseEnd = now;
            settledAt = now;
            return;
        }

        if (latest == target)
        {
            return;
        }

        var previousClose = target.Close;
        if (latest.OpenTime > target.OpenTime)
        {
            // A new bar: shown at once, the series slides in by one pitch.
            formingEnd = now;
            shiftStart = now;
            shiftEnd = now + ShiftDuration;
        }
        else if (latest.OpenTime == target.OpenTime && !SameOhlc(latest, target))
        {
            // Restart from what is on screen now, so an update mid-tween never jumps.
            var shown = Forming!;
            from = new Ohlc(shown.Open, shown.High, shown.Low, shown.Close);
            formingStart = now;
            formingEnd = now + FormingDuration;
        }
        else
        {
            // An older bar, or only volume / IsClosed changed: shown at once.
            formingEnd = now;
        }

        target = latest;
        if (latest.Close != previousClose && (lastPulseStart is not { } last || now - last >= PulseMinInterval))
        {
            lastPulseStart = now;
            pulseStart = now;
            pulseEnd = now + PulseDuration;
            PulseUp = latest.Close > previousClose;
        }

        settledAt = Max(now, Max(formingEnd, Max(shiftEnd, pulseEnd)));
    }

    private static decimal Progress(DateTimeOffset now, DateTimeOffset start, TimeSpan duration) =>
        Math.Clamp((decimal)(now - start).Ticks / duration.Ticks, 0m, 1m);

    /// <summary>Cubic ease-out: fast start, gentle landing; never behind linear progress.</summary>
    private static decimal EaseOut(decimal p)
    {
        var q = 1m - p;
        return 1m - (q * q * q);
    }

    private static decimal Lerp(decimal a, decimal b, decimal e) => a + ((b - a) * e);

    private static bool SameOhlc(Candle a, Candle b) =>
        a.Open == b.Open && a.High == b.High && a.Low == b.Low && a.Close == b.Close;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    private readonly record struct Ohlc(decimal Open, decimal High, decimal Low, decimal Close);
}
