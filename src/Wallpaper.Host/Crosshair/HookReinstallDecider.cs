namespace BitcoinWallpaper.Host.Crosshair;

/// <summary>
/// Whether the low-level mouse hook must be installed again (#47): Windows removes a low-level hook silently when its
/// callback is too slow once (LowLevelHooksTimeout), and the crosshair would then stop following the mouse for good.
/// The host's existing watchdog (every 2 s) feeds one GetCursorPos sample: when the cursor stands somewhere else than
/// where the hook last saw it, and the hook has been silent longer than <c>silence</c>, the hook is gone. No timer of
/// its own and nothing at all while the hook is not supposed to run (no idle cost). Pure.
/// </summary>
public sealed class HookReinstallDecider
{
    private readonly TimeSpan _silence;
    private readonly TimeSpan _minInterval;
    private TimeSpan? _lastReinstall;

    /// <param name="silence">How long the hook may report nothing while the cursor moved.</param>
    /// <param name="minInterval">At least this long between two reinstalls.</param>
    public HookReinstallDecider(TimeSpan silence, TimeSpan minInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(silence, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minInterval, TimeSpan.Zero);
        _silence = silence;
        _minInterval = minInterval;
    }

    /// <summary>True ("reinstall", and <paramref name="now"/> remembered) only if the hook is expected, the cursor
    /// differs from the hook's last point, the hook has been silent longer than the threshold, and no reinstall was
    /// answered within the minimum interval.</summary>
    /// <param name="hookX">The last point the hook reported (or the cursor at install time).</param>
    /// <param name="hookAt">When the hook last reported (or the install time), on a monotonic clock.</param>
    /// <param name="cursorX">The watchdog's GetCursorPos sample at <paramref name="now"/>.</param>
    public bool ShouldReinstall(bool hookExpected, int hookX, int hookY, TimeSpan hookAt, int cursorX, int cursorY, TimeSpan now)
    {
        if (!hookExpected || (cursorX == hookX && cursorY == hookY) || now - hookAt <= _silence)
        {
            return false;
        }

        if (_lastReinstall is { } last && now - last < _minInterval)
        {
            return false;
        }

        _lastReinstall = now;
        return true;
    }
}
