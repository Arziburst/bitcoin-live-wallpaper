using BitcoinWallpaper.Chart.Core.Crosshair;

namespace BitcoinWallpaper.Host.Crosshair;

/// <summary>A scene that draws the crosshair (D-35). Host thread only.</summary>
internal interface ICrosshairScene
{
    /// <summary>The cursor in surface px for the next frames, or null (hidden). Returns the crosshair as it would be
    /// drawn now (null: hidden), for <see cref="CrosshairFrameGate"/>.</summary>
    CrosshairState? SetCursor(CursorPx? cursor);
}
