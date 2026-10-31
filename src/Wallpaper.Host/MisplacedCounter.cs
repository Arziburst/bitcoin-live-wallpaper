namespace BitcoinWallpaper.Host;

/// <summary>
/// The tries to re-place a wallpaper window that is not where it should be (#23): at most <see cref="MaxTries"/>
/// per episode of consecutive misplaced observations; seeing the window back in place ends the episode, so a
/// later misplacement gets its tries again. Pure: the host calls it from its watchdog check.
/// </summary>
public sealed class MisplacedCounter
{
    public const int MaxTries = 3;

    /// <summary>Tries made in the current episode, 0..<see cref="MaxTries"/>.</summary>
    public int Tries { get; private set; }

    /// <summary>The window was observed misplaced: true (and one more try counted) while tries are left.</summary>
    public bool TryNext()
    {
        if (Tries >= MaxTries)
        {
            return false;
        }

        Tries++;
        return true;
    }

    /// <summary>The window was observed in place: the episode is over.</summary>
    public void ObservedInPlace() => Tries = 0;

    /// <summary>Attach, re-layout or turned off: a fresh count.</summary>
    public void Reset() => Tries = 0;
}
