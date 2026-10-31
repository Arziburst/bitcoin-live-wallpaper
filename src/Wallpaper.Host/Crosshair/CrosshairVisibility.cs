using BitcoinWallpaper.Chart.Core.Pause;
using BitcoinWallpaper.Host.Settings;

namespace BitcoinWallpaper.Host.Crosshair;

/// <summary>What the host read at the cursor (D-35): its position in virtual-screen px (like <see cref="PixelRect"/>).</summary>
public readonly record struct CursorSample(int X, int Y);

/// <summary>When the crosshair is shown (D-35, D-38): anywhere on the wallpaper's monitor, whatever is under the cursor
/// (windows and icons simply cover the background lines). Pure.</summary>
public static class CrosshairVisibility
{
    /// <summary>True only if the wallpaper is on and not paused and the cursor lies in the wallpaper's monitor
    /// (Right/Bottom exclusive).</summary>
    public static bool ShouldShow(CursorSample cursor, PixelRect wallpaperMonitor, bool paused, WallpaperState state) =>
        state == WallpaperState.On
        && !paused
        && Contains(wallpaperMonitor, cursor.X, cursor.Y);

    /// <summary>Win32 RECT semantics: Left/Top inside, Right/Bottom outside.</summary>
    public static bool Contains(PixelRect rect, int x, int y) =>
        x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
}
