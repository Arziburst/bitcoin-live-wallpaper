namespace BitcoinWallpaper.Chart.Core.Desktop;

/// <summary>
/// A rectangle in virtual-screen coordinates, physical px (the host is PerMonitorV2, so GetMonitorInfo's
/// rcMonitor is already physical). Right/Bottom are exclusive, as in RECT.
/// </summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
}

/// <summary>One monitor as the host reads it (EnumDisplayMonitors + GetMonitorInfo + GetDpiForMonitor effective dpi).</summary>
public sealed record MonitorInfo(string DeviceName, ScreenRect Bounds, uint Dpi, bool IsPrimary);

/// <summary>
/// Where our window goes and how big the swap chain is. X/Y are relative to the virtual-screen origin (the
/// client origin of Progman/WorkerW, which span the virtual screen). Width/Height = swap-chain size in
/// physical px. Scale = Dpi / 96. An equal placement after a display change means nothing to do.
/// </summary>
public sealed record SurfacePlacement(string DeviceName, int X, int Y, int Width, int Height, uint Dpi, double Scale);

/// <summary>Which monitor the wallpaper lives on, and where and how big its surface is.</summary>
public static class DisplayDecisions
{
    private const uint DefaultDpi = 96;

    /// <summary>
    /// The owner uses one monitor at a time: one monitor -> it; several -> the one with IsPrimary
    /// (fallback when none is flagged: the one at (0,0), else the first); none -> null.
    /// </summary>
    public static MonitorInfo? SelectActive(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0)
        {
            return null;
        }

        if (monitors.Count == 1)
        {
            return monitors[0];
        }

        return monitors.FirstOrDefault(m => m.IsPrimary)
            ?? monitors.FirstOrDefault(m => m.Bounds.Left == 0 && m.Bounds.Top == 0)
            ?? monitors[0];
    }

    /// <summary>Union of all monitor bounds; default(ScreenRect) when the list is empty.</summary>
    public static ScreenRect VirtualScreen(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0)
        {
            return default;
        }

        var first = monitors[0].Bounds;
        int left = first.Left, top = first.Top, right = first.Right, bottom = first.Bottom;
        for (var i = 1; i < monitors.Count; i++)
        {
            var b = monitors[i].Bounds;
            left = Math.Min(left, b.Left);
            top = Math.Min(top, b.Top);
            right = Math.Max(right, b.Right);
            bottom = Math.Max(bottom, b.Bottom);
        }

        return new ScreenRect(left, top, right, bottom);
    }

    /// <summary>dpi / 96.0; a dpi of 0 (GetDpiForMonitor failed) counts as 96.</summary>
    public static double DpiScale(uint dpi) => (dpi == 0 ? DefaultDpi : dpi) / 96.0;

    /// <summary>The monitor's bounds in physical px, offset by the virtual-screen origin; Dpi 0 is reported as 96.</summary>
    public static SurfacePlacement Place(MonitorInfo monitor, ScreenRect virtualScreen)
    {
        var dpi = monitor.Dpi == 0 ? DefaultDpi : monitor.Dpi;
        return new SurfacePlacement(
            monitor.DeviceName,
            monitor.Bounds.Left - virtualScreen.Left,
            monitor.Bounds.Top - virtualScreen.Top,
            monitor.Bounds.Width,
            monitor.Bounds.Height,
            dpi,
            DpiScale(dpi));
    }

    /// <summary>Place(SelectActive(monitors), VirtualScreen(monitors)); null when there is no monitor.</summary>
    public static SurfacePlacement? Layout(IReadOnlyList<MonitorInfo> monitors)
    {
        var active = SelectActive(monitors);
        return active is null ? null : Place(active, VirtualScreen(monitors));
    }
}
