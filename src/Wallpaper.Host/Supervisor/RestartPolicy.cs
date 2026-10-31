namespace BitcoinWallpaper.Host.Supervisor;

public enum RestartVerdict
{
    /// <summary>Not a crash: the supervisor ends with the child's exit code.</summary>
    Exit,

    /// <summary>A crash with room left in the window: the child starts again.</summary>
    Restart,

    /// <summary>A crash, but <see cref="RestartPolicy.MaxRestarts"/> restarts already happened in the window.</summary>
    StayDown,
}

/// <summary><paramref name="RestartNumber"/>: for a restart, its number in the window (1 to 3); 0 otherwise.</summary>
public sealed record RestartDecision(RestartVerdict Verdict, int RestartNumber);

/// <summary>
/// D-24: which child exits are crashes, and whether a crash restarts the child (at most 3 restarts in a sliding
/// 10 minutes). One instance per supervisor run.
/// </summary>
public sealed class RestartPolicy
{
    /// <summary>The wallpaper child ends with this code after it logged an unhandled exception (EX_SOFTWARE).</summary>
    public const int CrashExitCode = 70;

    public const int MaxRestarts = 3;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>The CLR's exit code for an unhandled exception it ended the process for.</summary>
    private const uint ClrUnhandledException = 0xE0434352;

    private readonly Queue<DateTimeOffset> _restarts = new();

    /// <summary>
    /// Our own crash code, an NTSTATUS error (0xC0000000-0xCFFFFFFF: access violation, stack overrun ...) or the CLR's
    /// unhandled exception. 0 (Exit, a second start), 1 (taskkill /F, a startup error) and any other code are not.
    /// </summary>
    public static bool IsCrash(int exitCode)
    {
        var code = unchecked((uint)exitCode);
        return exitCode == CrashExitCode || code is >= 0xC0000000 and <= 0xCFFFFFFF || code == ClrUnhandledException;
    }

    /// <summary>The child exited with <paramref name="exitCode"/> at <paramref name="now"/> (times only grow).</summary>
    public RestartDecision OnChildExit(int exitCode, DateTimeOffset now)
    {
        if (!IsCrash(exitCode))
        {
            return new RestartDecision(RestartVerdict.Exit, 0);
        }

        while (_restarts.Count > 0 && now - _restarts.Peek() >= Window)
        {
            _restarts.Dequeue();
        }

        if (_restarts.Count >= MaxRestarts)
        {
            return new RestartDecision(RestartVerdict.StayDown, 0);
        }

        _restarts.Enqueue(now);
        return new RestartDecision(RestartVerdict.Restart, _restarts.Count);
    }
}
