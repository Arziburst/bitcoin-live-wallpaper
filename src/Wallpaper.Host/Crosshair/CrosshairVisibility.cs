using BitcoinWallpaper.Chart.Core.Pause;
using BitcoinWallpaper.Host.Settings;

namespace BitcoinWallpaper.Host.Crosshair;

/// <summary>What the host read at the cursor (D-35): its position in virtual-screen px (like <see cref="PixelRect"/>),
/// the class of the window under it (WindowFromPoint; null when none) and whether it is over a desktop icon (the
/// icons list view's hit test).</summary>
public readonly record struct CursorSample(int X, int Y, string? WindowClass, bool OverIcon);

/// <summary>When the crosshair is shown (D-35): only over the bare desktop of the wallpaper's monitor. Pure.</summary>
public static class CrosshairVisibility
{
    // The shell's desktop windows (the wallpaper host, its WorkerW, the icons' view and list view) and our own layer.
    private static readonly string[] DesktopClasses =
        ["Progman", "WorkerW", "SHELLDLL_DefView", "SysListView32", "BitcoinWallpaper.Surface"];

    /// <summary>The window classes that are the bare desktop; anything else (also null or empty) is another window.
    /// Ordinal, case-sensitive.</summary>
    public static bool IsDesktopClass(string? windowClass) =>
        !string.IsNullOrEmpty(windowClass) && Array.IndexOf(DesktopClasses, windowClass) >= 0;

    /// <summary>True only if the wallpaper is on and not paused, the cursor lies in the wallpaper's monitor
    /// (Right/Bottom exclusive), the window under it is the desktop and it is not over an icon.</summary>
    public static bool ShouldShow(CursorSample cursor, PixelRect wallpaperMonitor, bool paused, WallpaperState state) =>
        state == WallpaperState.On
        && !paused
        && Contains(wallpaperMonitor, cursor.X, cursor.Y)
        && IsDesktopClass(cursor.WindowClass)
        && !cursor.OverIcon;

    /// <summary>Win32 RECT semantics: Left/Top inside, Right/Bottom outside.</summary>
    public static bool Contains(PixelRect rect, int x, int y) =>
        x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
}
