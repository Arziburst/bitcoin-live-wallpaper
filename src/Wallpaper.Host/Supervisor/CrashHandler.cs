using System.Runtime.InteropServices;

namespace BitcoinWallpaper.Host.Supervisor;

/// <summary>
/// The wallpaper child's side of D-24: an unhandled exception is written to the log (type, message, stack) and the
/// process ends with <see cref="RestartPolicy.CrashExitCode"/>, so the supervisor restarts it.
/// </summary>
internal static partial class CrashHandler
{
    public static readonly TimeSpan CrashTestDelay = TimeSpan.FromSeconds(8);

    private static int _hostCode;
    private static volatile bool _hostReturned;

    public static void Install() => AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        Die($"an unhandled exception on thread {Environment.CurrentManagedThreadId}", e.ExceptionObject);

    /// <summary>
    /// The host's run ended with <paramref name="code"/> (Exit, the end of the session, a startup error): an exception
    /// in the cleanup after it is logged but keeps that code, so an Exit never turns into a restart.
    /// </summary>
    public static void HostReturned(int code)
    {
        _hostCode = code;
        _hostReturned = true;
    }

    /// <summary>
    /// An exception that escaped the wallpaper's main code: logged like an unhandled one; the crash code, or the
    /// host's own code when it came from the cleanup after <see cref="HostReturned"/>.
    /// </summary>
    public static int Escaped(Exception e)
    {
        if (_hostReturned)
        {
            Log.Error($"the cleanup after the wallpaper ended (code {_hostCode}) failed, not a crash: {e}");
            return _hostCode;
        }

        Log.Error($"crash (an exception escaped Main): {e}");
        return RestartPolicy.CrashExitCode;
    }

    /// <summary>--crash-test: throws an unhandled exception on a background thread after <see cref="CrashTestDelay"/>.</summary>
    public static void StartCrashTest()
    {
        Log.Warn($"crash test: an unhandled exception on a background thread in {CrashTestDelay.TotalSeconds:0} s");
        var thread = new Thread(() =>
        {
            Thread.Sleep(CrashTestDelay);
            throw new InvalidOperationException("--crash-test: a deliberate unhandled exception on a background thread");
        })
        {
            IsBackground = true,
            Name = "crash test",
        };
        thread.Start();
    }

    private static void Die(string what, object error)
    {
        var returned = _hostReturned;
        Log.Error(returned
            ? $"{what} after the wallpaper ended (code {_hostCode}), not a crash: {error}"
            : $"crash ({what}): {error}");
        Log.Close();

        // Ends now with our code; the CLR would end with 0xE0434352 after its own report.
        TerminateProcess(GetCurrentProcess(), returned ? _hostCode : RestartPolicy.CrashExitCode);
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, int exitCode);
}
