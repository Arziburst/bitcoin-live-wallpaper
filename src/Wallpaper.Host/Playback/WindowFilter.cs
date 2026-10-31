using BitcoinWallpaper.Chart.Core.Pause;

namespace BitcoinWallpaper.Host.Playback;

/// <summary>What Win32 reports about a window beyond its WindowInfo: whether it is really on screen.</summary>
public readonly record struct WindowFlags(bool IsVisible, bool IsIconic, bool IsCloaked, bool IsClickThrough);

/// <summary>A top-level window before the on-screen filter.</summary>
public sealed record CandidateWindow(WindowInfo Info, WindowFlags Flags);

/// <summary>One on-screen rule for the foreground and the enumerated windows alike.</summary>
public static class WindowFilter
{
    /// <summary>Hidden, minimised and cloaked windows (other virtual desktops, suspended UWP apps, the closed
    /// Start menu) and click-through overlays (WS_EX_TRANSPARENT) never hide the wallpaper.</summary>
    public static bool IsOnScreen(WindowFlags flags) =>
        flags.IsVisible && !flags.IsIconic && !flags.IsCloaked && !flags.IsClickThrough;

    /// <summary>The snapshot from what Win32 reported: the foreground and the windows that are on screen, the
    /// windows in their order; monitor, notification and session pass through.</summary>
    public static PauseSnapshot BuildSnapshot(PixelRect monitor, CandidateWindow? foreground,
        IReadOnlyList<CandidateWindow> windows, NotificationState notification, SessionState session)
    {
        ArgumentNullException.ThrowIfNull(windows);

        var visible = new List<WindowInfo>(windows.Count);
        foreach (var window in windows)
        {
            if (IsOnScreen(window.Flags))
            {
                visible.Add(window.Info);
            }
        }

        var shown = foreground is not null && IsOnScreen(foreground.Flags) ? foreground.Info : null;
        return new PauseSnapshot(monitor, shown, visible, notification, session);
    }
}
