using BitcoinWallpaper.Chart.Core.Pause;

namespace BitcoinWallpaper.Chart.Core.Tests;

// Contract #16, criterion 1: Busy and RunningD3DFullScreen pause only with window evidence (a non-shell,
// non-own, non-minimised window covers >= 90 % of the active monitor) or for at most 2 minutes without it;
// PresentationMode, NotPresent, locked and disconnected sessions stay unconditional; the reason
// distinguishes the guarded case. PauseDecisionTests and PauseDebounceTests stay green unchanged:
// PauseDecision.Evaluate keeps its behaviour; the time-dependent guard is a new stateful component.
//
// API SKETCH (written before the code, #237). In src/Chart.Core/Pause/, namespace
// BitcoinWallpaper.Chart.Core.Pause. Additions only; nothing existing changes meaning.
//
//   public sealed class NotificationGuard
//   {
//       public const int EvidencePercent = 90;                // single-window coverage that corroborates
//       public static TimeSpan UncorroboratedLimit { get; }    // 2 minutes
//       public NotificationGuard(TimeProvider time);           // no timers: state changes only in Evaluate
//       public PauseVerdict Evaluate(PauseSnapshot snapshot);  // called once per poll (PlaybackMonitor does)
//   }
//
//   public enum PauseReason { ...existing values unchanged...,
//       NotificationCorroborated,     // Busy/D3D and a window covers >= 90 % of the active monitor
//       NotificationUncorroborated }  // Busy/D3D without such a window, inside the 2-minute limit
//
//   public sealed record WindowInfo(..., bool IsShell = false, nint Handle = 0);
//       // new LAST optional parameter: the HWND (0 when unknown); Win32PauseSnapshotSource fills it.
//
// Rules the tests rely on:
// - Time comes only from the TimeProvider (GetTimestamp or GetUtcNow; faked consistently here).
// - Session Locked / RemoteDisconnected: the same verdict as PauseDecision.Evaluate, whatever the
//   notification state and however long it lasts.
// - PresentationMode and NotPresent: Paused(NotificationState), unconditionally, however long it lasts.
// - AcceptsNotifications, QuietTime, App: exactly PauseDecision.Evaluate(snapshot) (the 90 % rule is not
//   applied to them).
// - Busy / RunningD3DFullScreen with an active session:
//   * Evidence = one window (the foreground or any of VisibleWindows) that is not shell (IsShell or a shell
//     class name, PauseDecision.IsShellClass), not ours, not minimised, and whose rect clipped to the
//     monitor covers >= 90 % of it (inclusive). Union of several windows is NOT evidence.
//   * With evidence: Paused(NotificationCorroborated), whatever the time.
//   * Without evidence: Paused(NotificationUncorroborated) while less than UncorroboratedLimit has passed
//     since the first poll of the run; from exactly UncorroboratedLimit on, the window rules decide:
//     PauseDecision.Evaluate(snapshot with Notification = AcceptsNotifications) (Running on a quiet desktop).
//   * A run is the polls with the same notification state and the same foreground window (identity =
//     WindowInfo.Handle; a moved or resized foreground is the same window). Once the limit has passed the
//     guard stays out until the notification state or the foreground changes; a change starts a new run with
//     the full limit. A window showing evidence and going away again does not re-arm the guard.
// Not decided and NOT tested: the reason inside the limit when several windows together cover the monitor;
// evidence appearing and disappearing inside the limit; a locked session in the middle of a run; a foreground
// with Handle 0.
public class StaleBusyGuardTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);

    /// <summary>A manual clock: time moves only when the test sets it.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => now.UtcTicks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new NotSupportedException("The guard must not use timers: its state changes only in Evaluate.");

        public void At(long milliseconds)
        {
            var next = Start.AddMilliseconds(milliseconds);
            if (next < now)
            {
                throw new InvalidOperationException("The test clock only moves forward.");
            }

            now = next;
        }
    }

    // The owner's TV (primary, 3840x2160, taskbar 48 px at the bottom) and a 5120x1440 monitor to its right.
    private static readonly PixelRect Tv = new(0, 0, 3840, 2160);
    private static readonly PixelRect Wide = new(3840, 0, 8960, 1440);
    private static readonly PixelRect VirtualScreen = new(0, 0, 8960, 2160);

    private const string Browser = "Chrome_WidgetWin_1";
    private const long TwoMinutes = 120_000;

    private static readonly WindowInfo[] ShellBaseline =
    [
        new("Progman", VirtualScreen, Handle: 0x10),
        new("WorkerW", VirtualScreen, Handle: 0x11),
        new("Shell_TrayWnd", new PixelRect(0, 2112, 3840, 2160), Handle: 0x12),
        new("Shell_SecondaryTrayWnd", new PixelRect(3840, 1392, 8960, 1440), Handle: 0x13),
        new("BitcoinWallpaper.Host", Tv, IsOurs: true, Handle: 0x14),
    ];

    /// <summary>A small, unzoomed window: what a stale Busy flag leaves as the foreground on a quiet desktop.</summary>
    private static readonly WindowInfo Editor = new("Notepad", new PixelRect(200, 200, 1800, 1200), Handle: 0x100);

    private static readonly WindowInfo OtherEditor = new("Notepad", new PixelRect(200, 200, 1800, 1200), Handle: 0x200);

    private readonly ManualClock clock = new(Start);
    private readonly NotificationGuard guard;

    public StaleBusyGuardTests() => guard = new NotificationGuard(clock);

    private static WindowInfo App(PixelRect rect, nint handle, bool zoomed = false, bool minimized = false) =>
        new(Browser, rect, IsZoomed: zoomed, IsMinimized: minimized, Handle: handle);

    private static PauseSnapshot Snap(
        WindowInfo? foreground,
        NotificationState notification,
        WindowInfo[]? others = null,
        SessionState session = SessionState.Active,
        PixelRect? monitor = null)
    {
        var visible = new List<WindowInfo>(ShellBaseline);
        if (foreground is not null)
        {
            visible.Add(foreground);
        }

        visible.AddRange(others ?? []);
        return new PauseSnapshot(monitor ?? Tv, foreground, visible, notification, session);
    }

    private static readonly PauseVerdict Running = new(PlaybackState.Running, PauseReason.None);

    private static PauseVerdict Paused(PauseReason reason) => new(PlaybackState.Paused, reason);

    private void Expect(long ms, PauseSnapshot snapshot, PauseVerdict expected)
    {
        clock.At(ms);
        var verdict = guard.Evaluate(snapshot);
        Assert.True(expected == verdict,
            $"at +{ms} ms ({snapshot.Notification}, {snapshot.Session}): expected {expected}, got {verdict}");
    }

    // ---- the constants ----

    [Fact]
    public void Limit_is_two_minutes_and_evidence_is_90_percent()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), NotificationGuard.UncorroboratedLimit);
        Assert.Equal(90, NotificationGuard.EvidencePercent);
    }

    [Fact]
    public void New_reasons_are_distinct_from_the_unconditional_notification_reason()
    {
        Assert.NotEqual(PauseReason.NotificationCorroborated, PauseReason.NotificationUncorroborated);
        Assert.NotEqual(PauseReason.NotificationState, PauseReason.NotificationCorroborated);
        Assert.NotEqual(PauseReason.NotificationState, PauseReason.NotificationUncorroborated);
    }

    // ---- the stale flag: uncorroborated Busy / D3D pauses at most 2 minutes ----

    [Theory]
    [InlineData(NotificationState.Busy)]
    [InlineData(NotificationState.RunningD3DFullScreen)]
    public void Uncorroborated_state_pauses_for_two_minutes_then_runs(NotificationState state)
    {
        var snapshot = Snap(Editor, state);
        Expect(0, snapshot, Paused(PauseReason.NotificationUncorroborated));
        Expect(500, snapshot, Paused(PauseReason.NotificationUncorroborated));
        Expect(60_000, snapshot, Paused(PauseReason.NotificationUncorroborated));
        Expect(TwoMinutes - 1, snapshot, Paused(PauseReason.NotificationUncorroborated));
        Expect(TwoMinutes, snapshot, Running);
        Expect(TwoMinutes + 500, snapshot, Running);
        Expect(3_600_000, snapshot, Running);
    }

    [Fact]
    public void Uncorroborated_busy_without_a_foreground_window_also_expires()
    {
        var snapshot = Snap(foreground: null, NotificationState.Busy);
        Expect(0, snapshot, Paused(PauseReason.NotificationUncorroborated));
        Expect(TwoMinutes, snapshot, Running);
        Expect(TwoMinutes * 10, snapshot, Running);
    }

    [Fact]
    public void The_limit_counts_from_the_first_poll_of_the_run_not_from_construction()
    {
        // Quiet desktop for ten minutes, then the flag goes stale.
        Expect(0, Snap(Editor, NotificationState.AcceptsNotifications), Running);
        Expect(600_000, Snap(Editor, NotificationState.AcceptsNotifications), Running);
        Expect(600_500, Snap(Editor, NotificationState.Busy), Paused(PauseReason.NotificationUncorroborated));
        Expect(600_500 + TwoMinutes - 1, Snap(Editor, NotificationState.Busy),
            Paused(PauseReason.NotificationUncorroborated));
        Expect(600_500 + TwoMinutes, Snap(Editor, NotificationState.Busy), Running);
    }

    [Fact]
    public void A_moved_or_resized_foreground_is_the_same_window_and_does_not_re_arm()
    {
        Expect(0, Snap(Editor, NotificationState.Busy), Paused(PauseReason.NotificationUncorroborated));
        Expect(TwoMinutes, Snap(Editor, NotificationState.Busy), Running);
        var moved = Editor with { Rect = new PixelRect(400, 300, 2400, 1500) };
        Expect(TwoMinutes + 500, Snap(moved, NotificationState.Busy), Running);
        Expect(TwoMinutes + 1000, Snap(moved, NotificationState.Busy), Running);
    }

    [Fact]
    public void After_expiry_the_window_rules_still_pause()
    {
        // Two snapped halves: neither alone is evidence, together they cover 97.8 % (PauseDecision: WindowsCover).
        var left = App(new PixelRect(0, 0, 1920, 2112), handle: 0x300);
        var right = App(new PixelRect(1920, 0, 3840, 2112), handle: 0x301);
        Expect(0, Snap(Editor, NotificationState.Busy), Paused(PauseReason.NotificationUncorroborated));
        Expect(TwoMinutes, Snap(Editor, NotificationState.Busy), Running);
        Expect(TwoMinutes + 500, Snap(Editor, NotificationState.Busy, [left, right]), Paused(PauseReason.WindowsCover));
    }

    // ---- re-arming: a change of the notification state or the foreground starts a new run ----

    [Fact]
    public void Foreground_change_starts_a_new_two_minute_run()
    {
        Expect(0, Snap(Editor, NotificationState.Busy), Paused(PauseReason.NotificationUncorroborated));
        Expect(TwoMinutes, Snap(Editor, NotificationState.Busy), Running);
        Expect(200_000, Snap(Editor, NotificationState.Busy), Running);
        Expect(200_500, Snap(OtherEditor, NotificationState.Busy), Paused(PauseReason.NotificationUncorroborated));
        Expect(200_500 + TwoMinutes - 1, Snap(OtherEditor, NotificationState.Busy),
            Paused(PauseReason.NotificationUncorroborated));
        Expect(200_500 + TwoMinutes, Snap(OtherEditor, NotificationState.Busy), Running);
    }

    [Fact]
    public void Foreground_change_inside_the_limit_restarts_the_limit()
    {
        Expect(0, Snap(Editor, NotificationState.Busy), Paused(PauseReason.NotificationUncorroborated));
        Expect(100_000, Snap(OtherEditor, NotificationState.Busy), Paused(PauseReason.NotificationUncorroborated));
        Expect(TwoMinutes, Snap(OtherEditor, NotificationState.Busy), Paused(PauseReason.NotificationUncorroborated));
        Expect(100_000 + TwoMinutes - 1, Snap(OtherEditor, NotificationState.Busy),
            Paused(PauseReason.NotificationUncorroborated));
        Expect(100_000 + TwoMinutes, Snap(OtherEditor, NotificationState.Busy), Running);
    }

    [Fact]
    public void Notification_state_change_from_busy_to_d3d_starts_a_new_run()
    {
        Expect(0, Snap(Editor, NotificationState.Busy), Paused(PauseReason.NotificationUncorroborated));
        Expect(TwoMinutes, Snap(Editor, NotificationState.Busy), Running);
        Expect(TwoMinutes + 500, Snap(Editor, NotificationState.RunningD3DFullScreen),
            Paused(PauseReason.NotificationUncorroborated));
        Expect(TwoMinutes + 500 + TwoMinutes, Snap(Editor, NotificationState.RunningD3DFullScreen), Running);
    }

    [Fact]
    public void Busy_cleared_and_set_again_starts_a_new_run()
    {
        Expect(0, Snap(Editor, NotificationState.Busy), Paused(PauseReason.NotificationUncorroborated));
        Expect(TwoMinutes, Snap(Editor, NotificationState.Busy), Running);
        Expect(TwoMinutes + 500, Snap(Editor, NotificationState.AcceptsNotifications), Running);
        Expect(TwoMinutes + 1000, Snap(Editor, NotificationState.Busy), Paused(PauseReason.NotificationUncorroborated));
        Expect(TwoMinutes + 1000 + TwoMinutes - 1, Snap(Editor, NotificationState.Busy),
            Paused(PauseReason.NotificationUncorroborated));
        Expect(TwoMinutes + 1000 + TwoMinutes, Snap(Editor, NotificationState.Busy), Running);
    }

    // ---- corroborated: window evidence keeps Busy / D3D paused however long ----

    [Theory]
    [InlineData(NotificationState.Busy)]
    [InlineData(NotificationState.RunningD3DFullScreen)]
    public void Borderless_fullscreen_foreground_corroborates_indefinitely(NotificationState state)
    {
        var game = App(Tv, handle: 0x400);
        Expect(0, Snap(game, state), Paused(PauseReason.NotificationCorroborated));
        Expect(TwoMinutes, Snap(game, state), Paused(PauseReason.NotificationCorroborated));
        Expect(3_600_000, Snap(game, state), Paused(PauseReason.NotificationCorroborated));
    }

    [Fact]
    public void Foreground_covering_exactly_90_percent_corroborates()
    {
        // 3456 x 2160 = 90.00 % of the TV: below PauseDecision's 95 %, at the guard's 90 %.
        var window = App(new PixelRect(0, 0, 3456, 2160), handle: 0x401);
        Expect(0, Snap(window, NotificationState.Busy), Paused(PauseReason.NotificationCorroborated));
        Expect(TwoMinutes * 5, Snap(window, NotificationState.Busy), Paused(PauseReason.NotificationCorroborated));
    }

    [Fact]
    public void Foreground_just_below_90_percent_does_not_corroborate()
    {
        // 3455 x 2160 = 89.97 % of the TV.
        var window = App(new PixelRect(0, 0, 3455, 2160), handle: 0x402);
        Expect(0, Snap(window, NotificationState.Busy), Paused(PauseReason.NotificationUncorroborated));
        Expect(TwoMinutes, Snap(window, NotificationState.Busy), Running);
    }

    [Fact]
    public void A_covering_window_that_is_not_the_foreground_corroborates()
    {
        // An exclusive-fullscreen game may leave a small window as the foreground.
        var game = App(Tv, handle: 0x403);
        Expect(0, Snap(Editor, NotificationState.RunningD3DFullScreen, [game]),
            Paused(PauseReason.NotificationCorroborated));
        Expect(TwoMinutes * 3, Snap(Editor, NotificationState.RunningD3DFullScreen, [game]),
            Paused(PauseReason.NotificationCorroborated));
    }

    [Fact]
    public void A_covering_foreground_missing_from_the_visible_list_still_corroborates()
    {
        var game = App(Tv, handle: 0x404);
        var snapshot = new PauseSnapshot(Tv, game, ShellBaseline, NotificationState.Busy, SessionState.Active);
        Expect(0, snapshot, Paused(PauseReason.NotificationCorroborated));
        Expect(TwoMinutes, snapshot, Paused(PauseReason.NotificationCorroborated));
    }

    [Fact]
    public void Maximised_foreground_corroborates()
    {
        var maximised = App(new PixelRect(-8, -8, 3848, 2120), handle: 0x405, zoomed: true);
        Expect(0, Snap(maximised, NotificationState.Busy), Paused(PauseReason.NotificationCorroborated));
        Expect(TwoMinutes, Snap(maximised, NotificationState.Busy), Paused(PauseReason.NotificationCorroborated));
    }

    [Fact]
    public void Corroborated_on_an_active_monitor_away_from_the_origin()
    {
        var game = App(Wide, handle: 0x406);
        Expect(0, Snap(game, NotificationState.Busy, monitor: Wide), Paused(PauseReason.NotificationCorroborated));
        Expect(TwoMinutes, Snap(game, NotificationState.Busy, monitor: Wide),
            Paused(PauseReason.NotificationCorroborated));
    }

    [Fact]
    public void Evidence_after_expiry_pauses_again_and_does_not_re_arm_when_it_goes()
    {
        var maximised = Editor with { Rect = new PixelRect(-8, -8, 3848, 2120), IsZoomed = true };
        Expect(0, Snap(Editor, NotificationState.Busy), Paused(PauseReason.NotificationUncorroborated));
        Expect(TwoMinutes, Snap(Editor, NotificationState.Busy), Running);
        Expect(TwoMinutes + 500, Snap(maximised, NotificationState.Busy), Paused(PauseReason.NotificationCorroborated));
        Expect(TwoMinutes + 1000, Snap(Editor, NotificationState.Busy), Running);
    }

    // ---- what is NOT evidence ----

    [Fact]
    public void Shell_and_own_windows_spanning_the_monitor_are_not_evidence()
    {
        // The baseline's Progman, WorkerW (class names, IsShell false) and our own window all span the TV.
        Expect(0, Snap(Editor, NotificationState.Busy), Paused(PauseReason.NotificationUncorroborated));
        Expect(TwoMinutes, Snap(Editor, NotificationState.Busy), Running);
    }

    [Fact]
    public void Window_flagged_as_shell_is_not_evidence()
    {
        var shellFlagged = new WindowInfo("Windows.UI.Core.CoreWindow", Tv, IsShell: true, Handle: 0x500);
        Expect(0, Snap(Editor, NotificationState.Busy, [shellFlagged]), Paused(PauseReason.NotificationUncorroborated));
        Expect(TwoMinutes, Snap(Editor, NotificationState.Busy, [shellFlagged]), Running);
    }

    [Fact]
    public void Shell_class_window_as_the_foreground_is_not_evidence()
    {
        var desktop = new WindowInfo("Progman", VirtualScreen, Handle: 0x10);
        Expect(0, Snap(desktop, NotificationState.Busy), Paused(PauseReason.NotificationUncorroborated));
        Expect(TwoMinutes, Snap(desktop, NotificationState.Busy), Running);
    }

    [Fact]
    public void Own_window_as_the_foreground_is_not_evidence()
    {
        var ours = new WindowInfo("BitcoinWallpaper.Host", Tv, IsOurs: true, Handle: 0x14);
        Expect(0, Snap(ours, NotificationState.Busy), Paused(PauseReason.NotificationUncorroborated));
        Expect(TwoMinutes, Snap(ours, NotificationState.Busy), Running);
    }

    [Fact]
    public void Minimised_window_reporting_a_full_rect_is_not_evidence()
    {
        var minimised = App(Tv, handle: 0x501, minimized: true);
        Expect(0, Snap(Editor, NotificationState.Busy, [minimised]), Paused(PauseReason.NotificationUncorroborated));
        Expect(TwoMinutes, Snap(Editor, NotificationState.Busy, [minimised]), Running);
    }

    [Fact]
    public void Window_covering_another_monitor_is_not_evidence()
    {
        var onWide = App(Wide, handle: 0x502);
        Expect(0, Snap(onWide, NotificationState.Busy), Paused(PauseReason.NotificationUncorroborated));
        Expect(TwoMinutes, Snap(onWide, NotificationState.Busy), Running);
    }

    [Fact]
    public void Several_windows_together_are_not_single_window_evidence()
    {
        // Three side-by-side windows (each about a third) cover 100 % together; none alone reaches 90 %.
        var a = App(new PixelRect(0, 0, 1280, 2160), handle: 0x503);
        var b = App(new PixelRect(1280, 0, 2560, 2160), handle: 0x504);
        var c = App(new PixelRect(2560, 0, 3840, 2160), handle: 0x505);
        var verdict = Evaluate(0, Snap(a, NotificationState.Busy, [b, c]));
        Assert.NotEqual(PauseReason.NotificationCorroborated, verdict.Reason);
    }

    // ---- unconditional states ----

    [Theory]
    [InlineData(NotificationState.PresentationMode)]
    [InlineData(NotificationState.NotPresent)]
    public void Presentation_mode_and_not_present_pause_unconditionally(NotificationState state)
    {
        var snapshot = Snap(Editor, state);
        Expect(0, snapshot, Paused(PauseReason.NotificationState));
        Expect(TwoMinutes, snapshot, Paused(PauseReason.NotificationState));
        Expect(3_600_000, snapshot, Paused(PauseReason.NotificationState));
    }

    [Theory]
    [InlineData(NotificationState.Busy)]
    [InlineData(NotificationState.RunningD3DFullScreen)]
    [InlineData(NotificationState.AcceptsNotifications)]
    public void Locked_session_pauses_unconditionally(NotificationState state)
    {
        var snapshot = Snap(foreground: null, state, session: SessionState.Locked);
        Expect(0, snapshot, Paused(PauseReason.SessionLocked));
        Expect(TwoMinutes, snapshot, Paused(PauseReason.SessionLocked));
        Expect(3_600_000, snapshot, Paused(PauseReason.SessionLocked));
    }

    [Theory]
    [InlineData(NotificationState.Busy)]
    [InlineData(NotificationState.AcceptsNotifications)]
    public void Remote_disconnected_session_pauses_unconditionally(NotificationState state)
    {
        var snapshot = Snap(foreground: null, state, session: SessionState.RemoteDisconnected);
        Expect(0, snapshot, Paused(PauseReason.SessionDisconnected));
        Expect(TwoMinutes, snapshot, Paused(PauseReason.SessionDisconnected));
        Expect(3_600_000, snapshot, Paused(PauseReason.SessionDisconnected));
    }

    // ---- other states defer to PauseDecision unchanged ----

    [Theory]
    [InlineData(NotificationState.AcceptsNotifications)]
    [InlineData(NotificationState.QuietTime)]
    [InlineData(NotificationState.App)]
    public void Other_states_on_a_quiet_desktop_run(NotificationState state)
    {
        Expect(0, Snap(Editor, state), Running);
        Expect(TwoMinutes, Snap(Editor, state), Running);
    }

    [Fact]
    public void The_90_percent_evidence_rule_does_not_apply_outside_busy_and_d3d()
    {
        // 92 % pauses nothing under PauseDecision's 95 % rule.
        var window = App(new PixelRect(0, 0, 3533, 2160), handle: 0x600);
        Expect(0, Snap(window, NotificationState.AcceptsNotifications), Running);
    }

    [Fact]
    public void Other_states_keep_the_window_reasons()
    {
        var maximised = App(new PixelRect(-8, -8, 3848, 2120), handle: 0x601, zoomed: true);
        Expect(0, Snap(maximised, NotificationState.AcceptsNotifications), Paused(PauseReason.Maximized));
        Expect(500, Snap(App(Tv, handle: 0x602), NotificationState.QuietTime), Paused(PauseReason.Fullscreen));
    }

    [Fact]
    public void Matches_PauseDecision_for_every_non_guarded_case()
    {
        // The guard is a layer over PauseDecision, not a replacement: same verdict wherever it does not apply.
        var cases = new[]
        {
            Snap(Editor, NotificationState.AcceptsNotifications),
            Snap(App(new PixelRect(0, 0, 3648, 2160), handle: 0x700), NotificationState.App),
            Snap(App(new PixelRect(0, 0, 1920, 2112), handle: 0x701), NotificationState.QuietTime,
                [App(new PixelRect(1920, 0, 3840, 2112), handle: 0x702)]),
            Snap(Editor, NotificationState.PresentationMode),
            Snap(Editor, NotificationState.NotPresent),
            Snap(App(Tv, handle: 0x703), NotificationState.Busy, session: SessionState.Locked),
        };

        long ms = 0;
        foreach (var snapshot in cases)
        {
            Expect(ms, snapshot, PauseDecision.Evaluate(snapshot));
            ms += TwoMinutes;
        }
    }

    private PauseVerdict Evaluate(long ms, PauseSnapshot snapshot)
    {
        clock.At(ms);
        return guard.Evaluate(snapshot);
    }
}
