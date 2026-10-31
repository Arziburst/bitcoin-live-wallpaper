using BitcoinWallpaper.Chart.Core.Layout;
using BitcoinWallpaper.Chart.Core.Pause;

namespace BitcoinWallpaper.Host;

/// <summary>What a chart is drawn for: the desktop wallpaper, the lock screen picture (D-34) or the screensaver.</summary>
public enum SurfaceKind
{
    DesktopWallpaper,
    LockScreenSnapshot,
    Screensaver,
}

/// <summary>The chart's content rectangle per surface (D-37). Pure; the host feeds it GetMonitorInfo's rcMonitor and
/// rcWork.</summary>
public static class SurfaceContent
{
    /// <summary>The content rectangle relative to the monitor's top-left (physical px). <paramref name="monitor"/> and
    /// <paramref name="workArea"/> are virtual-screen px (Right/Bottom exclusive). The desktop wallpaper: the work area
    /// cut to the monitor, moved by the monitor's origin; an empty cut falls back to the full monitor. The lock screen
    /// picture and the screensaver: the full monitor (no taskbar there).</summary>
    public static RectPx For(SurfaceKind kind, PixelRect monitor, PixelRect workArea)
    {
        var full = FullMonitor(monitor.Right - monitor.Left, monitor.Bottom - monitor.Top);
        if (kind != SurfaceKind.DesktopWallpaper)
        {
            return full;
        }

        var cut = workArea.Intersect(monitor);
        return cut.Area == 0
            ? full
            : new RectPx(cut.Left - monitor.Left, cut.Top - monitor.Top, cut.Right - cut.Left, cut.Bottom - cut.Top);
    }

    /// <summary>The whole monitor of the given size: RectPx(0, 0, width, height).</summary>
    public static RectPx FullMonitor(int width, int height) => new(0, 0, width, height);
}

/// <summary>When a window message means the work area changed and the desktop chart must be laid out again (D-37).
/// Remembers the work area of the last answered check; constructed with the one in use.</summary>
public sealed class WorkAreaRelayout(PixelRect current)
{
    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint SPI_SETWORKAREA = 0x002F;

    private PixelRect _current = current;

    /// <summary>True (and <paramref name="currentWorkArea"/> remembered) only on WM_DISPLAYCHANGE, or on
    /// WM_SETTINGCHANGE with wParam SPI_SETWORKAREA, when the work area differs from the remembered one.</summary>
    public bool ShouldRelayout(uint message, nuint wParam, PixelRect currentWorkArea)
    {
        var relevant = message == WM_DISPLAYCHANGE || (message == WM_SETTINGCHANGE && wParam == SPI_SETWORKAREA);
        if (!relevant || currentWorkArea == _current)
        {
            return false;
        }

        _current = currentWorkArea;
        return true;
    }
}
