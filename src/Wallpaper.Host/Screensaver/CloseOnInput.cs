namespace BitcoinWallpaper.Host.Screensaver;

/// <summary>
/// When the screensaver closes (D-21): a key or any mouse button at once; a mouse move only after
/// <see cref="Grace"/> from the start and only when the pointer is more than <see cref="MovePixels"/> from the first
/// position seen, on either axis (Windows sends a move or two when the window appears, and a desk knock jiggles).
/// </summary>
public sealed class CloseOnInput(TimeSpan startedAt)
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(1);
    public const int MovePixels = 4;

    private (int X, int Y)? _first;

    public bool OnKey() => true;

    public bool OnMouseButton() => true;

    /// <param name="x">Screen px (the virtual desktop, negative coordinates included).</param>
    /// <param name="now">A reading of the same clock as the start.</param>
    public bool OnMouseMove(int x, int y, TimeSpan now)
    {
        if (_first is not { } first)
        {
            _first = (x, y);
            return false;
        }

        return now - startedAt >= Grace && (Math.Abs(x - first.X) > MovePixels || Math.Abs(y - first.Y) > MovePixels);
    }
}
