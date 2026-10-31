using System.Runtime.InteropServices;

namespace DesktopSpike;

/// <summary>Read-only look at the shell's desktop windows and the monitors. Changes nothing.</summary>
internal static class DesktopProbe
{
    public sealed record Probe(nint Progman, nint DefView, DesktopSnapshot Snapshot);

    public sealed record MonitorTarget(Native.RECT Bounds, uint Dpi);

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

    /// <summary>Logs Progman's children and the visible top-level WorkerW windows, for learning what the shell does.</summary>
    public static void LogTree(Probe probe)
    {
        if (probe.Progman == 0)
        {
            return;
        }

        Log.Info($"Progman {Describe(probe.Progman)}");
        foreach (var child in Children(probe.Progman))
        {
            Log.Info($"  child {child.ClassName,-18} {Describe(child.Handle)}");
        }

        var topLevel = Children(0);
        for (var i = 0; i < topLevel.Count; i++)
        {
            var w = topLevel[i];
            var visibleWorkerW = w.ClassName == DesktopLayoutDetector.WorkerWClass && (Native.StyleOf(w.Handle) & Native.WS_VISIBLE) != 0;
            if (visibleWorkerW || w.ClassName == DesktopLayoutDetector.ProgmanClass || w.HasDefViewChild)
            {
                var hosts = w.HasDefViewChild ? " hosts SHELLDLL_DefView" : "";
                Log.Info($"top-level #{i} {w.ClassName,-10} {Describe(w.Handle)}{hosts}");
            }
        }
    }

    public static string Describe(nint hwnd)
    {
        Native.GetWindowRect(hwnd, out var rect);
        return $"{Native.Hex(hwnd)} style=0x{Native.StyleOf(hwnd):X8} ex=0x{Native.ExStyleOf(hwnd):X8} rect={rect}";
    }

    /// <summary>The monitor under the mouse cursor (the one the owner is using), else the primary one.</summary>
    public static MonitorTarget ActiveMonitor()
    {
        Native.GetCursorPos(out var cursor);
        var monitor = Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTOPRIMARY);
        var info = new Native.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(monitor, ref info);
        var dpi = Native.GetDpiForMonitor(monitor, Native.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 ? dpiX : 96;
        return new MonitorTarget(info.rcMonitor, dpi);
    }
}
