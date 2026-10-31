using BitcoinWallpaper.Chart.Core.Desktop;

namespace BitcoinWallpaper.Chart.Core.Tests;

// Contract #6, criterion 1: the pure decisions of the desktop host live in src/Chart.Core/Desktop
// (namespace BitcoinWallpaper.Chart.Core.Desktop, no Win32 types): which monitor is active after a display
// change, whether to re-attach (WorkerW gone, explorer pid changed, parent or z-order drift), the new
// swap-chain size in physical px and the DPI scale.
//
// API the worker implements exactly as written (written before the code; nothing else is assumed):
//
//   // Moved from tools/DesktopSpike/DesktopLayout.cs (same members; the spike keeps its own copy and tests).
//   // The rest of the spike's detector may move alongside (handles as long, styles as uint); not tested here.
//   public enum DesktopLayout { Unknown, Legacy, Raised }
//
//   // A rectangle in virtual-screen coordinates, physical px (the host is PerMonitorV2, so GetMonitorInfo's
//   // rcMonitor is already physical). Right/Bottom are exclusive, as in RECT.
//   public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
//   {
//       public int Width => Right - Left;
//       public int Height => Bottom - Top;
//   }
//
//   // One monitor as the host reads it (EnumDisplayMonitors + GetMonitorInfo + GetDpiForMonitor effective dpi).
//   public sealed record MonitorInfo(string DeviceName, ScreenRect Bounds, uint Dpi, bool IsPrimary);
//
//   // Where our window goes and how big the swap chain is. X/Y are relative to the virtual-screen origin
//   // (the client origin of Progman/WorkerW, which span the virtual screen), so never negative for a
//   // monitor inside the virtual screen. Width/Height = swap-chain size in physical px. Scale = Dpi / 96.
//   // Value equality: an equal placement after a display change means nothing to do (no resize, no Present).
//   public sealed record SurfacePlacement(string DeviceName, int X, int Y, int Width, int Height, uint Dpi, double Scale);
//
//   public static class DisplayDecisions
//   {
//       // The owner uses one monitor at a time: one monitor -> it; several -> the one with IsPrimary
//       // (fallback when none is flagged: the one at (0,0), else the first); none -> null.
//       public static MonitorInfo? SelectActive(IReadOnlyList<MonitorInfo> monitors);
//       // Union of all monitor bounds; default(ScreenRect) when the list is empty.
//       public static ScreenRect VirtualScreen(IReadOnlyList<MonitorInfo> monitors);
//       // dpi / 96.0; a dpi of 0 (GetDpiForMonitor failed) counts as 96.
//       public static double DpiScale(uint dpi);
//       // The monitor's bounds in physical px, offset by the virtual-screen origin; Dpi 0 is reported as 96.
//       public static SurfacePlacement Place(MonitorInfo monitor, ScreenRect virtualScreen);
//       // Place(SelectActive(monitors), VirtualScreen(monitors)); null when there is no monitor.
//       public static SurfacePlacement? Layout(IReadOnlyList<MonitorInfo> monitors);
//   }
//
//   // What the host attached to (recorded at attach time).
//   //   Raised: Parent = Progman, ShellWorkerW = the shell's WorkerW child of Progman (0 when there was none).
//   //   Legacy: Parent = ShellWorkerW = the top-level WorkerW behind the SHELLDLL_DefView host.
//   public sealed record AttachedState(DesktopLayout Layout, long Window, long Parent, long ShellWorkerW, uint ExplorerPid);
//
//   // What the host sees now (on a WorkerW destroy hook, TaskbarCreated, WM_WTSSESSION_CHANGE unlock,
//   // WM_POWERBROADCAST resume, or the watchdog). Plain values read with IsWindow / GetParent /
//   // GetClassName / GetWindow(GW_HWNDPREV) / GetWindowThreadProcessId(Progman).
//   public sealed record ShellObservation(
//       uint ExplorerPid,            // pid owning Progman now; 0 when there is no Progman
//       bool WindowAlive,            // IsWindow(attached.Window)
//       bool ParentAlive,            // IsWindow(attached.Parent)
//       bool ShellWorkerWAlive,      // IsWindow(attached.ShellWorkerW); ignored when ShellWorkerW was 0
//       long CurrentParent,          // GetParent(our window) now; 0 when top-level or gone
//       string? CurrentParentClass,  // class name of CurrentParent; null when 0
//       string? ClassAbove);         // class of the sibling directly above our window; null when we are on top
//
//   public enum ReattachReason { None, ExplorerRestarted, ShellWorkerWGone, ParentGone, WindowGone, ParentChanged, ZOrderDrift }
//
//   public static class ReattachDecider
//   {
//       // None = stay attached, do nothing. Otherwise the first that applies, in this order:
//       //   ExplorerRestarted  now.ExplorerPid != attached.ExplorerPid
//       //   ShellWorkerWGone   attached.ShellWorkerW != 0 and !now.ShellWorkerWAlive
//       //   ParentGone         !now.ParentAlive
//       //   WindowGone         !now.WindowAlive
//       //   ParentChanged      now.CurrentParent != attached.Parent, or CurrentParentClass is not
//       //                      "Progman" (Raised) / "WorkerW" (Legacy)
//       //   ZOrderDrift        Raised only: ClassAbove != "SHELLDLL_DefView"
//       public static ReattachReason Decide(AttachedState attached, ShellObservation now);
//   }
public class DesktopHostDecisionTests
{
    // The owner's monitors (D-3): Samsung C49RG9x 5120x1440 at 175 % (168 dpi), Xiaomi TV 3840x2160 at
    // 150 % (144 dpi) or 100 % (96 dpi).
    private const uint Dpi175 = 168;
    private const uint Dpi150 = 144;
    private const uint Dpi125 = 120;
    private const uint Dpi100 = 96;

    private static MonitorInfo Samsung(int left = 0, int top = 0, bool primary = true, uint dpi = Dpi175) =>
        new(@"\\.\DISPLAY1", new ScreenRect(left, top, left + 5120, top + 1440), dpi, primary);

    private static MonitorInfo Tv(int left = 0, int top = 0, bool primary = true, uint dpi = Dpi150) =>
        new(@"\\.\DISPLAY2", new ScreenRect(left, top, left + 3840, top + 2160), dpi, primary);

    private static SurfacePlacement LayoutOf(params MonitorInfo[] monitors)
    {
        var placement = DisplayDecisions.Layout(monitors);
        Assert.NotNull(placement);
        return placement;
    }

    // ---- (1) which monitor is active after a display change -------------------------------------------

    [Fact]
    public void Single_samsung_is_active()
    {
        var active = DisplayDecisions.SelectActive([Samsung()]);

        Assert.NotNull(active);
        Assert.Equal(@"\\.\DISPLAY1", active.DeviceName);
    }

    [Fact]
    public void Switch_from_samsung_to_tv_only_makes_the_tv_active()
    {
        // Win+P "second screen only" or the TV input: the Samsung disappears, the TV becomes the primary at (0,0).
        var before = DisplayDecisions.SelectActive([Samsung()]);
        var after = DisplayDecisions.SelectActive([Tv()]);

        Assert.Equal(@"\\.\DISPLAY1", before?.DeviceName);
        Assert.Equal(@"\\.\DISPLAY2", after?.DeviceName);
    }

    [Fact]
    public void Two_monitors_at_once_pick_the_primary_even_when_it_is_listed_second()
    {
        MonitorInfo[] monitors = [Tv(left: 5120, primary: false), Samsung(primary: true)];

        var active = DisplayDecisions.SelectActive(monitors);

        Assert.Equal(@"\\.\DISPLAY1", active?.DeviceName);
    }

    [Fact]
    public void Two_monitors_with_the_tv_primary_pick_the_tv()
    {
        MonitorInfo[] monitors = [Samsung(left: -5120, primary: false), Tv(primary: true)];

        var active = DisplayDecisions.SelectActive(monitors);

        Assert.Equal(@"\\.\DISPLAY2", active?.DeviceName);
    }

    [Fact]
    public void Removing_the_active_monitor_moves_to_the_remaining_one()
    {
        // We were on the TV (primary), the TV goes off, Windows makes the Samsung the primary at (0,0).
        var before = DisplayDecisions.SelectActive([Tv(primary: true), Samsung(left: 3840, primary: false)]);
        var after = DisplayDecisions.SelectActive([Samsung(primary: true)]);

        Assert.Equal(@"\\.\DISPLAY2", before?.DeviceName);
        Assert.Equal(@"\\.\DISPLAY1", after?.DeviceName);
    }

    [Fact]
    public void Removing_the_other_monitor_keeps_the_active_one_and_its_placement()
    {
        var before = LayoutOf(Samsung(primary: true), Tv(left: 5120, primary: false));
        var after = LayoutOf(Samsung(primary: true));

        Assert.Equal(@"\\.\DISPLAY1", after.DeviceName);
        Assert.Equal(before, after);
    }

    [Fact]
    public void No_monitor_means_no_active_monitor_and_no_layout()
    {
        Assert.Null(DisplayDecisions.SelectActive([]));
        Assert.Null(DisplayDecisions.Layout([]));
    }

    [Fact]
    public void Resolution_change_on_the_same_monitor_changes_the_swap_chain_size()
    {
        var before = LayoutOf(Samsung());
        var after = LayoutOf(new MonitorInfo(@"\\.\DISPLAY1", new ScreenRect(0, 0, 3840, 1080), Dpi175, true));

        Assert.Equal(@"\\.\DISPLAY1", after.DeviceName);
        Assert.Equal((3840, 1080), (after.Width, after.Height));
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Dpi_change_on_the_same_monitor_changes_the_scale_but_not_the_size()
    {
        var before = LayoutOf(Samsung(dpi: Dpi175));
        var after = LayoutOf(Samsung(dpi: Dpi125));

        Assert.Equal((5120, 1440), (after.Width, after.Height));
        Assert.Equal(1.25, after.Scale);
        Assert.Equal(Dpi125, after.Dpi);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Nothing_changed_gives_an_equal_placement()
    {
        // Two separately built but identical enumerations: the host compares and does nothing (no Present).
        var first = LayoutOf(Samsung(), Tv(left: 5120, primary: false));
        var second = LayoutOf(Samsung(), Tv(left: 5120, primary: false));

        Assert.Equal(first, second);
    }

    // ---- (3) swap-chain size in physical px, DPI scale, position relative to the virtual screen ------

    [Theory]
    [InlineData(168u, 1.75)]
    [InlineData(144u, 1.5)]
    [InlineData(120u, 1.25)]
    [InlineData(96u, 1.0)]
    [InlineData(192u, 2.0)]
    public void Dpi_scale_is_dpi_over_96(uint dpi, double expected)
    {
        Assert.Equal(expected, DisplayDecisions.DpiScale(dpi));
    }

    [Fact]
    public void Dpi_zero_counts_as_96()
    {
        Assert.Equal(1.0, DisplayDecisions.DpiScale(0));
    }

    [Fact]
    public void Samsung_at_175_percent_gets_a_full_physical_5120x1440_swap_chain()
    {
        var placement = LayoutOf(Samsung(dpi: Dpi175));

        Assert.Equal(new SurfacePlacement(@"\\.\DISPLAY1", 0, 0, 5120, 1440, Dpi175, 1.75), placement);
    }

    [Fact]
    public void Tv_at_100_percent_gets_a_full_physical_3840x2160_swap_chain()
    {
        var placement = LayoutOf(Tv(dpi: Dpi100));

        Assert.Equal(new SurfacePlacement(@"\\.\DISPLAY2", 0, 0, 3840, 2160, Dpi100, 1.0), placement);
    }

    [Fact]
    public void Tv_at_150_percent_keeps_its_physical_size()
    {
        var placement = LayoutOf(Tv(dpi: Dpi150));

        Assert.Equal((3840, 2160), (placement.Width, placement.Height));
        Assert.Equal(1.5, placement.Scale);
    }

    [Fact]
    public void Placement_reports_dpi_zero_as_96()
    {
        var placement = DisplayDecisions.Place(Samsung(dpi: 0), new ScreenRect(0, 0, 5120, 1440));

        Assert.Equal(Dpi100, placement.Dpi);
        Assert.Equal(1.0, placement.Scale);
    }

    [Fact]
    public void Virtual_screen_is_the_union_of_monitors_including_negative_coordinates()
    {
        var virtualScreen = DisplayDecisions.VirtualScreen([Tv(left: -3840, primary: false), Samsung()]);

        Assert.Equal(new ScreenRect(-3840, 0, 5120, 2160), virtualScreen);
        Assert.Equal((8960, 2160), (virtualScreen.Width, virtualScreen.Height));
    }

    [Fact]
    public void Virtual_screen_of_no_monitor_is_empty()
    {
        Assert.Equal(default(ScreenRect), DisplayDecisions.VirtualScreen([]));
    }

    [Fact]
    public void Primary_with_a_monitor_to_its_left_sits_right_of_the_virtual_origin()
    {
        // TV left of the Samsung: screen coordinates of the TV are negative, the parent's client origin is the
        // TV's left edge, so our window goes at x = 3840 in the parent.
        var placement = LayoutOf(Tv(left: -3840, primary: false), Samsung());

        Assert.Equal(new SurfacePlacement(@"\\.\DISPLAY1", 3840, 0, 5120, 1440, Dpi175, 1.75), placement);
    }

    [Fact]
    public void Primary_with_a_monitor_above_sits_below_the_virtual_origin()
    {
        var placement = LayoutOf(Tv(left: 0, top: -2160, primary: false), Samsung());

        Assert.Equal((0, 2160), (placement.X, placement.Y));
        Assert.Equal((5120, 1440), (placement.Width, placement.Height));
    }

    [Fact]
    public void Primary_with_a_monitor_to_its_right_stays_at_the_origin()
    {
        var placement = LayoutOf(Samsung(), Tv(left: 5120, primary: false));

        Assert.Equal((0, 0), (placement.X, placement.Y));
    }

    [Fact]
    public void Monitor_with_negative_screen_coordinates_is_placed_from_the_virtual_origin()
    {
        var tv = Tv(left: -3840, top: -720, primary: false);
        var virtualScreen = new ScreenRect(-3840, -720, 5120, 1440);

        var placement = DisplayDecisions.Place(tv, virtualScreen);

        Assert.Equal(new SurfacePlacement(@"\\.\DISPLAY2", 0, 0, 3840, 2160, Dpi150, 1.5), placement);
    }

    [Fact]
    public void Removing_a_monitor_on_the_left_shifts_the_placement_of_the_same_active_monitor()
    {
        // The active monitor's own bounds do not change, but the virtual origin does: the host must move us.
        var before = LayoutOf(Tv(left: -3840, primary: false), Samsung());
        var after = LayoutOf(Samsung());

        Assert.Equal(3840, before.X);
        Assert.Equal(0, after.X);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Switching_samsung_to_tv_changes_size_scale_and_device()
    {
        var before = LayoutOf(Samsung(dpi: Dpi175));
        var after = LayoutOf(Tv(dpi: Dpi150));

        Assert.Equal(new SurfacePlacement(@"\\.\DISPLAY1", 0, 0, 5120, 1440, Dpi175, 1.75), before);
        Assert.Equal(new SurfacePlacement(@"\\.\DISPLAY2", 0, 0, 3840, 2160, Dpi150, 1.5), after);
    }

    // ---- (2) whether to re-attach ----------------------------------------------------------------------

    private const uint ExplorerPid = 4242;
    private const long OurWindow = 0x0003_0A1C;
    private const long Progman = 0x0001_0010;
    private const long RaisedWorkerW = 0x0001_0022;
    private const long LegacyWorkerW = 0x0002_0040;

    private static AttachedState RaisedAttached(long shellWorkerW = RaisedWorkerW) =>
        new(DesktopLayout.Raised, OurWindow, Progman, shellWorkerW, ExplorerPid);

    private static AttachedState LegacyAttached() =>
        new(DesktopLayout.Legacy, OurWindow, LegacyWorkerW, LegacyWorkerW, ExplorerPid);

    private static ShellObservation RaisedHealthy() =>
        new(ExplorerPid, WindowAlive: true, ParentAlive: true, ShellWorkerWAlive: true,
            CurrentParent: Progman, CurrentParentClass: "Progman", ClassAbove: "SHELLDLL_DefView");

    private static ShellObservation LegacyHealthy() =>
        new(ExplorerPid, WindowAlive: true, ParentAlive: true, ShellWorkerWAlive: true,
            CurrentParent: LegacyWorkerW, CurrentParentClass: "WorkerW", ClassAbove: null);

    private static ShellObservation EverythingGone(uint pid) =>
        new(pid, WindowAlive: false, ParentAlive: false, ShellWorkerWAlive: false,
            CurrentParent: 0, CurrentParentClass: null, ClassAbove: null);

    [Fact]
    public void Raised_nothing_changed_does_not_reattach()
    {
        Assert.Equal(ReattachReason.None, ReattachDecider.Decide(RaisedAttached(), RaisedHealthy()));
    }

    [Fact]
    public void Legacy_nothing_changed_does_not_reattach()
    {
        Assert.Equal(ReattachReason.None, ReattachDecider.Decide(LegacyAttached(), LegacyHealthy()));
    }

    [Fact]
    public void Taskbar_created_unlock_or_resume_with_the_same_explorer_and_live_windows_does_not_reattach()
    {
        // TaskbarCreated also arrives without an explorer restart; unlock and resume usually leave the tree
        // intact. The same pid and live, correctly placed windows mean: stay.
        var now = RaisedHealthy() with { ExplorerPid = ExplorerPid };

        Assert.Equal(ReattachReason.None, ReattachDecider.Decide(RaisedAttached(), now));
    }

    [Fact]
    public void Raised_attached_without_a_shell_workerw_does_not_reattach_for_it()
    {
        var now = RaisedHealthy() with { ShellWorkerWAlive = false };

        Assert.Equal(ReattachReason.None, ReattachDecider.Decide(RaisedAttached(shellWorkerW: 0), now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("SHELLDLL_DefView")]
    [InlineData("WorkerW")]
    [InlineData("SomeChild")]
    public void Legacy_ignores_the_sibling_order(string? classAbove)
    {
        var now = LegacyHealthy() with { ClassAbove = classAbove };

        Assert.Equal(ReattachReason.None, ReattachDecider.Decide(LegacyAttached(), now));
    }

    [Fact]
    public void Raised_shell_workerw_destroyed_reattaches()
    {
        // The EVENT_OBJECT_DESTROY hook fired for the shell's WorkerW (e.g. the owner changed the wallpaper in
        // Settings); Progman and our window are still alive.
        var now = RaisedHealthy() with { ShellWorkerWAlive = false };

        Assert.Equal(ReattachReason.ShellWorkerWGone, ReattachDecider.Decide(RaisedAttached(), now));
    }

    [Fact]
    public void Legacy_workerw_destroyed_with_our_child_window_reattaches_as_workerw_gone()
    {
        var now = LegacyHealthy() with
        {
            WindowAlive = false, ParentAlive = false, ShellWorkerWAlive = false,
            CurrentParent = 0, CurrentParentClass = null,
        };

        Assert.Equal(ReattachReason.ShellWorkerWGone, ReattachDecider.Decide(LegacyAttached(), now));
    }

    [Fact]
    public void Explorer_restart_reattaches_as_explorer_restarted()
    {
        // TaskbarCreated after Task Manager -> Windows Explorer -> Restart: new pid, the old tree is gone.
        var now = EverythingGone(pid: 9999);

        Assert.Equal(ReattachReason.ExplorerRestarted, ReattachDecider.Decide(RaisedAttached(), now));
        Assert.Equal(ReattachReason.ExplorerRestarted, ReattachDecider.Decide(LegacyAttached(), now));
    }

    [Fact]
    public void Explorer_pid_change_reattaches_even_when_old_handles_still_look_alive()
    {
        // Handle values can be reused by the new explorer; the pid is the signal.
        var now = RaisedHealthy() with { ExplorerPid = 9999 };

        Assert.Equal(ReattachReason.ExplorerRestarted, ReattachDecider.Decide(RaisedAttached(), now));
    }

    [Fact]
    public void Explorer_not_running_yet_reattaches()
    {
        // Mid-restart: no Progman, pid 0. The host keeps trying until the shell is back.
        Assert.NotEqual(ReattachReason.None, ReattachDecider.Decide(RaisedAttached(), EverythingGone(pid: 0)));
    }

    [Fact]
    public void Our_window_destroyed_alone_reattaches_as_window_gone()
    {
        var now = RaisedHealthy() with
        {
            WindowAlive = false, CurrentParent = 0, CurrentParentClass = null, ClassAbove = null,
        };

        Assert.Equal(ReattachReason.WindowGone, ReattachDecider.Decide(RaisedAttached(), now));
    }

    [Fact]
    public void Raised_progman_dead_while_our_window_still_answers_reattaches_as_parent_gone()
    {
        var now = RaisedHealthy() with { ParentAlive = false };

        Assert.Equal(ReattachReason.ParentGone, ReattachDecider.Decide(RaisedAttached(), now));
    }

    [Fact]
    public void Session_unlocked_with_a_dead_parent_reattaches()
    {
        // WM_WTSSESSION_CHANGE / WTS_SESSION_UNLOCK: same explorer, but the desktop tree we hung on is gone.
        var raised = RaisedHealthy() with
        {
            WindowAlive = false, ParentAlive = false, ShellWorkerWAlive = false,
            CurrentParent = 0, CurrentParentClass = null, ClassAbove = null,
        };
        var legacy = LegacyHealthy() with
        {
            WindowAlive = false, ParentAlive = false, ShellWorkerWAlive = false,
            CurrentParent = 0, CurrentParentClass = null,
        };

        Assert.NotEqual(ReattachReason.None, ReattachDecider.Decide(RaisedAttached(), raised));
        Assert.NotEqual(ReattachReason.None, ReattachDecider.Decide(LegacyAttached(), legacy));
    }

    [Fact]
    public void Resumed_from_sleep_with_a_dead_parent_but_our_window_still_listed_reattaches()
    {
        // WM_POWERBROADCAST / PBT_APMRESUMEAUTOMATIC: our window handle still answers, its parent does not.
        var now = RaisedHealthy() with { ParentAlive = false, CurrentParent = 0, CurrentParentClass = null };

        Assert.NotEqual(ReattachReason.None, ReattachDecider.Decide(RaisedAttached(), now));
    }

    [Fact]
    public void Raised_window_became_top_level_reattaches_as_parent_changed()
    {
        var now = RaisedHealthy() with { CurrentParent = 0, CurrentParentClass = null, ClassAbove = null };

        Assert.Equal(ReattachReason.ParentChanged, ReattachDecider.Decide(RaisedAttached(), now));
    }

    [Fact]
    public void Raised_parent_no_longer_progman_reattaches_as_parent_changed()
    {
        var now = RaisedHealthy() with { CurrentParent = RaisedWorkerW, CurrentParentClass = "WorkerW" };

        Assert.Equal(ReattachReason.ParentChanged, ReattachDecider.Decide(RaisedAttached(), now));
    }

    [Fact]
    public void Legacy_parent_no_longer_a_workerw_reattaches_as_parent_changed()
    {
        var now = LegacyHealthy() with { CurrentParent = Progman, CurrentParentClass = "Progman" };

        Assert.Equal(ReattachReason.ParentChanged, ReattachDecider.Decide(LegacyAttached(), now));
    }

    [Fact]
    public void Legacy_moved_to_another_workerw_reattaches_as_parent_changed()
    {
        var now = LegacyHealthy() with { CurrentParent = 0x0002_0099, CurrentParentClass = "WorkerW" };

        Assert.Equal(ReattachReason.ParentChanged, ReattachDecider.Decide(LegacyAttached(), now));
    }

    [Fact]
    public void Same_parent_handle_with_a_foreign_class_reattaches_as_parent_changed()
    {
        // The handle value was reused by an unrelated window: the class no longer matches the layout.
        var now = RaisedHealthy() with { CurrentParentClass = "Shell_TrayWnd" };

        Assert.Equal(ReattachReason.ParentChanged, ReattachDecider.Decide(RaisedAttached(), now));
    }

    [Theory]
    [InlineData(null)]          // we are above DefView: we would cover the icons
    [InlineData("WorkerW")]     // the shell's WorkerW is directly above us: it hides us
    [InlineData("SomeChild")]   // something else slipped in between
    public void Raised_not_directly_below_defview_reattaches_as_zorder_drift(string? classAbove)
    {
        var now = RaisedHealthy() with { ClassAbove = classAbove };

        Assert.Equal(ReattachReason.ZOrderDrift, ReattachDecider.Decide(RaisedAttached(), now));
    }
}
