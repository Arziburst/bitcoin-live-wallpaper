using BitcoinWallpaper.Host.Settings;
using BitcoinWallpaper.Host.Supervisor;

namespace BitcoinWallpaper.Host.Tests;

// Contract 22.3 (D-24), criterion "the supervisor loop (testable with a fake child launcher and a fake clock)":
// restarts on a crash per the policy (RestartPolicyTests.cs), ends when the child exits normally, never restarts after
// the policy says stop, logs each child exit (code, its meaning), each restart (n of 3) and the final stay-down; a
// restarted child starts like the sign-in launch (persisted wallpaper state, LaunchKind.Autostart), never like a user
// start; it starts no child while the exe is gone or Windows is shutting down.
//
// API SKETCH (written before the code, #237). Namespace BitcoinWallpaper.Host.Supervisor, src/Wallpaper.Host/Supervisor/.
// All types public. Uses RestartPolicy / RestartVerdict (RestartPolicyTests.cs) and LaunchKind
// (BitcoinWallpaper.Host.Settings, SecondStartTests.cs).
//
//   // What one child start asks for. Production maps it to the exe's arguments: Launch Autostart -> "--autostart",
//   // User -> none; CrashTest -> "--crash-test"; plus the hidden child flag the supervisor uses.
//   public sealed record ChildStart(LaunchKind Launch, bool CrashTest);
//
//   // Starts the wallpaper child and waits for it. Returns its exit code (as Process.ExitCode, so high-bit codes are
//   // negative). Production: Process.Start of the same exe + WaitForExit. Test: a script.
//   public interface IChildLauncher
//   {
//       int Run(ChildStart start);
//   }
//
//   public interface ISupervisorClock
//   {
//       DateTimeOffset UtcNow { get; }
//       void Wait(TimeSpan delay);   // blocks for the delay; the fake advances UtcNow and returns at once
//   }
//
//   public sealed class Supervisor
//   {
//       // The pause before a restarted child starts: a few seconds (greater than zero, at most 30 s; the worker picks it).
//       public static readonly TimeSpan RestartDelay = ...;
//
//       // canStart: false when the exe is gone (uninstall, Velopack update) or Windows is shutting down / the session
//       // ends (GetSystemMetrics(SM_SHUTTINGDOWN)). log: one call per line, no timestamp or level (Program passes Log.Info).
//       // The policy is a new RestartPolicy, fed with clock.UtcNow at the moment the child returned.
//       public Supervisor(IChildLauncher launcher, ISupervisorClock clock, Action<string> log, Func<bool> canStart);
//
//       // Runs until the loop ends and returns the exit code of the last child (0 when no child was ever started).
//       //  1. canStart() false -> no child; log a line; return 0.
//       //  2. launcher.Run(new ChildStart(launch, crashTest)) for the first child: the supervisor's own launch kind.
//       //  3. log one line per child exit with the code as hex "0x" + 8 digits ("0xC0000005"; 0 -> "0x00000000") and
//       //     what it means (normal exit / crash / killed ...).
//       //  4. policy.OnChildExit(code, clock.UtcNow):
//       //       Exit     -> return the code; no restart, no delay.
//       //       StayDown -> log a line that says the wallpaper stays down ("staying down", with the limit: "3 restarts in
//       //                   10 minutes"); return the code.
//       //       Restart  -> log a line with "restart n of 3" (e.g. "restart 2 of 3"); clock.Wait(RestartDelay) once;
//       //                   canStart() is asked again after the wait: false -> log a line, return the code, no child;
//       //                   else launcher.Run(new ChildStart(LaunchKind.Autostart, crashTest)) - always Autostart (the
//       //                   persisted state), the same crashTest as given to Run; go to 3 with the new child.
//       public int Run(LaunchKind launch, bool crashTest);
//   }
public class SupervisorTests
{
    private const int AccessViolation = unchecked((int)0xC0000005);
    private const int StackBufferOverrun = unchecked((int)0xC0000409);
    private const int ClrUnhandled = unchecked((int)0xE0434352);

    // ---- fakes ----

    /// <summary>The events of one run in order: "run:&lt;Launch&gt;:&lt;CrashTest&gt;" and "wait:&lt;seconds&gt;".</summary>
    private sealed class Scene
    {
        public List<string> Events { get; } = new();
        public List<string> Log { get; } = new();
        public List<ChildStart> Starts { get; } = new();
        public List<TimeSpan> Waits { get; } = new();
        public DateTimeOffset Now { get; set; } = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        public bool CanStart { get; set; } = true;
        public Action? OnWait { get; set; }
    }

    private sealed class FakeClock(Scene run) : ISupervisorClock
    {
        public DateTimeOffset UtcNow => run.Now;

        public void Wait(TimeSpan delay)
        {
            run.Waits.Add(delay);
            run.Events.Add($"wait:{delay.TotalSeconds:0.###}");
            run.Now += delay;
            run.OnWait?.Invoke();
        }
    }

    /// <summary>Each child ends with the next scripted exit code after it lived the scripted time (the fake clock moves).</summary>
    private sealed class FakeLauncher(Scene run, params (int Exit, TimeSpan Lived, Action? During)[] script) : IChildLauncher
    {
        private int _next;

        public int Run(ChildStart start)
        {
            Assert.True(_next < script.Length, $"an unexpected child start #{_next + 1}: {start}");
            var (exit, lived, during) = script[_next++];
            run.Starts.Add(start);
            run.Events.Add($"run:{start.Launch}:{start.CrashTest}");
            during?.Invoke();
            run.Now += lived;
            return exit;
        }
    }

    private static (int Exit, TimeSpan Lived, Action? During) Child(int exit, double livedSeconds = 0, Action? during = null) =>
        (exit, TimeSpan.FromSeconds(livedSeconds), during);

    private static int Go(Scene run, IChildLauncher launcher, LaunchKind launch = LaunchKind.User, bool crashTest = false) =>
        new Supervisor.Supervisor(launcher, new FakeClock(run), run.Log.Add, () => run.CanStart).Run(launch, crashTest);

    private static Scene Scripted(out IChildLauncher launcher, params (int Exit, TimeSpan Lived, Action? During)[] script)
    {
        var run = new Scene();
        launcher = new FakeLauncher(run, script);
        return run;
    }

    private static bool Mentions(Scene run, params string[] words) =>
        run.Log.Any(line => words.All(w => line.Contains(w, StringComparison.OrdinalIgnoreCase)));

    // ---- a normal exit ends the loop ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(7)]
    [InlineData(unchecked((int)0x80000003))]
    public void A_normal_exit_ends_the_loop_with_that_code_and_restarts_nothing(int code)
    {
        var run = Scripted(out var launcher, Child(code));

        var result = Go(run, launcher);

        Assert.Equal(code, result);
        Assert.Single(run.Starts);
        Assert.Empty(run.Waits);
        Assert.False(Mentions(run, "restart", " of 3"), string.Join("\n", run.Log));
    }

    [Fact]
    public void A_normal_exit_is_logged_with_its_code()
    {
        var run = Scripted(out var launcher, Child(0));

        Go(run, launcher);

        Assert.True(Mentions(run, "exit", "0x00000000"), string.Join("\n", run.Log));
    }

    [Fact]
    public void Exit_code_1_taskkill_or_a_startup_error_is_not_restarted()
    {
        var run = Scripted(out var launcher, Child(1, livedSeconds: 3600));

        Assert.Equal(1, Go(run, launcher));
        Assert.Single(run.Starts);
    }

    // ---- crash restarts ----

    [Theory]
    [InlineData(unchecked((int)0xC0000005))]
    [InlineData(unchecked((int)0xC0000409))]
    [InlineData(unchecked((int)0xE0434352))]
    [InlineData(RestartPolicy.CrashExitCode)]
    public void A_crash_restarts_the_child_which_then_exits_normally(int crash)
    {
        var run = Scripted(out var launcher, Child(crash, livedSeconds: 30), Child(0, livedSeconds: 600));

        var result = Go(run, launcher);

        Assert.Equal(0, result);
        Assert.Equal(2, run.Starts.Count);
        Assert.Single(run.Waits);
    }

    [Fact]
    public void The_restart_waits_a_few_seconds_before_the_new_child_starts()
    {
        var run = Scripted(out var launcher, Child(AccessViolation), Child(0));

        Go(run, launcher);

        Assert.Equal(new[] { "run:User:False", $"wait:{Supervisor.Supervisor.RestartDelay.TotalSeconds:0.###}", "run:Autostart:False" },
            run.Events);
        Assert.True(Supervisor.Supervisor.RestartDelay > TimeSpan.Zero);
        Assert.True(Supervisor.Supervisor.RestartDelay <= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void A_restarted_child_gets_the_sign_in_launch_never_a_user_start()
    {
        var run = Scripted(out var launcher,
            Child(AccessViolation), Child(StackBufferOverrun), Child(ClrUnhandled), Child(0));

        Go(run, launcher, LaunchKind.User);

        Assert.Equal(
            new[] { LaunchKind.User, LaunchKind.Autostart, LaunchKind.Autostart, LaunchKind.Autostart },
            run.Starts.Select(s => s.Launch));
    }

    [Fact]
    public void The_first_child_gets_the_supervisors_own_launch_kind()
    {
        var user = Scripted(out var userLauncher, Child(0));
        Go(user, userLauncher, LaunchKind.User);
        Assert.Equal(LaunchKind.User, Assert.Single(user.Starts).Launch);

        var sign = Scripted(out var signLauncher, Child(0));
        Go(sign, signLauncher, LaunchKind.Autostart);
        Assert.Equal(LaunchKind.Autostart, Assert.Single(sign.Starts).Launch);
    }

    [Fact]
    public void The_crash_test_flag_is_kept_for_every_restarted_child()
    {
        var run = Scripted(out var launcher, Child(ClrUnhandled), Child(ClrUnhandled), Child(0));

        Go(run, launcher, LaunchKind.User, crashTest: true);

        Assert.Equal(3, run.Starts.Count);
        Assert.All(run.Starts, s => Assert.True(s.CrashTest));
    }

    [Fact]
    public void Without_the_crash_test_flag_no_child_gets_it()
    {
        var run = Scripted(out var launcher, Child(ClrUnhandled), Child(ClrUnhandled), Child(0));

        Go(run, launcher, LaunchKind.Autostart, crashTest: false);

        Assert.Equal(3, run.Starts.Count);
        Assert.All(run.Starts, s => Assert.False(s.CrashTest));
    }

    // ---- the limit ----

    [Fact]
    public void The_fourth_quick_crash_stays_down_after_three_restarts()
    {
        var run = Scripted(out var launcher,
            Child(AccessViolation, 20), Child(AccessViolation, 20), Child(StackBufferOverrun, 20), Child(ClrUnhandled, 20));

        var result = Go(run, launcher, LaunchKind.User, crashTest: true);

        Assert.Equal(ClrUnhandled, result);                      // the last child's code
        Assert.Equal(4, run.Starts.Count);                       // the original and 3 restarts, no 5th
        Assert.Equal(3, run.Waits.Count);                        // no wait after the last crash
        Assert.Equal("run:Autostart:True", run.Events[^1]);      // nothing happens after the last child
    }

    [Fact]
    public void Staying_down_is_final_the_loop_does_not_start_anything_after_it()
    {
        // A launcher that fails the test when started a 5th time (the script has 4 entries).
        var run = Scripted(out var launcher,
            Child(AccessViolation), Child(AccessViolation), Child(AccessViolation), Child(AccessViolation));

        var ex = Record.Exception(() => Go(run, launcher));

        Assert.Null(ex);
        Assert.Equal(4, run.Starts.Count);
    }

    [Fact]
    public void A_crash_after_the_oldest_restart_slid_out_of_the_window_restarts_again()
    {
        // Crashes at 0 s, 3 s, 6 s use the 3 restarts; the 4th child lives 11 minutes, so when it crashes the first
        // restart is out of the window: a 5th child starts.
        var run = Scripted(out var launcher,
            Child(AccessViolation), Child(AccessViolation), Child(AccessViolation),
            Child(AccessViolation, livedSeconds: 11 * 60), Child(0));

        var result = Go(run, launcher);

        Assert.Equal(0, result);
        Assert.Equal(5, run.Starts.Count);
        Assert.True(Mentions(run, "restart", "3 of 3"), string.Join("\n", run.Log));
    }

    [Fact]
    public void A_child_that_lived_long_between_crashes_still_counts_inside_ten_minutes()
    {
        // Each child lives 3 minutes: crashes at ~3, ~6, ~9 min use 3 restarts; the 4th crash at ~12 min finds
        // the first restart (~3 min) only 9 min back: still inside the window, so it stays down.
        var run = Scripted(out var launcher,
            Child(AccessViolation, 180), Child(AccessViolation, 180), Child(AccessViolation, 180), Child(AccessViolation, 180));

        var result = Go(run, launcher);

        Assert.Equal(AccessViolation, result);
        Assert.Equal(4, run.Starts.Count);
    }

    // ---- the log ----

    [Fact]
    public void Every_child_exit_is_logged_with_its_code_and_a_meaning()
    {
        var run = Scripted(out var launcher, Child(AccessViolation), Child(StackBufferOverrun), Child(ClrUnhandled), Child(0));

        Go(run, launcher);

        Assert.True(Mentions(run, "0xC0000005"), string.Join("\n", run.Log));
        Assert.True(Mentions(run, "0xC0000409"), string.Join("\n", run.Log));
        Assert.True(Mentions(run, "0xE0434352"), string.Join("\n", run.Log));
        Assert.True(Mentions(run, "0x00000000"), string.Join("\n", run.Log));
        Assert.True(Mentions(run, "crash"), string.Join("\n", run.Log));
    }

    [Fact]
    public void Each_restart_is_logged_as_n_of_3()
    {
        var run = Scripted(out var launcher, Child(AccessViolation), Child(AccessViolation), Child(AccessViolation), Child(0));

        Go(run, launcher);

        Assert.True(Mentions(run, "restart", "1 of 3"), string.Join("\n", run.Log));
        Assert.True(Mentions(run, "restart", "2 of 3"), string.Join("\n", run.Log));
        Assert.True(Mentions(run, "restart", "3 of 3"), string.Join("\n", run.Log));
        Assert.False(Mentions(run, "stay"), string.Join("\n", run.Log));
    }

    [Fact]
    public void The_stay_down_is_logged_with_the_limit()
    {
        var run = Scripted(out var launcher,
            Child(AccessViolation), Child(AccessViolation), Child(AccessViolation), Child(AccessViolation));

        Go(run, launcher);

        Assert.True(Mentions(run, "stay", "down"), string.Join("\n", run.Log));
        Assert.True(Mentions(run, "stay", "3 restarts", "10 minutes"), string.Join("\n", run.Log));
    }

    // ---- no start while the exe is gone / Windows is shutting down ----

    [Fact]
    public void No_child_starts_when_the_supervisor_may_not_start_one()
    {
        var run = Scripted(out var launcher);   // an empty script: any start fails the test
        run.CanStart = false;

        var result = Go(run, launcher);

        Assert.Equal(0, result);
        Assert.Empty(run.Starts);
        Assert.Empty(run.Waits);
        Assert.NotEmpty(run.Log);   // it says why nothing started
    }

    [Fact]
    public void No_restart_when_the_session_is_ending_by_the_time_the_child_crashed()
    {
        // The sign-out kills the child (a crash-like code), and the same moment the system flag is up.
        var run = new Scene();
        var launcher = new FakeLauncher(run, Child(AccessViolation, during: () => run.CanStart = false));

        var result = Go(run, launcher);

        Assert.Equal(AccessViolation, result);
        Assert.Single(run.Starts);
        Assert.NotEmpty(run.Log);
    }

    [Fact]
    public void No_restart_when_the_session_starts_ending_during_the_restart_delay()
    {
        var run = new Scene();
        var launcher = new FakeLauncher(run, Child(AccessViolation));
        run.OnWait = () => run.CanStart = false;

        var result = Go(run, launcher);

        Assert.Equal(AccessViolation, result);
        Assert.Single(run.Starts);   // the second child never started
        Assert.Single(run.Waits);
    }

    [Fact]
    public void No_further_restart_when_the_exe_disappears_after_a_restart()
    {
        // An update replaced the exe while the first restarted child ran; it then ended with a crash code.
        var run = new Scene();
        var launcher = new FakeLauncher(run,
            Child(AccessViolation),
            Child(AccessViolation, during: () => run.CanStart = false));

        var result = Go(run, launcher);

        Assert.Equal(AccessViolation, result);
        Assert.Equal(2, run.Starts.Count);
    }
}
