namespace BitcoinWallpaper.Chart.Core.Pause;

/// <summary>Decides from one snapshot whether the wallpaper is hidden and should stop presenting (D-2, D-6).
/// Pure: no Win32, no clock.</summary>
public static class PauseDecision
{
    /// <summary>Coverage at or above this share of the monitor pauses (inclusive).</summary>
    public const int CoveragePercent = 95;

    public static PauseVerdict Evaluate(PauseSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        switch (snapshot.Session)
        {
            case SessionState.Locked:
                return PauseVerdict.Paused(PauseReason.SessionLocked);
            case SessionState.RemoteDisconnected:
                return PauseVerdict.Paused(PauseReason.SessionDisconnected);
        }

        // NotPresent: screen saver, locked machine or an inactive fast-user-switching session.
        // QuietTime, App and AcceptsNotifications defer to the windows.
        if (snapshot.Notification is NotificationState.Busy or NotificationState.RunningD3DFullScreen
            or NotificationState.PresentationMode or NotificationState.NotPresent)
        {
            return PauseVerdict.Paused(PauseReason.NotificationState);
        }

        var monitor = snapshot.Monitor;
        var monitorArea = monitor.Area;
        if (monitorArea == 0)
        {
            return PauseVerdict.Running;
        }

        var foreground = snapshot.Foreground is { } fg && Counts(fg) ? fg : null;
        if (foreground is not null)
        {
            var onMonitor = foreground.Rect.Intersect(monitor).Area;

            // Maximised on the monitor that holds most of it: the border spilling onto a neighbour does not count.
            if (foreground.IsZoomed && onMonitor * 2 > foreground.Rect.Area)
            {
                return PauseVerdict.Paused(PauseReason.Maximized);
            }

            if (onMonitor == monitorArea)
            {
                return PauseVerdict.Paused(PauseReason.Fullscreen);
            }

            if (Covers(onMonitor, monitorArea))
            {
                return PauseVerdict.Paused(PauseReason.ForegroundCovers);
            }
        }

        var rects = new List<PixelRect>();
        if (foreground is not null)
        {
            rects.Add(foreground.Rect);
        }

        foreach (var window in snapshot.VisibleWindows)
        {
            if (Counts(window))
            {
                rects.Add(window.Rect);
            }
        }

        return Covers(UnionArea(rects, monitor), monitorArea)
            ? PauseVerdict.Paused(PauseReason.WindowsCover)
            : PauseVerdict.Running;
    }

    /// <summary>The desktop, the taskbars, our own windows and minimised windows never hide the wallpaper.</summary>
    public static bool IsShellClass(string className) =>
        className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    private static bool Counts(WindowInfo window) =>
        !window.IsShell && !window.IsOurs && !window.IsMinimized && !IsShellClass(window.ClassName);

    private static bool Covers(long covered, long monitorArea) => covered * 100 >= monitorArea * CoveragePercent;

    /// <summary>Area of the union of <paramref name="rects"/> clipped to <paramref name="clip"/>: a sweep over
    /// vertical strips between the distinct x edges, merging the y intervals inside each strip.</summary>
    private static long UnionArea(List<PixelRect> rects, PixelRect clip)
    {
        var clipped = new List<PixelRect>(rects.Count);
        foreach (var rect in rects)
        {
            var part = rect.Intersect(clip);
            if (part.Area > 0)
            {
                clipped.Add(part);
            }
        }

        var xs = new SortedSet<int>();
        foreach (var rect in clipped)
        {
            xs.Add(rect.Left);
            xs.Add(rect.Right);
        }

        long area = 0;
        var edges = xs.ToArray();
        var spans = new List<(int Top, int Bottom)>();
        for (var i = 0; i + 1 < edges.Length; i++)
        {
            int left = edges[i], right = edges[i + 1];
            spans.Clear();
            foreach (var rect in clipped)
            {
                if (rect.Left <= left && rect.Right >= right)
                {
                    spans.Add((rect.Top, rect.Bottom));
                }
            }

            spans.Sort();
            long height = 0;
            int runTop = int.MinValue, runBottom = int.MinValue;
            foreach (var (top, bottom) in spans)
            {
                if (top > runBottom)
                {
                    height += runBottom - runTop;
                    (runTop, runBottom) = (top, bottom);
                }
                else if (bottom > runBottom)
                {
                    runBottom = bottom;
                }
            }

            height += runBottom - runTop;
            area += height * (right - left);
        }

        return area;
    }
}
