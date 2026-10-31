using BitcoinWallpaper.Chart.Core.Pause;

namespace BitcoinWallpaper.Chart.Core.Tests;

// Contract #7, criterion 1: PauseDecision.Evaluate (pure, src/Chart.Core/Pause) returns Paused when the
// foreground window is maximised on the active monitor, covers >= 95 % of it, the visible windows together
// cover >= 95 % of it, QUNS is RUNNING_D3D_FULL_SCREEN / BUSY / PRESENTATION_MODE, the session is locked or
// remote-disconnected; Running when the foreground is the desktop/shell, our own window, the taskbar, or a
// window on another monitor; borderless fullscreen counts as fullscreen.
//
// API SKETCH (written before the code, #237). The worker implements exactly these public types in
// src/Chart.Core/Pause/, namespace BitcoinWallpaper.Chart.Core.Pause. No Win32 types: the host
// (src/Wallpaper.Host/Playback/PlaybackMonitor) fills the snapshot.
//
//   public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom);
//       // Win32 RECT semantics, virtual-screen pixels: Right/Bottom exclusive; coordinates may be negative.
//
//   public enum NotificationState            // mirrors QUERY_USER_NOTIFICATION_STATE, same numeric values
//   { NotPresent = 1, Busy = 2, RunningD3DFullScreen = 3, PresentationMode = 4,
//     AcceptsNotifications = 5, QuietTime = 6, App = 7 }
//
//   public enum SessionState { Active, Locked, RemoteDisconnected }
//
//   public sealed record WindowInfo(string ClassName, PixelRect Rect, bool IsZoomed = false,
//       bool IsMinimized = false, bool IsOurs = false, bool IsShell = false);
//
//   public sealed record PauseSnapshot(
//       PixelRect Monitor,                        // the active monitor: the one the wallpaper is drawn on
//       WindowInfo? Foreground,                   // null when GetForegroundWindow returns NULL
//       IReadOnlyList<WindowInfo> VisibleWindows, // visible top-level windows on any monitor; may or may
//                                                 // not contain the foreground window
//       NotificationState Notification,
//       SessionState Session);
//
//   public enum PlaybackState { Running, Paused }
//
//   public enum PauseReason { None, SessionLocked, SessionDisconnected, NotificationState, Maximized,
//       Fullscreen, ForegroundCovers, WindowsCover }
//
//   public readonly record struct PauseVerdict(PlaybackState State, PauseReason Reason);
//
//   public static class PauseDecision { public static PauseVerdict Evaluate(PauseSnapshot snapshot); }
//
// Rules the tests rely on:
// - Running verdicts carry PauseReason.None.
// - A window is "shell" when IsShell is true OR its ClassName is (ordinal) "Progman", "WorkerW",
//   "Shell_TrayWnd" or "Shell_SecondaryTrayWnd". Shell windows, our own windows (IsOurs) and minimised
//   windows (IsMinimized) never count: not as the foreground, not in the coverage of VisibleWindows,
//   whatever rect they report.
// - Coverage = area of (rect ∩ Monitor) / area of Monitor; ">= 95 %" is inclusive (exactly 95 % pauses).
//   "Together" means the area of the UNION of the counting windows (foreground included) clipped to the
//   monitor, not the sum of their areas.
// - A zoomed foreground counts as maximised on the monitor that holds most of it (the ~8 px border a
//   maximised window spills onto the neighbouring monitor does not count).
// - Reason precedence when several apply: SessionLocked / SessionDisconnected, then NotificationState,
//   then Maximized, Fullscreen (the foreground covers 100 % of the monitor), ForegroundCovers
//   (>= 95 %), WindowsCover.
// - A locked or remote-disconnected session pauses whatever the foreground is.
// Not decided by the contract and NOT tested: a shell/own foreground together with QUNS Busy/D3D or with
// other windows covering the monitor; QUNS NotPresent / QuietTime / App.
public class PauseDecisionTests
{
    // The owner's TV (primary, 3840x2160, taskbar 48 px at the bottom) and a 5120x1440 monitor to its right.
    private static readonly PixelRect Tv = new(0, 0, 3840, 2160);
    private static readonly PixelRect Wide = new(3840, 0, 8960, 1440);
    private static readonly PixelRect VirtualScreen = new(0, 0, 8960, 2160);

    private const string Browser = "Chrome_WidgetWin_1";

    // What EnumWindows always reports as visible on a quiet desktop: the shell and our own wallpaper window.
    // Every one of them spans the whole monitor; none may count as covering it.
    private static readonly WindowInfo[] ShellBaseline =
    [
        new("Progman", VirtualScreen),
        new("WorkerW", VirtualScreen),
        new("Shell_TrayWnd", new PixelRect(0, 2112, 3840, 2160)),
        new("Shell_SecondaryTrayWnd", new PixelRect(3840, 1392, 8960, 1440)),
        new("BitcoinWallpaper.Host", Tv, IsOurs: true),
    ];

    private static WindowInfo App(PixelRect rect, bool zoomed = false, bool minimized = false) =>
        new(Browser, rect, IsZoomed: zoomed, IsMinimized: minimized);

    /// <summary>A snapshot with the foreground included in the visible windows, plus the shell baseline.</summary>
    private static PauseSnapshot Snap(
        WindowInfo? foreground,
        WindowInfo[]? others = null,
        PixelRect? monitor = null,
        NotificationState notification = NotificationState.AcceptsNotifications,
        SessionState session = SessionState.Active)
    {
        var visible = new List<WindowInfo>(ShellBaseline);
        if (foreground is not null)
        {
            visible.Add(foreground);
        }

        visible.AddRange(others ?? []);
        return new PauseSnapshot(monitor ?? Tv, foreground, visible, notification, session);
    }

    private static void AssertPaused(PauseReason reason, PauseSnapshot snapshot)
    {
        var verdict = PauseDecision.Evaluate(snapshot);
        Assert.Equal(new PauseVerdict(PlaybackState.Paused, reason), verdict);
    }

    private static void AssertRunning(PauseSnapshot snapshot)
    {
        var verdict = PauseDecision.Evaluate(snapshot);
        Assert.Equal(new PauseVerdict(PlaybackState.Running, PauseReason.None), verdict);
    }

    // ---- the enum mirrors the Win32 values, so the host can cast SHQueryUserNotificationState's result ----

    [Fact]
    public void NotificationState_values_mirror_QUERY_USER_NOTIFICATION_STATE()
    {
        Assert.Equal(1, (int)NotificationState.NotPresent);
        Assert.Equal(2, (int)NotificationState.Busy);
        Assert.Equal(3, (int)NotificationState.RunningD3DFullScreen);
        Assert.Equal(4, (int)NotificationState.PresentationMode);
        Assert.Equal(5, (int)NotificationState.AcceptsNotifications);
        Assert.Equal(6, (int)NotificationState.QuietTime);
        Assert.Equal(7, (int)NotificationState.App);
    }

    // ---- Running ----

    [Fact]
    public void Quiet_desktop_with_only_shell_and_our_window_visible_runs()
    {
        AssertRunning(Snap(foreground: null));
    }

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    public void Shell_class_foreground_runs_even_if_its_rect_spans_the_monitor(string className)
    {
        // IsShell deliberately false: the class name alone identifies the desktop and the taskbar.
        AssertRunning(Snap(new WindowInfo(className, Tv)));
    }

    [Fact]
    public void Foreground_flagged_as_shell_runs()
    {
        AssertRunning(Snap(new WindowInfo("Windows.UI.Core.CoreWindow", Tv, IsShell: true)));
    }

    [Fact]
    public void Our_own_window_as_foreground_runs()
    {
        AssertRunning(Snap(new WindowInfo("BitcoinWallpaper.Host", Tv, IsOurs: true)));
    }

    [Fact]
    public void Foreground_maximised_on_another_monitor_runs_even_though_its_border_spills_onto_the_active_one()
    {
        // Maximised on Wide: Windows extends the rect 8 px past the work area, i.e. 8 px into the TV.
        var maximisedOnWide = App(new PixelRect(3832, -8, 8968, 1400), zoomed: true);
        AssertRunning(Snap(maximisedOnWide));
    }

    [Fact]
    public void Borderless_fullscreen_on_another_monitor_runs()
    {
        AssertRunning(Snap(App(Wide)));
    }

    [Fact]
    public void Minimised_foreground_is_ignored_whatever_rect_it_reports()
    {
        AssertRunning(Snap(App(Tv, minimized: true)));
    }

    [Fact]
    public void Minimised_visible_windows_are_ignored_whatever_rect_they_report()
    {
        var foreground = App(new PixelRect(100, 100, 1100, 900));
        var minimised = new[]
        {
            App(new PixelRect(-8, -8, 3848, 2120), minimized: true),
            App(Tv, minimized: true),
        };
        AssertRunning(Snap(foreground, minimised));
    }

    [Fact]
    public void Foreground_covering_94_percent_runs()
    {
        // 3840 x 2030 = 93.98 % of the TV.
        AssertRunning(Snap(App(new PixelRect(0, 0, 3840, 2030))));
    }

    [Fact]
    public void Foreground_just_below_95_percent_runs()
    {
        // 3647 x 2160 = 94.97 % of the TV.
        AssertRunning(Snap(App(new PixelRect(0, 0, 3647, 2160))));
    }

    [Fact]
    public void Overlapping_windows_whose_areas_sum_past_95_percent_but_whose_union_is_below_run()
    {
        // Each 62.5 % of the TV (sum 125 %), union 0..3600 = 93.75 %.
        var foreground = App(new PixelRect(0, 0, 2400, 2160));
        var other = App(new PixelRect(1200, 0, 3600, 2160));
        AssertRunning(Snap(foreground, [other]));
    }

    [Fact]
    public void Windows_framing_an_uncovered_hole_of_6_percent_run()
    {
        // Four windows around a 960 x 540 hole in the middle (6.25 % uncovered); their bounding box is the
        // whole TV, so a bounding-box shortcut would wrongly pause.
        var top = App(new PixelRect(0, 0, 3840, 810));
        var bottom = App(new PixelRect(0, 1350, 3840, 2160));
        var left = App(new PixelRect(0, 810, 1440, 1350));
        var right = App(new PixelRect(2400, 810, 3840, 1350));
        AssertRunning(Snap(top, [bottom, left, right]));
    }

    [Fact]
    public void Windows_that_cover_only_another_monitor_run()
    {
        var foreground = App(new PixelRect(3840, 0, 6400, 1440));
        var other = App(new PixelRect(6400, 0, 8960, 1440));
        AssertRunning(Snap(foreground, [other]));
    }

    [Fact]
    public void Accepts_notifications_defers_to_the_windows_running_on_a_quiet_desktop()
    {
        AssertRunning(Snap(App(new PixelRect(200, 200, 1800, 1200)),
            notification: NotificationState.AcceptsNotifications));
    }

    // ---- Paused: the foreground ----

    [Fact]
    public void Foreground_maximised_on_the_active_monitor_pauses()
    {
        AssertPaused(PauseReason.Maximized, Snap(App(new PixelRect(-8, -8, 3848, 2120), zoomed: true)));
    }

    [Fact]
    public void Maximised_foreground_pauses_even_when_an_appbar_leaves_it_under_95_percent()
    {
        // A docked toolbar shrinks the work area: the maximised window covers 3840 x 1700 = 78.7 %.
        AssertPaused(PauseReason.Maximized, Snap(App(new PixelRect(-8, -8, 3848, 1700), zoomed: true)));
    }

    [Fact]
    public void Foreground_maximised_on_an_active_monitor_away_from_the_origin_pauses()
    {
        var maximisedOnWide = App(new PixelRect(3832, -8, 8968, 1400), zoomed: true);
        AssertPaused(PauseReason.Maximized, Snap(maximisedOnWide, monitor: Wide));
    }

    [Fact]
    public void Borderless_fullscreen_foreground_pauses()
    {
        // Rect equals the monitor, not zoomed: a borderless fullscreen game or video player.
        AssertPaused(PauseReason.Fullscreen, Snap(App(Tv)));
    }

    [Fact]
    public void Borderless_fullscreen_foreground_larger_than_the_monitor_pauses()
    {
        AssertPaused(PauseReason.Fullscreen, Snap(App(new PixelRect(-1, -1, 3841, 2161))));
    }

    [Fact]
    public void Borderless_fullscreen_on_an_active_monitor_away_from_the_origin_pauses()
    {
        AssertPaused(PauseReason.Fullscreen, Snap(App(Wide), monitor: Wide));
    }

    [Fact]
    public void Foreground_covering_exactly_95_percent_pauses()
    {
        // 3648 x 2160 = 95.00 % of the TV.
        AssertPaused(PauseReason.ForegroundCovers, Snap(App(new PixelRect(0, 0, 3648, 2160))));
    }

    [Fact]
    public void Unzoomed_foreground_sized_like_a_maximised_window_pauses()
    {
        // Covers the work area: 3840 x 2112 = 97.8 %, not zoomed.
        AssertPaused(PauseReason.ForegroundCovers, Snap(App(new PixelRect(-8, -8, 3848, 2120))));
    }

    [Fact]
    public void Foreground_on_an_active_monitor_away_from_the_origin_covering_95_percent_pauses()
    {
        // 4864 x 1440 = 95.00 % of Wide.
        AssertPaused(PauseReason.ForegroundCovers,
            Snap(App(new PixelRect(3840, 0, 8704, 1440)), monitor: Wide));
    }

    [Fact]
    public void Accepts_notifications_defers_to_the_windows_pausing_behind_a_maximised_window()
    {
        AssertPaused(PauseReason.Maximized, Snap(App(new PixelRect(-8, -8, 3848, 2120), zoomed: true),
            notification: NotificationState.AcceptsNotifications));
    }

    // ---- Paused: the visible windows together ----

    [Fact]
    public void Two_snapped_halves_together_pause()
    {
        // Each 50 % of the work area; together 3840 x 2112 = 97.8 %.
        var left = App(new PixelRect(0, 0, 1920, 2112));
        var right = App(new PixelRect(1920, 0, 3840, 2112));
        AssertPaused(PauseReason.WindowsCover, Snap(left, [right]));
    }

    [Fact]
    public void Overlapping_windows_whose_union_is_exactly_95_percent_pause()
    {
        // Union 0..3648 = 95.00 %; neither alone reaches it.
        var foreground = App(new PixelRect(0, 0, 2000, 2160));
        var other = App(new PixelRect(1648, 0, 3648, 2160));
        AssertPaused(PauseReason.WindowsCover, Snap(foreground, [other]));
    }

    [Fact]
    public void Windows_leaving_a_small_hole_pause()
    {
        // A 190 x 1080 gap: 2.47 % uncovered.
        var top = App(new PixelRect(0, 0, 3840, 1080));
        var bottomLeft = App(new PixelRect(0, 1080, 1800, 2160));
        var bottomRight = App(new PixelRect(1990, 1080, 3840, 2160));
        AssertPaused(PauseReason.WindowsCover, Snap(bottomLeft, [top, bottomRight]));
    }

    [Fact]
    public void Windows_straddling_the_monitor_edge_count_only_their_part_on_the_active_monitor()
    {
        // Half on Wide, half on the TV; plus the other half of the TV.
        var straddling = App(new PixelRect(1920, 0, 5760, 2160));
        var left = App(new PixelRect(0, 0, 1920, 2160));
        AssertPaused(PauseReason.WindowsCover, Snap(straddling, [left]));
    }

    [Fact]
    public void Foreground_missing_from_the_visible_list_still_counts_toward_coverage()
    {
        var foreground = App(new PixelRect(0, 0, 1920, 2160));
        var other = App(new PixelRect(1920, 0, 3840, 2160));
        var visible = new List<WindowInfo>(ShellBaseline) { other };
        var snapshot = new PauseSnapshot(Tv, foreground, visible, NotificationState.AcceptsNotifications,
            SessionState.Active);
        AssertPaused(PauseReason.WindowsCover, snapshot);
    }

    [Fact]
    public void Windows_covering_the_monitor_pause_without_a_foreground_window()
    {
        var left = App(new PixelRect(0, 0, 1920, 2160));
        var right = App(new PixelRect(1920, 0, 3840, 2160));
        AssertPaused(PauseReason.WindowsCover, Snap(foreground: null, [left, right]));
    }

    // ---- Paused: QUNS ----

    [Theory]
    [InlineData(NotificationState.RunningD3DFullScreen)]
    [InlineData(NotificationState.Busy)]
    [InlineData(NotificationState.PresentationMode)]
    public void Fullscreen_notification_states_pause_whatever_the_windows(NotificationState state)
    {
        // An exclusive-fullscreen game may leave a small, unzoomed window as the foreground.
        AssertPaused(PauseReason.NotificationState,
            Snap(App(new PixelRect(200, 200, 1800, 1200)), notification: state));
    }

    // ---- Paused: the session ----

    [Fact]
    public void Locked_session_pauses()
    {
        AssertPaused(PauseReason.SessionLocked, Snap(foreground: null, session: SessionState.Locked));
    }

    [Fact]
    public void Remote_disconnected_session_pauses()
    {
        AssertPaused(PauseReason.SessionDisconnected,
            Snap(foreground: null, session: SessionState.RemoteDisconnected));
    }

    [Fact]
    public void Locked_session_pauses_even_with_the_desktop_as_foreground()
    {
        AssertPaused(PauseReason.SessionLocked, Snap(new WindowInfo("Progman", VirtualScreen),
            session: SessionState.Locked));
    }

    [Fact]
    public void Session_reason_wins_over_a_fullscreen_notification_state()
    {
        AssertPaused(PauseReason.SessionLocked, Snap(App(Tv),
            notification: NotificationState.RunningD3DFullScreen, session: SessionState.Locked));
    }
}
