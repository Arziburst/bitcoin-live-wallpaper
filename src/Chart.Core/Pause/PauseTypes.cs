namespace BitcoinWallpaper.Chart.Core.Pause;

/// <summary>Win32 RECT semantics in virtual-screen pixels: Right/Bottom exclusive, coordinates may be negative.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public long Area => Right > Left && Bottom > Top ? (long)(Right - Left) * (Bottom - Top) : 0;

    /// <summary>The overlap of both rects; empty (Area 0) when they do not overlap.</summary>
    public PixelRect Intersect(PixelRect other) => new(
        Math.Max(Left, other.Left), Math.Max(Top, other.Top),
        Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
}

/// <summary>Mirrors QUERY_USER_NOTIFICATION_STATE (SHQueryUserNotificationState), same numeric values.</summary>
public enum NotificationState
{
    NotPresent = 1,
    Busy = 2,
    RunningD3DFullScreen = 3,
    PresentationMode = 4,
    AcceptsNotifications = 5,
    QuietTime = 6,
    App = 7,
}

public enum SessionState
{
    Active,
    Locked,
    RemoteDisconnected,
}

/// <summary>One top-level window as the host sees it.</summary>
/// <param name="Handle">The HWND, 0 when unknown: identifies the window across polls (NotificationGuard).</param>
public sealed record WindowInfo(string ClassName, PixelRect Rect, bool IsZoomed = false,
    bool IsMinimized = false, bool IsOurs = false, bool IsShell = false, nint Handle = 0);

/// <summary>What the host saw in one poll.</summary>
/// <param name="Monitor">The active monitor: the one the wallpaper is drawn on.</param>
/// <param name="Foreground">Null when there is no foreground window.</param>
/// <param name="VisibleWindows">Visible top-level windows on any monitor; may or may not contain the foreground.</param>
public sealed record PauseSnapshot(
    PixelRect Monitor,
    WindowInfo? Foreground,
    IReadOnlyList<WindowInfo> VisibleWindows,
    NotificationState Notification,
    SessionState Session);

public enum PlaybackState
{
    Running,
    Paused,
}

public enum PauseReason
{
    None,
    SessionLocked,
    SessionDisconnected,
    NotificationState,
    Maximized,
    Fullscreen,
    ForegroundCovers,
    WindowsCover,

    /// <summary>Busy or RunningD3DFullScreen, and one window covers most of the active monitor (NotificationGuard).</summary>
    NotificationCorroborated,

    /// <summary>Busy or RunningD3DFullScreen without such a window, inside NotificationGuard.UncorroboratedLimit.</summary>
    NotificationUncorroborated,
}

public readonly record struct PauseVerdict(PlaybackState State, PauseReason Reason)
{
    public static PauseVerdict Running => new(PlaybackState.Running, PauseReason.None);

    public static PauseVerdict Paused(PauseReason reason) => new(PlaybackState.Paused, reason);
}
