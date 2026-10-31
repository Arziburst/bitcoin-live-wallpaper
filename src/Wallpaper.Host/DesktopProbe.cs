using System.Runtime.InteropServices;
using BitcoinWallpaper.Chart.Core.Desktop;
using BitcoinWallpaper.Chart.Core.Pause;

namespace BitcoinWallpaper.Host;

/// <summary>Read-only look at the shell's desktop windows and the monitors. Changes nothing.</summary>
internal static unsafe class DesktopProbe
{
    public sealed record Probe(nint Progman, nint DefView, DesktopSnapshot Snapshot);

    public static Probe Read()
    {
        var progman = Native.FindWindow(DesktopLayoutDetector.ProgmanClass, null);
        if (progman == 0)
        {
            return new Probe(0, 0, new DesktopSnapshot(false, 0, DefViewHost.NotFound, 0));
        }

        var host = DefViewHost.NotFound;
        var defView = Native.FindWindowEx(progman, 0, DesktopLayoutDetector.DefViewClass, null);
        if (defView != 0)
        {
            host = DefViewHost.Progman;
        }
        else
        {
            nint workerW = 0;
            while ((workerW = Native.FindWindowEx(0, workerW, DesktopLayoutDetector.WorkerWClass, null)) != 0)
            {
                defView = Native.FindWindowEx(workerW, 0, DesktopLayoutDetector.DefViewClass, null);
                if (defView != 0)
                {
                    host = DefViewHost.TopLevelWorkerW;
                    break;
                }
            }
        }

        var snapshot = new DesktopSnapshot(
            true,
            Native.ExStyleOf(progman),
            host,
            defView == 0 ? 0 : Native.ExStyleOf(defView));
        return new Probe(progman, defView, snapshot);
    }

    /// <summary>The pid owning Progman now; 0 when there is no Progman.</summary>
    public static uint ExplorerPid()
    {
        var progman = Native.FindWindow(DesktopLayoutDetector.ProgmanClass, null);
        if (progman == 0)
        {
            return 0;
        }

        Native.GetWindowThreadProcessId(progman, out var pid);
        return pid;
    }

    /// <summary>Direct children of <paramref name="parent"/> in z-order, top first; 0 means top-level windows.</summary>
    public static List<WindowEntry> Children(nint parent)
    {
        var result = new List<WindowEntry>();
        nint child = 0;
        while ((child = Native.FindWindowEx(parent, child, null, null)) != 0)
        {
            var hasDefView = Native.FindWindowEx(child, 0, DesktopLayoutDetector.DefViewClass, null) != 0;
            result.Add(new WindowEntry(child, Native.ClassNameOf(child), hasDefView));
        }

        return result;
    }

    /// <summary>How the attached window sits in the shell's tree now, for <see cref="ReattachDecider"/>.</summary>
    public static ShellObservation Observe(AttachedState attached)
    {
        var window = (nint)attached.Window;
        var windowAlive = window != 0 && Native.IsWindow(window);
        var parent = windowAlive ? Native.GetParent(window) : 0;
        var above = windowAlive ? Native.GetWindow(window, Native.GW_HWNDPREV) : 0;
        return new ShellObservation(
            ExplorerPid(),
            windowAlive,
            attached.Parent != 0 && Native.IsWindow((nint)attached.Parent),
            attached.ShellWorkerW != 0 && Native.IsWindow((nint)attached.ShellWorkerW),
            parent,
            parent == 0 ? null : Native.ClassNameOf(parent),
            above == 0 ? null : Native.ClassNameOf(above));
    }

    public static string Describe(nint hwnd)
    {
        Native.GetWindowRect(hwnd, out var rect);
        return $"{Native.Hex(hwnd)} style=0x{Native.StyleOf(hwnd):X8} ex=0x{Native.ExStyleOf(hwnd):X8} rect={rect}";
    }

    /// <summary>All monitors, physical px (the process is PerMonitorV2), with their effective dpi.</summary>
    public static List<MonitorInfo> Monitors()
    {
        var list = new List<MonitorInfo>();
        var handle = GCHandle.Alloc(list);
        try
        {
            Native.EnumDisplayMonitors(0, null, &OnMonitor, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return list;
    }

    /// <summary>The work area (rcWork: the monitor without the taskbar and docked bars, D-37) of the monitor with these
    /// bounds, virtual-screen physical px; the bounds themselves when the monitor cannot be read.</summary>
    public static PixelRect WorkArea(ScreenRect monitorBounds)
    {
        var bounds = new PixelRect(monitorBounds.Left, monitorBounds.Top, monitorBounds.Right, monitorBounds.Bottom);
        var rect = new Native.RECT { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom };
        var monitor = Native.MonitorFromRect(rect, Native.MONITOR_DEFAULTTONULL);
        var info = new Native.MONITORINFOEXW { cbSize = (uint)sizeof(Native.MONITORINFOEXW) };
        if (monitor == 0 || !Native.GetMonitorInfo(monitor, &info))
        {
            return bounds;
        }

        var w = info.rcWork;
        return new PixelRect(w.Left, w.Top, w.Right, w.Bottom);
    }

    [UnmanagedCallersOnly]
    private static int OnMonitor(nint monitor, nint hdc, Native.RECT* clip, nint data)
    {
        var list = (List<MonitorInfo>)GCHandle.FromIntPtr(data).Target!;
        var info = new Native.MONITORINFOEXW { cbSize = (uint)sizeof(Native.MONITORINFOEXW) };
        if (!Native.GetMonitorInfo(monitor, &info))
        {
            return 1;
        }

        var dpi = Native.GetDpiForMonitor(monitor, Native.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 ? dpiX : 0;
        var r = info.rcMonitor;
        list.Add(new MonitorInfo(
            new string(info.szDevice),
            new ScreenRect(r.Left, r.Top, r.Right, r.Bottom),
            dpi,
            (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0));
        return 1;
    }
}
