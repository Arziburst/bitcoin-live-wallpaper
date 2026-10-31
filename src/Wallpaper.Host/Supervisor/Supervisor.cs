using BitcoinWallpaper.Host.Settings;

namespace BitcoinWallpaper.Host.Supervisor;

/// <summary>One child start: the launch kind it gets (the first child the supervisor's own, a restart the sign-in one).</summary>
public sealed record ChildStart(LaunchKind Launch, bool CrashTest);

/// <summary>Starts the wallpaper child and waits for it. Production: <see cref="ProcessChildLauncher"/>.</summary>
public interface IChildLauncher
{
    /// <summary>The child's exit code, as Process.ExitCode gives it (high-bit codes are negative).</summary>
    int Run(ChildStart start);
}

public interface ISupervisorClock
{
    DateTimeOffset UtcNow { get; }

    void Wait(TimeSpan delay);
}

/// <summary>
/// D-24: every normal start runs this loop in a small headless process; the wallpaper runs in its child. A crash
/// restarts the child after <see cref="RestartDelay"/> per <see cref="RestartPolicy"/>, any other exit ends the loop.
/// No child starts while <c>canStart</c> says no (the exe is gone or replaced, Windows is ending the session).
/// </summary>
public sealed class Supervisor(IChildLauncher launcher, ISupervisorClock clock, Action<string> log, Func<bool> canStart)
{
    public static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(3);

    /// <summary>Returns the last child's exit code (0 when none started).</summary>
    public int Run(LaunchKind launch, bool crashTest)
    {
        if (!canStart())
        {
            log("the wallpaper is not started: the exe is gone or Windows is ending the session");
            return 0;
        }

        var policy = new RestartPolicy();
        var start = new ChildStart(launch, crashTest);
        while (true)
        {
            var code = launcher.Run(start);
            log($"the wallpaper process exited with 0x{code:X8}: {Describe(code)}");

            var decision = policy.OnChildExit(code, clock.UtcNow);
            switch (decision.Verdict)
            {
                case RestartVerdict.Exit:
                    return code;
                case RestartVerdict.StayDown:
                    log($"{RestartPolicy.MaxRestarts} restarts in {RestartPolicy.Window.TotalMinutes:0} minutes already: " +
                        "the wallpaper is staying down until it is started again");
                    return code;
            }

            log($"the wallpaper crashed: restart {decision.RestartNumber} of {RestartPolicy.MaxRestarts} " +
                $"in {RestartDelay.TotalSeconds:0} s");
            clock.Wait(RestartDelay);
            if (!canStart())
            {
                log("no restart: the exe is gone or Windows is ending the session");
                return code;
            }

            // A restart keeps the persisted on/off state, like the sign-in launch (a persisted Off stays off).
            start = new ChildStart(LaunchKind.Autostart, crashTest);
        }
    }

    /// <summary>What an exit code of the child means, for the log.</summary>
    public static string Describe(int exitCode) => unchecked((uint)exitCode) switch
    {
        0 => "a normal exit (Exit in the tray, a second start or the end of the session)",
        1 => "ended by taskkill /F or a startup error, not a crash",
        (uint)RestartPolicy.CrashExitCode => "a crash (an unhandled exception, logged above by the wallpaper process)",
        0xE0434352 => "a crash (an unhandled .NET exception)",
        0xC0000005 => "a crash (access violation)",
        0xC00000FD => "a crash (stack overflow)",
        0xC0000409 => "a crash (stack buffer overrun or fail-fast)",
        _ when RestartPolicy.IsCrash(exitCode) => "a crash (a native error status)",
        _ => "not a crash",
    };
}
