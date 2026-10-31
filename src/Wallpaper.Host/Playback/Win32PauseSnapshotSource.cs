using System.Runtime.InteropServices;
using System.Text;
using BitcoinWallpaper.Chart.Core.Pause;
using static BitcoinWallpaper.Host.Playback.PlaybackNative;

namespace BitcoinWallpaper.Host.Playback;

/// <summary>Produces one PauseSnapshot of the moment.</summary>
public interface IPauseSnapshotSource
{
    PauseSnapshot Capture();
}

/// <summary>Reads the desktop through Win32: the foreground window, the visible top-level windows, the
/// notification state and the session state. Read-only. Rects are physical pixels when the process is
/// per-monitor DPI aware (the host and PauseProbe declare PerMonitorV2).</summary>
public sealed class Win32PauseSnapshotSource : IPauseSnapshotSource
{
    private readonly Func<PixelRect> activeMonitor;
    private readonly uint ownProcessId = (uint)Environment.ProcessId;
    private readonly StringBuilder classBuffer = new(256);

    /// <param name="activeMonitor">The monitor the wallpaper is drawn on; the primary monitor when null.
    /// The host passes its own once it knows where it attached (#8).</param>
    public Win32PauseSnapshotSource(Func<PixelRect>? activeMonitor = null)
    {
        this.activeMonitor = activeMonitor ?? PrimaryMonitor;
    }

    public PauseSnapshot Capture()
    {
        var shell = GetShellWindow();
        var foregroundHandle = GetForegroundWindow();
        var foreground = foregroundHandle == 0 ? null : Describe(foregroundHandle, shell);

        var visible = new List<WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            // Only what is really on screen: hidden, minimised and cloaked windows (other virtual desktops,
            // suspended UWP apps, the closed Start menu) and click-through overlays never hide the wallpaper.
            if (IsWindowVisible(hwnd) && !IsIconic(hwnd) && !IsCloaked(hwnd) && !IsClickThrough(hwnd))
            {
                visible.Add(Describe(hwnd, shell));
            }

            return true;
        }, 0);

        return new PauseSnapshot(activeMonitor(), foreground, visible, QueryNotificationState(), QuerySessionState());
    }

    /// <summary>The primary monitor's full rect.</summary>
    public static PixelRect PrimaryMonitor()
    {
        var monitor = MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(monitor, ref info) ? ToPixelRect(info.Monitor) : default;
    }

    private WindowInfo Describe(nint hwnd, nint shell)
    {
        classBuffer.Clear();
        var length = GetClassName(hwnd, classBuffer, classBuffer.Capacity);
        var className = length > 0 ? classBuffer.ToString(0, length) : string.Empty;
        GetWindowThreadProcessId(hwnd, out var processId);

        return new WindowInfo(className, FrameBounds(hwnd),
            IsZoomed: IsZoomed(hwnd),
            IsMinimized: IsIconic(hwnd),
            IsOurs: processId == ownProcessId,
            IsShell: hwnd == shell);
    }

    /// <summary>The visible frame without the invisible resize borders; GetWindowRect when DWM cannot say.</summary>
    private static PixelRect FrameBounds(nint hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT bounds, Marshal.SizeOf<RECT>()) == 0)
        {
            return ToPixelRect(bounds);
        }

        return GetWindowRect(hwnd, out var rect) ? ToPixelRect(rect) : default;
    }

    private static bool IsCloaked(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static bool IsClickThrough(nint hwnd) =>
        ((ulong)GetWindowLongPtr(hwnd, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0;

    private static NotificationState QueryNotificationState() =>
        SHQueryUserNotificationState(out var state) == 0 && Enum.IsDefined((NotificationState)state)
            ? (NotificationState)state
            : NotificationState.AcceptsNotifications;

    /// <summary>The session's lock and connect state from WTSSessionInfoEx (WTSINFOEXW, level 1).</summary>
    private static SessionState QuerySessionState()
    {
        if (!WTSQuerySessionInformation(WTS_CURRENT_SERVER_HANDLE, WTS_CURRENT_SESSION, WTSSessionInfoEx,
                out var buffer, out _) || buffer == 0)
        {
            return SessionState.Active;
        }

        try
        {
            // WTSINFOEXW: DWORD Level, then the WTSINFOEX_LEVEL1_W union, 8-byte aligned (it holds
            // LARGE_INTEGERs): SessionId at 8, SessionState at 12, SessionFlags at 16.
            if (Marshal.ReadInt32(buffer, 0) != 1)
            {
                return SessionState.Active;
            }

            if (Marshal.ReadInt32(buffer, 12) == WTSDisconnected)
            {
                return SessionState.RemoteDisconnected;
            }

            return Marshal.ReadInt32(buffer, 16) == WTS_SESSIONSTATE_LOCK ? SessionState.Locked : SessionState.Active;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static PixelRect ToPixelRect(RECT rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);
}
