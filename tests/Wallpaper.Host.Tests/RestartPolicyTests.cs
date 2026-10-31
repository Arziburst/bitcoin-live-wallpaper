using BitcoinWallpaper.Host.Supervisor;

namespace BitcoinWallpaper.Host.Tests;

// Contract 22.3 (D-24), criterion "restart policy (a pure public type)": a child exit is a crash only for the app's own
// crash exit code, an NTSTATUS error code 0xC0000000-0xCFFFFFFF (0xC0000005 access violation, 0xC0000409 stack
// buffer overrun) or 0xE0434352 (CLR unhandled exception); 0 (Exit, a second start), 1 (taskkill /F, a startup
// error) and every other code are not. A crash restarts the child unless 3 restarts already happened in the last
// 10 minutes (sliding window); then it stays down.
//
// API SKETCH (written before the code, #237). Namespace BitcoinWallpaper.Host.Supervisor, src/Wallpaper.Host/Supervisor/.
// All types public (the test project has no InternalsVisibleTo). Exit codes arrive as int, as Process.ExitCode gives
// them: the high-bit codes are negative (0xC0000005 is unchecked((int)0xC0000005)).
//
//   public enum RestartVerdict
//   {
//       Exit,       // not a crash: the supervisor ends with the child's exit code
//       Restart,    // a crash with room left in the window: start the child again
//       StayDown,   // a crash, but 3 restarts already happened in the last 10 minutes: no restart
//   }
//
//   // RestartNumber: for Restart, the number of restarts in the window counting this one (1, 2 or 3 - "restart n of 3");
//   // 0 for Exit and StayDown.
//   public sealed record RestartDecision(RestartVerdict Verdict, int RestartNumber);
//
//   // One instance per supervisor run; it remembers the times of the restarts it granted.
//   public sealed class RestartPolicy
//   {
//       // The exit code the wallpaper child ends with after it logged an unhandled exception. Any value that IsCrash
//       // accepts and that is not 0 or 1 (the worker picks it).
//       public const int CrashExitCode = ...;
//       public const int MaxRestarts = 3;                                    // per window
//       public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);   // sliding
//
//       // Pure: true for CrashExitCode, 0xC0000000..0xCFFFFFFF inclusive and 0xE0434352; false for anything else.
//       public static bool IsCrash(int exitCode);
//
//       // The child exited with exitCode at time `now` (times only grow from call to call).
//       //  - not a crash                      -> (Exit, 0), nothing is remembered
//       //  - a crash, fewer than MaxRestarts restarts granted with now - t < Window (the oldest one that is Window
//       //    old or older no longer counts) -> (Restart, count including this one); `now` is remembered
//       //  - a crash, MaxRestarts restarts still inside the window -> (StayDown, 0)
//       public RestartDecision OnChildExit(int exitCode, DateTimeOffset now);
//   }
public class RestartPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private const int AccessViolation = unchecked((int)0xC0000005);
    private const int StackBufferOverrun = unchecked((int)0xC0000409);
    private const int ClrUnhandled = unchecked((int)0xE0434352);

    // ---- classification ----

    [Fact]
    public void The_apps_own_crash_exit_code_is_a_crash_and_is_not_one_of_the_plain_codes()
    {
        Assert.True(RestartPolicy.IsCrash(RestartPolicy.CrashExitCode));
        Assert.NotEqual(0, RestartPolicy.CrashExitCode);
        Assert.NotEqual(1, RestartPolicy.CrashExitCode);
    }

    [Theory]
    [InlineData(unchecked((int)0xC0000005))]   // access violation
    [InlineData(unchecked((int)0xC0000409))]   // stack buffer overrun
    [InlineData(unchecked((int)0xC0000000))]   // lower bound of the NTSTATUS error range
    [InlineData(unchecked((int)0xCFFFFFFF))]   // upper bound
    [InlineData(unchecked((int)0xC000001D))]   // illegal instruction
    [InlineData(unchecked((int)0xE0434352))]   // CLR unhandled exception
    public void Crash_codes(int exitCode) => Assert.True(RestartPolicy.IsCrash(exitCode));

    [Theory]
    [InlineData(0)]                            // Exit, a second start
    [InlineData(1)]                            // taskkill /F, a startup error
    [InlineData(2)]
    [InlineData(-1)]                           // 0xFFFFFFFF
    [InlineData(unchecked((int)0xD0000000))]   // just above the range
    [InlineData(unchecked((int)0xBFFFFFFF))]   // just below the range
    [InlineData(unchecked((int)0x80000003))]   // breakpoint (a warning-class status, not an error)
    [InlineData(unchecked((int)0xE0434353))]   // next to the CLR code
    [InlineData(unchecked((int)0xE0434351))]
    [InlineData(unchecked((int)0xE0000000))]
    [InlineData(int.MaxValue)]
    [InlineData(255)]
    [InlineData(-2)]
    public void Not_crash_codes(int exitCode) => Assert.False(RestartPolicy.IsCrash(exitCode));

    // ---- decisions ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(unchecked((int)0x80000003))]
    public void A_non_crash_exit_never_restarts_and_is_not_counted(int exitCode)
    {
        var policy = new RestartPolicy();

        var decision = policy.OnChildExit(exitCode, T0);

        Assert.Equal(RestartVerdict.Exit, decision.Verdict);
        Assert.Equal(0, decision.RestartNumber);

        // It did not use up any of the 3 restarts.
        for (var n = 1; n <= RestartPolicy.MaxRestarts; n++)
        {
            Assert.Equal(new RestartDecision(RestartVerdict.Restart, n),
                policy.OnChildExit(AccessViolation, T0.AddSeconds(n)));
        }
    }

    [Fact]
    public void A_first_crash_restarts_as_restart_1()
    {
        var decision = new RestartPolicy().OnChildExit(AccessViolation, T0);

        Assert.Equal(RestartVerdict.Restart, decision.Verdict);
        Assert.Equal(1, decision.RestartNumber);
    }

    [Theory]
    [InlineData(unchecked((int)0xC0000005))]
    [InlineData(unchecked((int)0xC0000409))]
    [InlineData(unchecked((int)0xE0434352))]
    public void Each_crash_kind_restarts(int exitCode)
    {
        Assert.Equal(RestartVerdict.Restart, new RestartPolicy().OnChildExit(exitCode, T0).Verdict);
    }

    [Fact]
    public void The_own_crash_exit_code_restarts()
    {
        Assert.Equal(RestartVerdict.Restart, new RestartPolicy().OnChildExit(RestartPolicy.CrashExitCode, T0).Verdict);
    }

    [Fact]
    public void Three_crashes_restart_and_the_fourth_inside_ten_minutes_stays_down()
    {
        var policy = new RestartPolicy();

        Assert.Equal(new RestartDecision(RestartVerdict.Restart, 1), policy.OnChildExit(AccessViolation, T0));
        Assert.Equal(new RestartDecision(RestartVerdict.Restart, 2), policy.OnChildExit(StackBufferOverrun, T0.AddMinutes(1)));
        Assert.Equal(new RestartDecision(RestartVerdict.Restart, 3), policy.OnChildExit(ClrUnhandled, T0.AddMinutes(2)));

        var fourth = policy.OnChildExit(AccessViolation, T0.AddMinutes(3));

        Assert.Equal(RestartVerdict.StayDown, fourth.Verdict);
        Assert.Equal(0, fourth.RestartNumber);
    }

    [Fact]
    public void The_fourth_crash_stays_down_right_up_to_the_end_of_the_window()
    {
        var policy = new RestartPolicy();
        policy.OnChildExit(AccessViolation, T0);
        policy.OnChildExit(AccessViolation, T0.AddMinutes(1));
        policy.OnChildExit(AccessViolation, T0.AddMinutes(2));

        // 9 min 59 s after the first restart: it is still inside the window.
        var decision = policy.OnChildExit(AccessViolation, T0.AddMinutes(9).AddSeconds(59));

        Assert.Equal(RestartVerdict.StayDown, decision.Verdict);
    }

    [Fact]
    public void A_crash_after_the_oldest_restart_left_the_window_restarts_again()
    {
        var policy = new RestartPolicy();
        policy.OnChildExit(AccessViolation, T0);
        policy.OnChildExit(AccessViolation, T0.AddMinutes(1));
        policy.OnChildExit(AccessViolation, T0.AddMinutes(2));

        // 10 min 30 s: the restart at T0 slid out, the ones at +1 and +2 min are still in -> this is restart 3 of 3.
        var decision = policy.OnChildExit(AccessViolation, T0.AddMinutes(10).AddSeconds(30));

        Assert.Equal(new RestartDecision(RestartVerdict.Restart, 3), decision);
    }

    [Fact]
    public void After_the_window_slid_the_next_crash_in_it_stays_down_again()
    {
        var policy = new RestartPolicy();
        policy.OnChildExit(AccessViolation, T0);
        policy.OnChildExit(AccessViolation, T0.AddMinutes(1));
        policy.OnChildExit(AccessViolation, T0.AddMinutes(2));
        Assert.Equal(RestartVerdict.Restart, policy.OnChildExit(AccessViolation, T0.AddMinutes(10).AddSeconds(30)).Verdict);

        // Restarts now at +1, +2, +10:30: three inside the window.
        var decision = policy.OnChildExit(AccessViolation, T0.AddMinutes(10).AddSeconds(45));

        Assert.Equal(RestartVerdict.StayDown, decision.Verdict);
    }

    [Fact]
    public void A_crash_long_after_everything_restarts_as_restart_1_again()
    {
        var policy = new RestartPolicy();
        policy.OnChildExit(AccessViolation, T0);
        policy.OnChildExit(AccessViolation, T0.AddMinutes(1));
        policy.OnChildExit(AccessViolation, T0.AddMinutes(2));

        var decision = policy.OnChildExit(ClrUnhandled, T0.AddHours(2));

        Assert.Equal(new RestartDecision(RestartVerdict.Restart, 1), decision);
    }

    [Fact]
    public void A_non_crash_after_the_limit_still_just_exits()
    {
        var policy = new RestartPolicy();
        policy.OnChildExit(AccessViolation, T0);
        policy.OnChildExit(AccessViolation, T0.AddSeconds(10));
        policy.OnChildExit(AccessViolation, T0.AddSeconds(20));

        Assert.Equal(RestartVerdict.Exit, policy.OnChildExit(0, T0.AddSeconds(30)).Verdict);
    }

    [Fact]
    public void Two_policies_do_not_share_state()
    {
        var a = new RestartPolicy();
        a.OnChildExit(AccessViolation, T0);
        a.OnChildExit(AccessViolation, T0.AddSeconds(1));
        a.OnChildExit(AccessViolation, T0.AddSeconds(2));

        Assert.Equal(new RestartDecision(RestartVerdict.Restart, 1), new RestartPolicy().OnChildExit(AccessViolation, T0.AddSeconds(3)));
    }

    [Fact]
    public void The_limits_are_three_restarts_in_ten_minutes()
    {
        Assert.Equal(3, RestartPolicy.MaxRestarts);
        Assert.Equal(TimeSpan.FromMinutes(10), RestartPolicy.Window);
    }
}
