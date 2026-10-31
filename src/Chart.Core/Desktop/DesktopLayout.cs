namespace BitcoinWallpaper.Chart.Core.Desktop;

/// <summary>How the shell builds the desktop window tree.</summary>
public enum DesktopLayout
{
    /// <summary>No Progman window: explorer is not running (or not yet started).</summary>
    Unknown,

    /// <summary>
    /// Up to 23H2: Progman message 0x052C spawns a top-level WorkerW behind the top-level window that
    /// hosts SHELLDLL_DefView; we become a child of that WorkerW.
    /// </summary>
    Legacy,

    /// <summary>
    /// 24H2+ "raised desktop": Progman has WS_EX_NOREDIRECTIONBITMAP, SHELLDLL_DefView is a layered child
    /// of Progman and the wallpaper WorkerW is a child of Progman as well; we become a layered child of
    /// Progman between the two.
    /// </summary>
    Raised,
}

/// <summary>Where SHELLDLL_DefView (the icon view) was found.</summary>
public enum DefViewHost
{
    NotFound,
    Progman,
    TopLevelWorkerW,
}

/// <summary>What the host reads from the real window tree; tests build it by hand.</summary>
/// <param name="ProgmanFound">A top-level window of class Progman exists.</param>
/// <param name="ProgmanExStyle">Progman's GWL_EXSTYLE.</param>
/// <param name="DefViewHost">The parent of SHELLDLL_DefView.</param>
/// <param name="DefViewExStyle">SHELLDLL_DefView's GWL_EXSTYLE (0 when not found).</param>
public sealed record DesktopSnapshot(
    bool ProgmanFound,
    uint ProgmanExStyle,
    DefViewHost DefViewHost,
    uint DefViewExStyle);

/// <summary>The detected layout, why, and any signal that does not fit it.</summary>
public sealed record LayoutDetection(DesktopLayout Layout, string Reason, IReadOnlyList<string> Warnings);

/// <summary>A window as seen while walking a z-ordered list (top first).</summary>
/// <param name="HasDefViewChild">The window has a direct SHELLDLL_DefView child.</param>
public sealed record WindowEntry(long Handle, string ClassName, bool HasDefViewChild = false);

/// <summary>Raised layout: insert our window right below DefView and keep the shell's WorkerW below ours.</summary>
public sealed record RaisedAttachTargets(long DefView, long WorkerW);

/// <summary>Moved from tools/DesktopSpike/DesktopLayout.cs (the spike keeps its own copy and tests).</summary>
public static class DesktopLayoutDetector
{
    public const uint WS_EX_LAYERED = 0x0008_0000;
    public const uint WS_EX_NOREDIRECTIONBITMAP = 0x0020_0000;

    public const string ProgmanClass = "Progman";
    public const string WorkerWClass = "WorkerW";
    public const string DefViewClass = "SHELLDLL_DefView";

    /// <summary>
    /// The layout is decided by one signal, Progman's WS_EX_NOREDIRECTIONBITMAP; the DefView parent and
    /// style only produce warnings when they disagree with it.
    /// </summary>
    public static LayoutDetection Detect(DesktopSnapshot snapshot)
    {
        if (!snapshot.ProgmanFound)
        {
            return new LayoutDetection(DesktopLayout.Unknown, "no Progman window (explorer not running?)", []);
        }

        var warnings = new List<string>();
        if (snapshot.DefViewHost == DefViewHost.NotFound)
        {
            warnings.Add("SHELLDLL_DefView not found under Progman or any top-level WorkerW");
        }

        if ((snapshot.ProgmanExStyle & WS_EX_NOREDIRECTIONBITMAP) != 0)
        {
            if (snapshot.DefViewHost == DefViewHost.TopLevelWorkerW)
            {
                warnings.Add("raised flag set but SHELLDLL_DefView lives in a top-level WorkerW");
            }
            else if (snapshot.DefViewHost == DefViewHost.Progman && (snapshot.DefViewExStyle & WS_EX_LAYERED) == 0)
            {
                warnings.Add("raised flag set but SHELLDLL_DefView is not WS_EX_LAYERED");
            }

            return new LayoutDetection(
                DesktopLayout.Raised,
                $"Progman ex-style 0x{snapshot.ProgmanExStyle:X8} has WS_EX_NOREDIRECTIONBITMAP",
                warnings);
        }

        return new LayoutDetection(
            DesktopLayout.Legacy,
            $"Progman ex-style 0x{snapshot.ProgmanExStyle:X8} has no WS_EX_NOREDIRECTIONBITMAP",
            warnings);
    }

    /// <summary>
    /// Legacy layout: the WorkerW to parent into is the first top-level WorkerW after the window that
    /// hosts SHELLDLL_DefView in z-order. Returns 0 when there is none (0x052C not handled yet).
    /// </summary>
    public static long FindLegacyWorkerW(IReadOnlyList<WindowEntry> topLevelWindows)
    {
        var hostIndex = -1;
        for (var i = 0; i < topLevelWindows.Count; i++)
        {
            if (topLevelWindows[i].HasDefViewChild)
            {
                hostIndex = i;
                break;
            }
        }

        if (hostIndex < 0)
        {
            return 0;
        }

        for (var i = hostIndex + 1; i < topLevelWindows.Count; i++)
        {
            if (topLevelWindows[i].ClassName == WorkerWClass)
            {
                return topLevelWindows[i].Handle;
            }
        }

        return 0;
    }

    /// <summary>
    /// Raised layout: from Progman's direct children (z-order, top first) pick SHELLDLL_DefView and the
    /// shell's WorkerW. Returns null without a DefView; WorkerW is 0 when the shell has none yet.
    /// </summary>
    public static RaisedAttachTargets? FindRaisedTargets(IReadOnlyList<WindowEntry> progmanChildren)
    {
        var defView = progmanChildren.FirstOrDefault(w => w.ClassName == DefViewClass);
        if (defView is null)
        {
            return null;
        }

        var workerW = progmanChildren.FirstOrDefault(w => w.ClassName == WorkerWClass);
        return new RaisedAttachTargets(defView.Handle, workerW?.Handle ?? 0);
    }
}
