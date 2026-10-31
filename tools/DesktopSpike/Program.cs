using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using DesktopSpike;
using Microsoft.Win32;

const string UsageText = """
    DesktopSpike - draws a deep-blue rectangle with a moving white bar behind the desktop icons
    on the monitor under the mouse cursor (task #5 spike, throwaway).

    Usage: DesktopSpike [--seconds N] [--layout auto|legacy|raised] [--plain-child] [--dry-run] [--windowed] [--help]

      --seconds N      stop by itself after N seconds (default: run until Ctrl+C or the console closes)
      --layout L       force the attach path instead of detecting it (default: auto)
      --plain-child    raised layout: attach a plain child window instead of a WS_EX_LAYERED one
      --dry-run        only detect and log the desktop layout; attach nothing, change nothing
      --windowed       draw in an ordinary window instead of the desktop (checks the drawing path only)
      --help           show this text

    Log: console and %TEMP%\DesktopSpike.log
    """;

double? seconds = null;
var dryRun = false;
var plainChild = false;
var windowed = false;
DesktopLayout? forcedLayout = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--help" or "-h" or "/?":
            Console.WriteLine(UsageText);
            return 0;
        case "--dry-run":
            dryRun = true;
            break;
        case "--plain-child":
            plainChild = true;
            break;
        case "--windowed":
            windowed = true;
            break;
        case "--seconds" when i + 1 < args.Length
            && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0:
            seconds = s;
            i++;
            break;
        case "--layout" when i + 1 < args.Length && args[i + 1] is "auto" or "legacy" or "raised":
            forcedLayout = args[i + 1] switch
            {
                "legacy" => DesktopLayout.Legacy,
                "raised" => DesktopLayout.Raised,
                _ => null,
            };
            i++;
            break;
        default:
            Console.Error.WriteLine($"Unknown or incomplete argument: {args[i]}");
            Console.Error.WriteLine(UsageText);
            return 2;
    }
}

Log.Open();
try
{
    return Run();
}
finally
{
    Log.Close();
}

int Run()
{
    Log.Info($"DesktopSpike {(dryRun ? "dry run" : windowed ? "windowed run" : "run")}; log file {Log.FilePath}");
    Log.Info($"Windows {WindowsVersion()}, process DPI awareness {Native.DescribeAwareness(Native.GetThreadDpiAwarenessContext())}");

    var probe = DesktopProbe.Read();
    var detection = DesktopLayoutDetector.Detect(probe.Snapshot);
    Log.Info($"detected layout: {detection.Layout} ({detection.Reason}); SHELLDLL_DefView {Native.Hex(probe.DefView)} " +
             $"in {probe.Snapshot.DefViewHost}, ex=0x{probe.Snapshot.DefViewExStyle:X8}");
    foreach (var warning in detection.Warnings)
    {
        Log.Warn($"detection: {warning}");
    }

    DesktopProbe.LogTree(probe);
    var monitor = DesktopProbe.ActiveMonitor();
    Log.Info($"active monitor (under the cursor): {monitor.Bounds}, {monitor.Dpi} dpi ({monitor.Dpi * 100 / 96} %)");

    var layout = forcedLayout ?? detection.Layout;
    if (forcedLayout is not null)
    {
        Log.Warn($"layout forced by --layout: {layout}");
    }

    if (dryRun)
    {
        Log.Info(layout switch
        {
            DesktopLayout.Raised => $"dry run: would attach as a {(plainChild ? "plain" : "layered")} child of Progman " +
                                    "below SHELLDLL_DefView; nothing attached",
            DesktopLayout.Legacy => "dry run: would attach as a child of the WorkerW behind the SHELLDLL_DefView host; nothing attached",
            _ => "dry run: no attach path for an unknown layout; nothing attached",
        });
        return 0;
    }

    if (layout == DesktopLayout.Unknown && !windowed)
    {
        Log.Error("cannot attach: desktop layout unknown");
        return 1;
    }

    using var stop = new ManualResetEventSlim();
    using var cleanedUp = new ManualResetEventSlim();
    Action<PosixSignalContext> onSignal = context =>
    {
        Log.Info($"signal {context.Signal}: stopping");
        stop.Set();
        if (context.Signal == PosixSignal.SIGINT)
        {
            context.Cancel = true; // Ctrl+C: let the main loop clean up and return normally
        }
        else
        {
            // Console closed / logoff: Windows ends the process when this handler returns, so wait for cleanup.
            cleanedUp.Wait(TimeSpan.FromSeconds(4));
        }
    };
    using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, onSignal);
    using var sigquit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, onSignal);
    using var sighup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, onSignal);
    using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, onSignal);

    var attacher = new DesktopAttacher { LayeredChild = !plainChild };
    DCompPresenter? presenter = null;
    var exitCode = 0;
    try
    {
        presenter = AttachAndPresent(attacher, layout, monitor, windowed);
        if (presenter is null)
        {
            return exitCode = 1;
        }

        Log.Info(seconds is { } limit
            ? $"running for {limit} s (Ctrl+C or closing this console stops it earlier)"
            : "running; press Ctrl+C or close this console to stop");
        var clock = Stopwatch.StartNew();
        var frames = 0L;
        while (!stop.IsSet)
        {
            while (Native.PeekMessage(out var msg, 0, 0, 0, Native.PM_REMOVE))
            {
                if (msg.message == Native.WM_QUIT)
                {
                    stop.Set();
                }

                Native.TranslateMessage(msg);
                Native.DispatchMessage(msg);
            }

            if (stop.IsSet || (seconds is { } s && clock.Elapsed.TotalSeconds >= s))
            {
                break;
            }

            if (attacher.Lost)
            {
                presenter.Dispose();
                presenter = null;
                attacher.Detach();
                presenter = AttachAndPresent(attacher, layout, DesktopProbe.ActiveMonitor(), windowed);
                if (presenter is null)
                {
                    return exitCode = 1;
                }
            }

            if (!presenter.Render(clock.Elapsed.TotalSeconds))
            {
                return exitCode = 1;
            }

            frames++;
            unsafe
            {
                Native.MsgWaitForMultipleObjectsEx(0, null, 30, Native.QS_ALLINPUT, Native.MWMO_INPUTAVAILABLE);
            }
        }

        Log.Info($"stopping after {clock.Elapsed.TotalSeconds:F1} s, {frames} frames");
        return exitCode;
    }
    catch (Exception e)
    {
        Log.Error($"failed: {e}");
        return exitCode = 1;
    }
    finally
    {
        presenter?.Dispose();
        attacher.Dispose();
        Log.Info($"clean exit (code {exitCode}); the desktop should show the normal wallpaper again");
        cleanedUp.Set();
    }
}

static DCompPresenter? AttachAndPresent(
    DesktopAttacher attacher, DesktopLayout layout, DesktopProbe.MonitorTarget monitor, bool windowed)
{
    if (!(windowed ? attacher.CreateWindowed(monitor) : attacher.Attach(layout, monitor)))
    {
        return null;
    }

    return new DCompPresenter(attacher.Window, attacher.Width, attacher.Height);
}

static string WindowsVersion()
{
    using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
    var display = key?.GetValue("DisplayVersion") as string ?? "?";
    var build = key?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
    var revision = key?.GetValue("UBR") is int ubr ? ubr.ToString(CultureInfo.InvariantCulture) : "?";
    return $"{display} build {build}.{revision}";
}
