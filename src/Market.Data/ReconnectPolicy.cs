namespace BitcoinWallpaper.Market.Data;

/// <summary>Jittered exponential backoff between reconnect attempts.</summary>
public static class ReconnectPolicy
{
    /// <summary>1 s.</summary>
    public static TimeSpan MinDelay { get; } = TimeSpan.FromSeconds(1);

    /// <summary>60 s.</summary>
    public static TimeSpan MaxDelay { get; } = TimeSpan.FromSeconds(60);

    /// <summary>Delay before attempt number <paramref name="attempt"/> (1 = first retry) for a jitter
    /// draw in [0, 1). Always within [MinDelay, MaxDelay].</summary>
    /// <remarks>Base 1 s doubling per attempt up to the cap; the jitter scales it to [50 %, 100 %) so
    /// clients that failed together do not retry together.</remarks>
    public static TimeSpan BackoffDelay(int attempt, double jitter)
    {
        // 2^6 s already exceeds the cap; clamping the exponent keeps huge attempt numbers finite.
        var exponent = Math.Clamp(attempt - 1, 0, 6);
        var draw = double.IsFinite(jitter) ? Math.Clamp(jitter, 0.0, 1.0) : 0.5;
        var seconds = Math.Pow(2, exponent) * (0.5 + (0.5 * draw));
        seconds = Math.Clamp(seconds, MinDelay.TotalSeconds, MaxDelay.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }
}
