namespace BitcoinWallpaper.Chart.Core.Pause;

/// <summary>A layer over PauseDecision against a stale shell flag: Busy and RunningD3DFullScreen pause only
/// while a single window corroborates them (it covers at least EvidencePercent of the active monitor), or for
/// at most UncorroboratedLimit without one. The other states and the session pass straight to PauseDecision.
/// No timers: the state changes only inside Evaluate, called once per poll.</summary>
public sealed class NotificationGuard
{
    /// <summary>Single-window coverage of the active monitor that corroborates Busy / D3D (inclusive).</summary>
    public const int EvidencePercent = 90;

    private readonly TimeProvider time;

    // The current run: polls with the same guarded notification state and the same foreground window.
    private NotificationState? runState;
    private nint runForeground;
    private long runStart;

    public NotificationGuard(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        this.time = time;
    }

    /// <summary>How long Busy / D3D pause without window evidence.</summary>
    public static TimeSpan UncorroboratedLimit { get; } = TimeSpan.FromMinutes(2);

    /// <summary>After the last Evaluate: what is left of UncorroboratedLimit when it returned
    /// NotificationUncorroborated, otherwise null. Diagnostics (tools/PauseProbe).</summary>
    public TimeSpan? TimeLeft { get; private set; }

    public PauseVerdict Evaluate(PauseSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        TimeLeft = null;

        if (snapshot.Session != SessionState.Active)
        {
            return PauseDecision.Evaluate(snapshot);
        }

        if (snapshot.Notification is not (NotificationState.Busy or NotificationState.RunningD3DFullScreen))
        {
            runState = null;
            return PauseDecision.Evaluate(snapshot);
        }

        var now = time.GetTimestamp();
        var foreground = snapshot.Foreground?.Handle ?? 0;
        if (runState != snapshot.Notification || runForeground != foreground)
        {
            runState = snapshot.Notification;
            runForeground = foreground;
            runStart = now;
        }

        if (HasEvidence(snapshot))
        {
            return PauseVerdict.Paused(PauseReason.NotificationCorroborated);
        }

        var elapsed = time.GetElapsedTime(runStart, now);
        if (elapsed < UncorroboratedLimit)
        {
            TimeLeft = UncorroboratedLimit - elapsed;
            return PauseVerdict.Paused(PauseReason.NotificationUncorroborated);
        }

        return PauseDecision.Evaluate(snapshot with { Notification = NotificationState.AcceptsNotifications });
    }

    private static bool HasEvidence(PauseSnapshot snapshot)
    {
        var monitorArea = snapshot.Monitor.Area;
        if (monitorArea == 0)
        {
            return false;
        }

        if (snapshot.Foreground is { } foreground && Covers(foreground, snapshot.Monitor, monitorArea))
        {
            return true;
        }

        foreach (var window in snapshot.VisibleWindows)
        {
            if (Covers(window, snapshot.Monitor, monitorArea))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Covers(WindowInfo window, PixelRect monitor, long monitorArea) =>
        !window.IsShell && !window.IsOurs && !window.IsMinimized && !PauseDecision.IsShellClass(window.ClassName)
        && window.Rect.Intersect(monitor).Area * 100 >= monitorArea * EvidencePercent;
}
