namespace BitcoinWallpaper.Chart.Core.Desktop;

/// <summary>
/// What the host attached to (recorded at attach time).
/// Raised: Parent = Progman, ShellWorkerW = the shell's WorkerW child of Progman (0 when there was none).
/// Legacy: Parent = ShellWorkerW = the top-level WorkerW behind the SHELLDLL_DefView host.
/// </summary>
public sealed record AttachedState(DesktopLayout Layout, long Window, long Parent, long ShellWorkerW, uint ExplorerPid);

/// <summary>
/// What the host sees now (on a WorkerW destroy hook, TaskbarCreated, session unlock, resume, or the
/// watchdog), read with IsWindow / GetParent / GetClassName / GetWindow(GW_HWNDPREV) /
/// GetWindowThreadProcessId(Progman).
/// </summary>
/// <param name="ExplorerPid">Pid owning Progman now; 0 when there is no Progman.</param>
/// <param name="WindowAlive">IsWindow(attached.Window).</param>
/// <param name="ParentAlive">IsWindow(attached.Parent).</param>
/// <param name="ShellWorkerWAlive">IsWindow(attached.ShellWorkerW); ignored when ShellWorkerW was 0.</param>
/// <param name="CurrentParent">GetParent(our window) now; 0 when top-level or gone.</param>
/// <param name="CurrentParentClass">Class name of CurrentParent; null when 0.</param>
/// <param name="ClassAbove">Class of the sibling directly above our window; null when we are on top.</param>
public sealed record ShellObservation(
    uint ExplorerPid,
    bool WindowAlive,
    bool ParentAlive,
    bool ShellWorkerWAlive,
    long CurrentParent,
    string? CurrentParentClass,
    string? ClassAbove);

public enum ReattachReason
{
    None,
    ExplorerRestarted,
    ShellWorkerWGone,
    ParentGone,
    WindowGone,
    ParentChanged,
    ZOrderDrift,
}

/// <summary>Whether the attached wallpaper window still sits where it belongs in the shell's window tree.</summary>
public static class ReattachDecider
{
    /// <summary>None = stay attached; otherwise the first reason that applies, in the enum's order.</summary>
    public static ReattachReason Decide(AttachedState attached, ShellObservation now)
    {
        if (now.ExplorerPid != attached.ExplorerPid)
        {
            return ReattachReason.ExplorerRestarted;
        }

        if (attached.ShellWorkerW != 0 && !now.ShellWorkerWAlive)
        {
            return ReattachReason.ShellWorkerWGone;
        }

        if (!now.ParentAlive)
        {
            return ReattachReason.ParentGone;
        }

        if (!now.WindowAlive)
        {
            return ReattachReason.WindowGone;
        }

        if (now.CurrentParent != attached.Parent || now.CurrentParentClass != ExpectedParentClass(attached.Layout))
        {
            return ReattachReason.ParentChanged;
        }

        if (attached.Layout == DesktopLayout.Raised && now.ClassAbove != DesktopLayoutDetector.DefViewClass)
        {
            return ReattachReason.ZOrderDrift;
        }

        return ReattachReason.None;
    }

    private static string? ExpectedParentClass(DesktopLayout layout) => layout switch
    {
        DesktopLayout.Raised => DesktopLayoutDetector.ProgmanClass,
        DesktopLayout.Legacy => DesktopLayoutDetector.WorkerWClass,
        _ => null,
    };
}
