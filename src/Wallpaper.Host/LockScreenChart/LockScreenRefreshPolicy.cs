namespace BitcoinWallpaper.Host.LockScreenChart;

/// <summary>
/// When the lock screen snapshot is refreshed (D-34): on the first live data after start (or after the feature is
/// turned on), at once when a new candle opens, and every <see cref="Interval"/> since the last success; never while
/// the wallpaper or the feature is off, never without live data. After a failure at most one retry per
/// <see cref="Interval"/>, whatever else is due. Pure: no clock of its own, every call passes <c>now</c>.
/// </summary>
public sealed class LockScreenRefreshPolicy
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    private bool _featureOn = true;
    private DateTimeOffset? _lastSuccess;
    private DateTimeOffset? _lastCandleOpen;
    private DateTimeOffset? _lastFailure;

    /// <summary>The tray item. Turning it on forgets the last refresh and the last failure (refresh at once).</summary>
    public bool FeatureOn
    {
        get => _featureOn;
        set
        {
            if (value && !_featureOn)
            {
                _lastSuccess = null;
                _lastCandleOpen = null;
                _lastFailure = null;
            }

            _featureOn = value;
        }
    }

    /// <summary>The wallpaper is On (tray). Off never refreshes; setting it forgets nothing.</summary>
    public bool WallpaperOn { get; set; } = true;

    /// <summary>Changes nothing. <paramref name="candleOpen"/>: open time of the newest candle, null before data.</summary>
    public bool ShouldRefresh(DateTimeOffset now, bool hasLiveData, DateTimeOffset? candleOpen)
    {
        if (!_featureOn || !WallpaperOn || !hasLiveData)
        {
            return false;
        }

        if (_lastFailure is { } failed && now - failed < Interval)
        {
            return false;
        }

        if (_lastSuccess is not { } success)
        {
            return true;
        }

        return candleOpen != _lastCandleOpen || now - success >= Interval;
    }

    /// <summary>A snapshot was set; clears the failure back-off.</summary>
    public void Refreshed(DateTimeOffset now, DateTimeOffset? candleOpen)
    {
        _lastSuccess = now;
        _lastCandleOpen = candleOpen;
        _lastFailure = null;
    }

    public void Failed(DateTimeOffset now) => _lastFailure = now;
}
