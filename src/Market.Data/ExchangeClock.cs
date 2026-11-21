namespace BitcoinWallpaper.Market.Data;

/// <summary>The exchange's clock as seen from the PC (D-25): each time request gives a sample (local send, local
/// receive, the server's time); its offset is the server time minus the midpoint of the round trip. The offset is the
/// median of the last 5 accepted samples, so one odd answer never moves it; a round trip over 2 s is ignored. Pure:
/// no I/O and no clock of its own. Thread-safe.</summary>
public sealed class ExchangeClock
{
    public static readonly TimeSpan MaxRoundTrip = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan WarnAbove = TimeSpan.FromSeconds(5);

    public const int WindowSize = 5;

    private readonly Lock gate = new();
    private readonly Queue<TimeSpan> window = new();
    private TimeSpan offset;
    private bool warned;

    /// <summary>Raised when the offset's magnitude goes above 5 s (once per crossing); the argument is the offset.</summary>
    public event Action<TimeSpan>? Warning;

    /// <summary>The median of the last samples; zero before the first one.</summary>
    public TimeSpan Offset
    {
        get
        {
            lock (gate)
            {
                return offset;
            }
        }
    }

    public bool HasOffset
    {
        get
        {
            lock (gate)
            {
                return window.Count > 0;
            }
        }
    }

    /// <summary>False, and nothing changes, when the round trip is over <see cref="MaxRoundTrip"/>.</summary>
    public bool AddSample(DateTimeOffset sentLocal, DateTimeOffset receivedLocal, DateTimeOffset serverTime)
    {
        var roundTrip = receivedLocal - sentLocal;
        if (roundTrip > MaxRoundTrip || roundTrip < TimeSpan.Zero)
        {
            return false;
        }

        TimeSpan? warning = null;
        lock (gate)
        {
            window.Enqueue(serverTime - (sentLocal + (roundTrip / 2)));
            if (window.Count > WindowSize)
            {
                window.Dequeue();
            }

            var sorted = window.Order().ToArray();
            offset = sorted.Length % 2 == 1
                ? sorted[sorted.Length / 2]
                : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;

            var above = offset.Duration() > WarnAbove;
            if (above && !warned)
            {
                warning = offset;
            }

            warned = above;
        }

        if (warning is { } raised)
        {
            Warning?.Invoke(raised);
        }

        return true;
    }

    /// <summary>The exchange's time at the local time <paramref name="localNow"/>.</summary>
    public DateTimeOffset Now(DateTimeOffset localNow) => localNow + Offset;
}
