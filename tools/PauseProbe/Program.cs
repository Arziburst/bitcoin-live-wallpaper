using System.Runtime.InteropServices;
using BitcoinWallpaper.Chart.Core.Pause;
using BitcoinWallpaper.Host.Playback;

// Prints one line whenever the playback state, the poll verdict or the foreground window class changes.
// Reads window state only: attaches nothing to the desktop. Ctrl+C to stop.

// The manifest declares PerMonitorV2, but "dotnet PauseProbe.dll" runs under dotnet.exe's manifest: without this,
// monitor rects come back DPI-scaled while DWM frame bounds stay physical.
SetProcessDpiAwarenessContext(-4); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2

var seconds = args.Length > 0 && int.TryParse(args[0], out var s) && s > 0 ? s : 0;

Console.WriteLine($"PauseProbe: polling every {PlaybackMonitor.PollInterval.TotalMilliseconds:0} ms, " +
    $"active monitor = primary {Format(Win32PauseSnapshotSource.PrimaryMonitor())}. Ctrl+C to stop.");
Console.WriteLine("time     | state   | verdict (reason)          | foreground class (rect, zoomed) | notification | session");

using var monitor = new PlaybackMonitor();
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

var gate = new object();
string? last = null;
monitor.Sampled += (_, sample) =>
{
    var fg = sample.Snapshot.Foreground;
    // An uncorroborated guard also prints its countdown every 15 s.
    var key = $"{sample.State}|{sample.Verdict}|{fg?.ClassName}|{(int?)(sample.GuardTimeLeft?.TotalSeconds / 15)}";
    lock (gate)
    {
        if (key == last)
        {
            return;
        }

        last = key;
        var verdict = sample.Verdict.State == PlaybackState.Paused ? $"Paused ({sample.Verdict.Reason})" : "Running";
        var foreground = fg is null ? "(none)" : $"{fg.ClassName} {Format(fg.Rect)}{(fg.IsZoomed ? " zoomed" : "")}";
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} | {sample.State,-7} | {verdict,-25} | {foreground} | " +
            $"{sample.Snapshot.Notification} | {sample.Snapshot.Session}");

        // The stale-flag guard (NotificationGuard) when Busy / D3D is in play.
        if (sample.Verdict.Reason == PauseReason.NotificationCorroborated)
        {
            Console.WriteLine($"         guard: corroborated (a window covers >= {NotificationGuard.EvidencePercent} % " +
                "of the monitor), paused for as long as it lasts");
        }
        else if (sample.Verdict.Reason == PauseReason.NotificationUncorroborated && sample.GuardTimeLeft is { } left)
        {
            Console.WriteLine($"         guard: uncorroborated, {left.TotalSeconds:0} s left of " +
                $"{NotificationGuard.UncorroboratedLimit.TotalSeconds:0} s");
        }
    }
};
monitor.PausedChanged += (_, e) =>
{
    lock (gate)
    {
        Console.WriteLine(e.IsPaused ? $"         >>> PAUSED: {e.Verdict.Reason}" : "         >>> RESUMED");
    }
};

monitor.Start();
try
{
    await Task.Delay(seconds > 0 ? TimeSpan.FromSeconds(seconds) : Timeout.InfiniteTimeSpan, stop.Token);
}
catch (OperationCanceledException)
{
}

return 0;

static string Format(PixelRect r) => $"({r.Left},{r.Top})-({r.Right},{r.Bottom})";

[DllImport("user32.dll")]
static extern bool SetProcessDpiAwarenessContext(nint value);
